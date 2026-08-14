using System.IO;
using NAudio.Wave;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Plays - and, on request, exports - the few seconds of recording a note line came from.
///
/// ── WHY THIS IS BUILT THE WAY IT IS (competitor + privacy analysis, 2026-08-13) ──
///
/// GRANOLA keeps no audio at all. It streams the microphone to a transcription service and deletes the
/// audio the moment the text exists, and it markets that as the privacy feature - which it is. Its
/// evidence links are therefore text-only: click a bullet, land on the transcript line. You can check
/// what the model claims against what was WRITTEN DOWN, never against what was said.
///
/// OTTER keeps the audio, in its cloud. Highlight a passage, press Share, and it mints a link to a
/// hosted snippet: the recipient opens a web page showing the speaker and a play button. The audio
/// never leaves Otter as a file; what is shared is a URL Otter continues to control, and revoking the
/// link revokes the access.
///
/// TOKI is the only one of the three that already holds the audio ON THE USER'S OWN DISK, forever, at
/// zero cost. That is a real advantage - "no way to verify what the AI claims" is the loudest complaint
/// in this category, and Toki can answer it without a server. It is also a real liability, because a
/// local file has no revoke: once a clip is sent, it is gone, and it contains a colleague's voice.
///
/// So the design takes Otter's usefulness and keeps Granola's caution:
///
///   1. PLAYBACK IS FREE AND SILENT. Listening is between the user and their own disk. No prompt, no
///      confirmation, nothing recorded about it.
///   2. IT PLAYS A WINDOW, NEVER THE MEETING. A few seconds around the moment, with a short run-up so
///      the sentence starts where a person would start it. There is no "play the whole recording"
///      affordance here; this is evidence, not a media player.
///   3. EXPORT IS A DELIBERATE, SEPARATE ACT, and it is the ONLY thing that warns. The first export
///      says plainly what the file contains and that Toki cannot take it back. Not a scary dialog -
///      one honest sentence, once, in the words a person would use.
///   4. IT EXPORTS THE CLIP, NEVER THE MEETING. The file is exactly the window that was played, so
///      "share the evidence" cannot quietly become "share the whole call".
///   5. WAV, not MP3. Adding an encoder to ship one clip is a dependency and a licence question; a
///      few seconds of 16kHz mono is a couple of hundred kilobytes either way.
///   6. IT DISAPPEARS WHEN THE AUDIO DOES. Deleting a meeting deletes its recording (H1), and old
///      meetings may never have kept one. In both cases there is simply no play button - the same
///      rule <see cref="Evidence"/> already follows: silent when it cannot be sure, because an
///      affordance that fails is worse than one that was never offered.
/// </summary>
public static class EvidenceAudio
{
    // Padding, tuned against how the shipping products cut (researched 2026-08-13):
    //
    // Descript's edit-point patent describes the load-bearing trick - never cut at a raw ASR word
    // timestamp; move the cut INTO the neighbouring silence. Transducer models (Parakeet TDT included)
    // are weakest on END times - a token is emitted before its sound finishes - so the tail matters
    // more than the lead. Fathom bounds highlights at speaker-turn changes, ignoring backchannels;
    // Toki's ranges already sit on merged turns, so these constants are the remaining fine-tuning.

    /// <summary>Run-up before the first word - enough that its attack is not clipped, not so much that
    /// a "precise" clip opens with someone else's sentence (it was 1500ms once; that was padding
    /// wearing accuracy's clothes).</summary>
    const int LeadInMs = 300;

    /// <summary>After the last word. Deliberately longer than the lead: ASR end-times run early, and a
    /// clipped final word is more jarring than a beat of silence.</summary>
    const int TailMs = 700;

    /// <summary>When the next line of the same stream is known, the cut snaps to just before it rather
    /// than taking the whole tail - into the silence, never into the next sentence.</summary>
    const int SnapMarginMs = 80;

    /// <summary>A moment must be at least this long to be worth a play button - anything shorter is a
    /// fragment ("That."), and a clip that plays one word makes every other clip less trusted. 1.5s is
    /// where the research put the floor.</summary>
    public const int MinMomentMs = 1500;

    /// <summary>A hard ceiling. Evidence is a moment; anything longer is a recording.</summary>
    const int MaxClipMs = 45_000;

    /// <summary>Which file holds a given speaker's audio. Two streams, so the microphone and the
    /// meeting are separate recordings - and the line's own stream is the one that said it.</summary>
    public static string? WavFor(MeetingRecord m, Speaker speaker)
    {
        if (string.IsNullOrWhiteSpace(m.Folder)) return null;
        var path = Path.Combine(m.Folder, speaker == Speaker.You ? "you.wav" : "them.wav");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Is playing this range GUARANTEED to work? Only then does a play button appear (owner's rule:
    /// "only show audio if it's guaranteed present"). Three checks, each of which used to fail only at
    /// press time, which is the worst moment to find out:
    ///   1. every line in the range comes from the same stream (a range that crosses speakers would
    ///      need two files spliced - refuse rather than play half),
    ///   2. that stream's WAV is on disk,
    ///   3. the file actually COVERS the window (headers are read once and cached - a recording cut
    ///      short by a crash is shorter than its transcript says), and the moment clears the
    ///      minimum-length floor.
    /// </summary>
    public static bool Available(MeetingRecord m, IReadOnlyList<TranscriptSegment> range,
        IReadOnlyList<TranscriptSegment>? transcript = null)
    {
        if (range.Count == 0) return false;
        var speaker = range[0].Speaker;
        for (var i = 1; i < range.Count; i++)
            if (range[i].Speaker != speaker) return false;

        // Degenerate timestamps mean the clip boundaries are guesses, and a guessed clip plays the
        // wrong thing with total confidence. Refuse: zero-length lines, or a range whose lines run
        // backwards in time.
        for (var i = 0; i < range.Count; i++)
        {
            if (range[i].EndMs <= range[i].StartMs) return false;
            if (i > 0 && range[i].StartMs < range[i - 1].StartMs) return false;
        }

        if (WavFor(m, speaker) is not { } wav) return false;

        var (start, end) = Window(range, transcript);
        if (end - start < MinMomentMs) return false;
        return end <= DurationMs(wav);
    }

    public static bool Available(MeetingRecord m, TranscriptSegment segment) =>
        Available(m, new[] { segment });

    /// <summary>
    /// The window a RANGE of lines plays: from the first matched word's line to the last, with the
    /// lead-in, and the end SNAPPED INTO THE SILENCE before the same stream's next line when the whole
    /// transcript is available to find it - the Descript rule. A cut placed just before the next
    /// sentence starts sounds deliberate; a cut placed a fixed 700ms after an ASR end-time sometimes
    /// lands mid-way through it.
    /// </summary>
    public static (int StartMs, int EndMs) Window(IReadOnlyList<TranscriptSegment> range,
        IReadOnlyList<TranscriptSegment>? transcript = null)
    {
        var startMs = range.Min(s => s.StartMs);
        var endMs = range.Max(s => s.EndMs);
        var start = Math.Max(0, startMs - LeadInMs);
        var end = endMs + TailMs;

        if (transcript is not null)
        {
            var speaker = range[0].Speaker;
            var nextStart = int.MaxValue;
            foreach (var s in transcript)
                if (s.Speaker == speaker && s.StartMs >= endMs && s.StartMs < nextStart && !range.Contains(s))
                    nextStart = s.StartMs;
            if (nextStart != int.MaxValue)
                end = Math.Min(end, Math.Max(endMs + 150, nextStart - SnapMarginMs));
        }

        end = Math.Max(start + 500, end);
        if (end - start > MaxClipMs) end = start + MaxClipMs;
        return (start, end);
    }

    public static (int StartMs, int EndMs) Window(TranscriptSegment segment) =>
        Window(new[] { segment });

    // WAV durations, cached per path: Available runs while a note renders, and re-reading the same
    // header for every bullet would turn a render into file IO. A WAV never grows after the meeting
    // ends, so the cache cannot go stale in the direction that matters.
    static readonly Dictionary<string, int> _durations = new(StringComparer.OrdinalIgnoreCase);

    static int DurationMs(string wavPath)
    {
        lock (_durations)
        {
            if (_durations.TryGetValue(wavPath, out var known)) return known;
            var ms = 0;
            try
            {
                using var reader = new WaveFileReader(wavPath);
                ms = (int)reader.TotalTime.TotalMilliseconds;
            }
            catch (Exception ex) { App.Log($"evidence: could not read wav header: {ex.Message}"); }
            _durations[wavPath] = ms;
            return ms;
        }
    }

    /// <summary>
    /// Read one window out of a WAV as a new in-memory WAV, headers and all.
    ///
    /// Both playback and export go through this, so what you hear and what you send are the same bytes
    /// by construction - there is no second code path that could drift and export more than was played.
    /// </summary>
    public static byte[]? Clip(string wavPath, int startMs, int endMs)
    {
        try
        {
            using var reader = new WaveFileReader(wavPath);
            var format = reader.WaveFormat;

            var total = reader.TotalTime.TotalMilliseconds;
            if (startMs >= total) return null;                      // the moment is past the end of the file
            var end = Math.Min(endMs, (int)total);
            if (end - startMs < 200) return null;                   // too short to be worth playing

            reader.CurrentTime = TimeSpan.FromMilliseconds(startMs);
            var wanted = (long)((end - startMs) / 1000.0 * format.AverageBytesPerSecond);
            // Never split a sample frame - a half frame turns the whole clip into noise.
            wanted -= wanted % format.BlockAlign;

            var output = new MemoryStream();
            using (var writer = new WaveFileWriter(new IgnoreDisposeStream(output), format))
            {
                var buffer = new byte[format.AverageBytesPerSecond];
                long written = 0;
                while (written < wanted)
                {
                    var want = (int)Math.Min(buffer.Length, wanted - written);
                    var read = reader.Read(buffer, 0, want);
                    if (read <= 0) break;
                    writer.Write(buffer, 0, read);
                    written += read;
                }
            }
            output.Position = 0;
            return output.Length > 44 ? output.ToArray() : null;    // 44 bytes is a header and no audio
        }
        catch (Exception ex)
        {
            App.Log($"evidence clip failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>A suggested file name: the meeting, the moment, and nothing that has to be explained.</summary>
    public static string SuggestedFileName(MeetingRecord m, int startMs)
    {
        var safe = new string((m.Title ?? "Meeting").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        if (safe.Length == 0) safe = "Meeting";
        if (safe.Length > 60) safe = safe[..60].TrimEnd();
        var at = TimeSpan.FromMilliseconds(startMs);
        return $"{safe} - {at:hh\\-mm\\-ss}.wav";
    }

    /// <summary>Lets a WaveFileWriter finish its header without closing the stream underneath it.</summary>
    sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        readonly Stream _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
        public override long Seek(long off, SeekOrigin origin) => _inner.Seek(off, origin);
        public override void SetLength(long v) => _inner.SetLength(v);
        public override void Write(byte[] b, int o, int c) => _inner.Write(b, o, c);
        protected override void Dispose(bool disposing) { /* the caller owns the stream */ }
    }
}
