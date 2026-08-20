using System.IO;
using NAudio.Wave;
using Toki.Transcription;

namespace Toki.Storage;

/// <summary>
/// Plays, and on request exports, the few seconds of recording a note line came from.
/// Invariants: playback is silent and unprompted; it plays a WINDOW, never the meeting; export is a
/// deliberate separate act (warns once, first time) and exports exactly the clip that was played;
/// WAV only; when the audio is missing there is simply no play button.
/// </summary>
public static class EvidenceAudio
{
    // Padding rule: never cut at a raw ASR word timestamp; move the cut into neighbouring silence.
    // Transducer models (Parakeet TDT) emit tokens before the sound finishes, so end-times run early
    // and the tail matters more than the lead.

    /// <summary>Run-up before the first word: enough that its attack is not clipped, not so much
    /// that the clip opens with someone else's sentence.</summary>
    const int LeadInMs = 300;

    /// <summary>After the last word. Deliberately longer than the lead: ASR end-times run early, and a
    /// clipped final word is more jarring than a beat of silence.</summary>
    const int TailMs = 700;

    /// <summary>When the next line of the same stream is known, the cut snaps to just before it rather
    /// than taking the whole tail - into the silence, never into the next sentence.</summary>
    const int SnapMarginMs = 80;

    /// <summary>Floor for offering a play button: anything shorter is a fragment, and a one-word clip
    /// makes every other clip less trusted.</summary>
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
    /// True only when playing this range is guaranteed to work; only then does a play button appear.
    /// Checks: one stream across the range, that stream's WAV on disk, and the file actually covering
    /// the window (a crash-shortened recording is shorter than its transcript says).
    /// </summary>
    public static bool Available(MeetingRecord m, IReadOnlyList<TranscriptSegment> range,
        IReadOnlyList<TranscriptSegment>? transcript = null)
    {
        if (range.Count == 0) return false;
        var speaker = range[0].Speaker;
        for (var i = 1; i < range.Count; i++)
            if (range[i].Speaker != speaker) return false;

        // Degenerate timestamps (zero-length lines, out-of-order ranges) make the boundaries guesses,
        // and a guessed clip plays the wrong thing with total confidence. Refuse.
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
    /// The window a range of lines plays: lead-in before the first line, and the end snapped into the
    /// silence before the same stream's next line when the transcript is available to find it.
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

    // WAV durations cached per path: Available runs during note render, and a WAV never grows after
    // the meeting ends, so the cache cannot go stale in the direction that matters.
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
    /// Read one window out of a WAV as a new in-memory WAV. Playback and export both go through this,
    /// so what you hear and what you send are the same bytes by construction.
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
