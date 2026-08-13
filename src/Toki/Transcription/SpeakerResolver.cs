using System.Diagnostics;
using System.IO;

namespace Toki.Transcription;

/// <summary>
/// Turns "Them" into people, for one meeting, and then forgets them.
///
/// This is the whole T8 feature end to end: split the remote audio into voices, ask
/// <see cref="SpeakerNamer"/> who those voices are, and write the answer onto the saved transcript.
/// It is deliberately TRANSIENT - nothing about anybody's voice survives the call. The clusters exist
/// for the length of this method; what persists is text, which the user can edit or delete like any
/// other text. Reopen the same meeting tomorrow and Toki has no idea whose voice it heard.
///
/// Three design points worth keeping:
///
///   • <b>Only them.wav.</b> The microphone is its own stream, so the user is already separated more
///     reliably than any model could manage, and half the audio never needs looking at.
///   • <b>Sub-segment before embedding.</b> A merged turn can run to 700 characters, and embedding one
///     of those whole would blend two voices into a fingerprint that matches neither. Each turn is cut
///     into <see cref="WindowSec"/> windows, every window is embedded, and the turn takes the majority
///     verdict of its own windows.
///   • <b>Under-split beats over-split.</b> Merging two quiet people into one label is today's behaviour
///     and reads as slightly coarse. Splitting one person into two invents a colleague who was never in
///     the room. When the attendee count is known it is used as the answer; when it isn't, the threshold
///     leans conservative and the user can set the count by hand.
/// </summary>
public static class SpeakerResolver
{
    /// <summary>Embedding window. Short enough not to straddle a speaker change, long enough to
    /// fingerprint: research puts ~2s at roughly 1.7x the error rate of a full 5s sample, and below 1s
    /// it falls apart entirely.</summary>
    public const float WindowSec = 3.0f;

    /// <summary>How far each window advances. Overlapping means a short turn still yields more than one
    /// opinion.</summary>
    public const float HopSec = 1.5f;

    /// <summary>The result: a tag per transcript segment (by index), plus what happened.</summary>
    public sealed record Result(
        IReadOnlyList<string?> Tags, int Speakers, IReadOnlyList<SpeakerLabel> Named, TimeSpan Took, string? Skipped = null)
    {
        public bool Ran => Skipped is null;
    }

    /// <summary>
    /// Resolve the speakers for one meeting's transcript.
    /// </summary>
    /// <param name="transcript">The SAVED transcript, in order. Tags come back aligned to it by index.</param>
    /// <param name="themWav">Path to them.wav.</param>
    /// <param name="roster">Names known to be in the room (calendar attendees, pinned names).</param>
    /// <param name="ownName">The user's own name, never handed to a remote voice.</param>
    /// <param name="expectedSpeakers">Attendee count when known, else -1 to estimate.</param>
    public static Result Resolve(
        IReadOnlyList<TranscriptSegment> transcript,
        string themWav,
        IEnumerable<string>? roster = null,
        string? ownName = null,
        int expectedSpeakers = -1,
        CancellationToken ct = default)
    {
        var none = new string?[transcript.Count];

        if (!SpeakerDiariser.Available) return new Result(none, 0, [], TimeSpan.Zero, "diarisation models not installed");
        if (string.IsNullOrWhiteSpace(themWav) || !File.Exists(themWav)) return new Result(none, 0, [], TimeSpan.Zero, "no them.wav");

        // Which transcript lines came from the remote stream, and where they sit in it.
        var themIdx = new List<int>();
        for (var i = 0; i < transcript.Count; i++)
            if (transcript[i].Speaker == Speaker.Them) themIdx.Add(i);
        if (themIdx.Count < 2) return new Result(none, themIdx.Count, [], TimeSpan.Zero, "nothing to tell apart");

        var sw = Stopwatch.StartNew();
        float[] samples;
        try { samples = AudioLoader.To16kMono(themWav); }
        catch (Exception ex) { return new Result(none, 0, [], sw.Elapsed, $"could not read them.wav: {ex.Message}"); }
        ct.ThrowIfCancellationRequested();

        // Cut every remote turn into overlapping windows, remembering which turn each window belongs to,
        // so one long turn cannot dominate and a blended embedding cannot be created in the first place.
        var windows = new List<(float Start, float End)>();
        var owner = new List<int>();                       // index into themIdx, per window
        for (var t = 0; t < themIdx.Count; t++)
        {
            var seg = transcript[themIdx[t]];
            var start = seg.StartMs / 1000f;
            var end = seg.EndMs / 1000f;
            if (end - start < SpeakerDiariser.MinSpeechSec) continue;

            for (var w = start; w < end - SpeakerDiariser.MinSpeechSec + 0.001f; w += HopSec)
            {
                windows.Add((w, Math.Min(w + WindowSec, end)));
                owner.Add(t);
                if (w + WindowSec >= end) break;
            }
        }
        if (windows.Count < 2) return new Result(none, 1, [], sw.Elapsed, "not enough speech to tell voices apart");

        IReadOnlyList<SpeakerSpan> spans;
        try { (spans, _) = SpeakerDiariser.ByUtterance(samples, windows, expectedSpeakers); }
        catch (Exception ex) { return new Result(none, 0, [], sw.Elapsed, $"diarisation failed: {ex.Message}"); }
        ct.ThrowIfCancellationRequested();

        // ByUtterance drops windows with too little speech, so match its output back by time rather than
        // by position - an off-by-one here would silently attribute lines to the wrong person.
        var byStart = new Dictionary<float, int>();
        foreach (var s in spans) byStart[s.StartSec] = s.Speaker;

        // Majority verdict per turn.
        var votes = new Dictionary<int, Dictionary<int, int>>();
        for (var i = 0; i < windows.Count; i++)
        {
            if (!byStart.TryGetValue(windows[i].Start, out var cluster)) continue;
            var bucket = votes.TryGetValue(owner[i], out var b) ? b : votes[owner[i]] = new Dictionary<int, int>();
            bucket[cluster] = bucket.GetValueOrDefault(cluster) + 1;
        }

        var perTurn = new int[themIdx.Count];
        for (var t = 0; t < perTurn.Length; t++)
            perTurn[t] = votes.TryGetValue(t, out var b) && b.Count > 0
                ? b.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key
                : -1;

        // A turn nobody could fingerprint (too short to embed honestly) inherits from its neighbour.
        // Short lines are usually the same person carrying on, so this is far more often right than
        // wrong, and it beats leaving holes: a transcript where every third line says "Them" while the
        // rest say "Priya" reads as broken rather than as careful.
        var last = -1;
        for (var t = 0; t < perTurn.Length; t++)
        {
            if (perTurn[t] >= 0) last = perTurn[t];
            else if (last >= 0) perTurn[t] = last;
        }
        // ...and backwards, for the opening lines - there is no earlier turn to inherit from, so the
        // first few lines of a meeting would otherwise be the only ones left unattributed.
        for (var t = perTurn.Length - 1; t > 0; t--)
            if (perTurn[t - 1] < 0 && perTurn[t] >= 0) perTurn[t - 1] = perTurn[t];

        var speakers = perTurn.Where(c => c >= 0).Distinct().Count();

        // Now put names to the clusters, from the conversation alone. The whole transcript goes in, both
        // streams, in order: "thanks, Priya" only works if we can see who spoke either side of it.
        var turns = new List<NamedTurn>(transcript.Count);
        var pos = 0;
        for (var i = 0; i < transcript.Count; i++)
        {
            if (transcript[i].Speaker == Speaker.Them && pos < themIdx.Count && themIdx[pos] == i)
                turns.Add(new NamedTurn(perTurn[pos++], transcript[i].Text));
            else
                turns.Add(new NamedTurn(-1, transcript[i].Text));   // the local user
        }

        var named = SpeakerNamer.Name(turns, roster, ownName);
        var nameOf = named.ToDictionary(l => l.Cluster, l => l.Name);

        var tags = new string?[transcript.Count];
        for (var t = 0; t < themIdx.Count; t++)
        {
            var cluster = perTurn[t];
            if (cluster < 0) continue;
            // One voice, one label: a real name when the conversation gave one, else an honest number.
            tags[themIdx[t]] = nameOf.TryGetValue(cluster, out var n) ? n : SpeakerNamer.Fallback(cluster);
        }

        // One remote voice and no name for it? Leave the transcript alone. "Them" already says exactly
        // that, and "Speaker 1" is a worse way of saying it.
        if (speakers <= 1 && named.Count == 0)
            return new Result(none, speakers, named, sw.Elapsed, "only one remote voice");

        sw.Stop();
        return new Result(tags, speakers, named, sw.Elapsed);
    }
}
