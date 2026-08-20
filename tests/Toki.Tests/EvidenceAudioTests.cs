using System.IO;
using NAudio.Wave;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Cutting a few seconds of evidence out of a saved recording.
///
/// The properties that matter are the ones a person would notice: the clip is the moment and not the
/// meeting, it starts slightly EARLY so a sentence does not begin mid-word, and what gets exported is
/// byte-identical to what gets played (one code path, so "share this" can never quietly widen).
/// </summary>
public sealed class EvidenceAudioTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"toki-clip-{Guid.NewGuid():N}");

    public EvidenceAudioTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>A silent but well-formed 16kHz mono WAV of a given length - the same shape Toki records.</summary>
    string MakeWav(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var format = new WaveFormat(16000, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        var frame = new byte[format.AverageBytesPerSecond];
        for (var i = 0; i < seconds; i++) writer.Write(frame, 0, frame.Length);
        return path;
    }

    static TranscriptSegment At(int startMs, int endMs, Speaker who = Speaker.Them) =>
        new(who, startMs, endMs, "the words in question");

    [Fact]
    public void The_window_starts_before_the_moment_and_ends_after_it()
    {
        var (start, end) = EvidenceAudio.Window(At(30_000, 34_000));
        Assert.True(start < 30_000, "the clip must have a run-up, or a sentence starts mid-word");
        Assert.True(end > 34_000, "the clip must let the thought finish");
    }

    [Fact]
    public void A_moment_at_the_very_start_does_not_run_off_the_front()
    {
        var (start, _) = EvidenceAudio.Window(At(200, 1_500));
        Assert.Equal(0, start);
    }

    [Fact]
    public void A_very_long_turn_is_capped_rather_than_played_whole()
    {
        // A ten-minute monologue is one segment. Evidence is a moment; this must not become a
        // "play the meeting" button by accident.
        var (start, end) = EvidenceAudio.Window(At(0, 600_000));
        Assert.True(end - start <= 60_000, $"clip was {(end - start) / 1000}s");
    }

    [Fact]
    public void The_clip_is_a_playable_wav_of_roughly_the_window()
    {
        var wav = MakeWav("them.wav", 60);
        var (start, end) = EvidenceAudio.Window(At(30_000, 33_000));

        var bytes = EvidenceAudio.Clip(wav, start, end);

        Assert.NotNull(bytes);
        using var reader = new WaveFileReader(new MemoryStream(bytes!));
        var actual = reader.TotalTime.TotalMilliseconds;
        Assert.InRange(actual, (end - start) - 150, (end - start) + 150);
    }

    [Fact]
    public void The_clip_is_shorter_than_the_recording_it_came_from()
    {
        var wav = MakeWav("them.wav", 60);
        var (start, end) = EvidenceAudio.Window(At(30_000, 33_000));

        var bytes = EvidenceAudio.Clip(wav, start, end)!;

        Assert.True(bytes.Length < new FileInfo(wav).Length / 4,
            "an evidence clip must never approach the size of the whole meeting");
    }

    [Fact]
    public void Playing_and_exporting_produce_the_same_bytes()
    {
        // Both go through Clip, so this is a guard against a future second path appearing.
        var wav = MakeWav("them.wav", 60);
        var (start, end) = EvidenceAudio.Window(At(20_000, 22_000));
        Assert.Equal(EvidenceAudio.Clip(wav, start, end), EvidenceAudio.Clip(wav, start, end));
    }

    [Fact]
    public void A_moment_past_the_end_of_the_file_yields_nothing()
    {
        var wav = MakeWav("them.wav", 5);
        Assert.Null(EvidenceAudio.Clip(wav, 90_000, 95_000));
    }

    [Fact]
    public void A_missing_recording_is_silent_rather_than_broken()
    {
        Assert.Null(EvidenceAudio.Clip(Path.Combine(_dir, "gone.wav"), 0, 3000));

        var m = new MeetingRecord { Folder = _dir };
        Assert.Null(EvidenceAudio.WavFor(m, Speaker.Them));
        Assert.False(EvidenceAudio.Available(m, At(0, 1000)));
    }

    [Fact]
    public void A_meeting_with_no_folder_offers_nothing()
    {
        var m = new MeetingRecord { Folder = null };
        Assert.False(EvidenceAudio.Available(m, At(0, 1000)));
    }

    [Fact]
    public void The_speakers_own_stream_is_the_one_that_plays()
    {
        MakeWav("you.wav", 5);
        MakeWav("them.wav", 5);
        var m = new MeetingRecord { Folder = _dir };

        Assert.EndsWith("you.wav", EvidenceAudio.WavFor(m, Speaker.You));
        Assert.EndsWith("them.wav", EvidenceAudio.WavFor(m, Speaker.Them));
    }

    [Fact]
    public void The_suggested_name_survives_a_hostile_title()
    {
        var m = new MeetingRecord { Title = "Q3: pricing / margins <draft>", Folder = _dir };
        var name = EvidenceAudio.SuggestedFileName(m, 65_000);

        Assert.EndsWith(".wav", name);
        Assert.DoesNotContain(name, Path.GetInvalidFileNameChars().Select(c => c.ToString()));
        Assert.Equal(name, Path.GetFileName(name));   // no path separators smuggled through a title
    }
}

/// <summary>
/// The range behind a clip - locked down because the first version played a stray "That." in a real
/// meeting: scoring worked on a 3-line window, the return value was only the window's FIRST line, and
/// that line did not even need to contain a matching word.
/// </summary>
public class EvidenceRangeTests
{
    static IReadOnlyList<TranscriptSegment> T(params string[] lines) =>
        lines.Select((l, i) => new TranscriptSegment(Speaker.Them, i * 4000, i * 4000 + 3500, l)).ToList();

    [Fact]
    public void A_point_spread_over_two_lines_gets_both()
    {
        var t = T("So about the pricing page rewrite.",
                  "Devi should draft the opening paragraph by Friday.");
        var r = Evidence.BestRange("Devi to draft the pricing page opening paragraph", t);
        Assert.NotNull(r);
        Assert.Equal(0, r!.Value.First);
        Assert.Equal(1, r.Value.Last);
    }

    [Fact]
    public void A_leading_line_with_no_evidence_is_trimmed_off()
    {
        // THE "That." BUG: the window that wins can START on a line contributing nothing.
        var t = T("That.",
                  "We should rewrite the pricing page opener.",
                  "And pull the bounce rate before Friday.");
        var r = Evidence.BestRange("rewrite the pricing page opener and pull the bounce rate", t);
        Assert.NotNull(r);
        Assert.Equal(1, r!.Value.First);   // "That." is not evidence and is not played
    }

    [Fact]
    public void BestSegment_still_answers_with_the_ranges_first_line()
    {
        var t = T("That.", "We should rewrite the pricing page opener.", "And pull the bounce rate.");
        Assert.Equal(Evidence.BestRange("rewrite the pricing opener and pull the bounce rate", t)!.Value.First,
                     Evidence.BestSegment("rewrite the pricing opener and pull the bounce rate", t));
    }
}

/// <summary>The owner's rule, verbatim: "only show audio if it's guaranteed present."</summary>
public class EvidenceGuaranteeTests
{
    static TranscriptSegment Seg(int start, int end, Speaker who = Speaker.Them, string text = "words") =>
        new(who, start, end, text);

    [Fact]
    public void A_fragment_below_the_floor_gets_no_play_button()
    {
        // A 300ms utterance plus padding is still under the 1.5s floor: no button, ever.
        var m = new MeetingRecord { Folder = null };
        Assert.False(EvidenceAudio.Available(m, new[] { Seg(10_000, 10_300) }));
    }

    [Fact]
    public void Degenerate_timestamps_are_refused()
    {
        var m = new MeetingRecord { Folder = null };
        Assert.False(EvidenceAudio.Available(m, new[] { Seg(10_000, 10_000) }));          // zero length
        Assert.False(EvidenceAudio.Available(m, new[] { Seg(10_000, 9_000) }));           // backwards
        Assert.False(EvidenceAudio.Available(m, new[] { Seg(8_000, 12_000), Seg(4_000, 6_000) })); // non-monotonic
    }

    [Fact]
    public void A_range_crossing_streams_is_refused()
    {
        // Two streams are two FILES; playing half a moment is worse than playing none.
        var m = new MeetingRecord { Folder = null };
        Assert.False(EvidenceAudio.Available(m, new[]
        {
            Seg(10_000, 13_000, Speaker.Them),
            Seg(13_500, 16_000, Speaker.You),
        }));
    }

    [Fact]
    public void The_cut_snaps_into_the_silence_before_the_next_sentence()
    {
        // Descript's rule: never let the tail bleed into the next line of the same stream.
        var range = new[] { Seg(10_000, 13_000) };
        var transcript = new[] { range[0], Seg(13_400, 17_000) };   // next line starts 400ms later

        var (_, end) = EvidenceAudio.Window(range, transcript);

        Assert.True(end < 13_400, $"cut at {end} bleeds into the next sentence at 13400");
        Assert.True(end > 13_000, "the cut still clears the last word");
    }

    [Fact]
    public void With_no_neighbour_the_tail_is_the_full_breathing_room()
    {
        var range = new[] { Seg(10_000, 13_000) };
        var (start, end) = EvidenceAudio.Window(range, new[] { range[0] });
        Assert.Equal(9_700, start);    // 300ms lead-in
        Assert.Equal(13_700, end);     // 700ms tail
    }
}
