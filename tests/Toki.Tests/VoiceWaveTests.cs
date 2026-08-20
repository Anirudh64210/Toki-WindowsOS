using NAudio.Wave;
using Toki.Audio;
using Toki.Controls;

namespace Toki.Tests;

/// <summary>
/// The waveform's PURE logic: what a measured RMS turns into on screen, and the fixed envelope the
/// bars are shaped by. The dancing itself needs eyes, but everything that decides WHETHER it dances
/// is arithmetic and belongs in a test - the whole point of the rebuild is that silence has to look
/// like silence, and "it looked right when I ran it once" is not a guarantee of that.
/// </summary>
public class LevelScaleTests
{
    static float Settle(LevelScale s, float rms, int ticks)
    {
        var last = 0f;
        for (var i = 0; i < ticks; i++) last = s.Push(rms);
        return last;
    }

    [Fact]
    public void Digital_silence_reads_zero()
    {
        var s = new LevelScale();
        Assert.Equal(0f, Settle(s, 0f, LevelScale.Window * 2));
    }

    [Fact]
    public void A_quiet_room_does_not_dance()
    {
        // Steady room tone, well above digital silence. The noise tracker follows it, so the bars
        // stay resting - this is the bug the old decorative waves had by construction.
        var s = new LevelScale();
        Assert.Equal(0f, Settle(s, 0.008f, LevelScale.Window * 2));
    }

    [Fact]
    public void Speech_after_silence_fills_the_row()
    {
        var s = new LevelScale();
        Settle(s, 0.0005f, LevelScale.Window);
        Assert.True(s.Push(0.05f) > 0.95f);
    }

    [Fact]
    public void A_very_quiet_mic_still_moves_the_bars()
    {
        // The owner's mic records ~38dB below the meeting stream. Speech at 0.01 RMS has to read as
        // speech, or the honest wave is a permanently flat one.
        var s = new LevelScale();
        for (var i = 0; i < LevelScale.Window; i++) s.Push(i % 3 == 0 ? 0.01f : 0.0008f);
        Assert.True(s.Push(0.01f) > 0.3f);
    }

    [Fact]
    public void Curve_is_square_root_not_linear()
    {
        // Halfway up the span lands at sqrt(0.5), not 0.5. Linear amplitude spends the whole row on
        // the last few dB and reads as a bar that is either flat or slammed.
        var mid = LevelScale.Curve(0.5f, 0f, 1f);
        Assert.Equal(Math.Sqrt(0.5), mid, 3);
        Assert.True(mid > 0.6f);
    }

    [Fact]
    public void Curve_clamps_at_both_ends()
    {
        Assert.Equal(0f, LevelScale.Curve(0.001f, 0.01f, 0.2f));   // under the noise floor
        Assert.Equal(1f, LevelScale.Curve(0.9f, 0.01f, 0.2f));     // over the reference
    }

    [Fact]
    public void A_collapsed_span_reads_silent_instead_of_dividing_by_zero()
    {
        // Push() can never hand these in, but a caller with a flat history could, and the answer to
        // "no span" has to be resting dots - a NaN scale would freeze the row at whatever it held.
        Assert.Equal(0f, LevelScale.Curve(0.05f, 0.05f, 0.05f));
        Assert.Equal(0f, LevelScale.Curve(0.04f, 0.05f, 0.05f));
    }

    [Fact]
    public void Reference_tracks_the_loud_end_and_noise_the_quiet_end()
    {
        var s = new LevelScale();
        for (var i = 0; i < LevelScale.Window; i++) s.Push(i % 4 == 0 ? 0.20f : 0.01f);
        Assert.InRange(s.Reference, 0.15f, 0.25f);
        Assert.InRange(s.Noise, LevelScale.HardFloor, 0.02f);
    }

    [Fact]
    public void Noise_never_drops_below_the_hard_floor()
    {
        var s = new LevelScale();
        Settle(s, 0f, LevelScale.Window);
        Assert.Equal(LevelScale.HardFloor, s.Noise);
    }
}

public class WaveProfileTests
{
    [Fact]
    public void The_centre_is_the_tallest_bar_and_the_ends_the_shortest()
    {
        var p = WaveProfile.Build(14);
        var centre = Math.Max(p[6], p[7]);
        Assert.True(centre > p[0]);
        Assert.True(centre > p[13]);
        Assert.True(p[0] < 0.6);    // the dome's ends sit near EndFactor, jitter and all
        Assert.True(p[13] < 0.6);
    }

    [Fact]
    public void The_dome_rises_monotonically_apart_from_the_jitter()
    {
        // Every step towards the middle gains more than the +-12% jitter can take away.
        var p = WaveProfile.Build(9);
        for (var i = 1; i <= 4; i++) Assert.True(p[i] > p[i - 1] * 0.9);
        Assert.True(p[4] > p[0] * 1.5);
    }

    [Fact]
    public void Jitter_is_real_and_breaks_the_symmetry()
    {
        // A pure cosine dome is a mirror; the jitter is what stops the row reading as a drawn arch.
        var p = WaveProfile.Build(14);
        var mirrored = 0;
        for (var i = 0; i < 7; i++)
            if (Math.Abs(p[i] - p[13 - i]) > 1e-9) mirrored++;
        Assert.True(mirrored >= 6);
    }

    [Fact]
    public void No_bar_can_grow_out_of_its_own_row()
    {
        // Normalised to a tallest bar of exactly 1, so the control's Height is the row's real height.
        // Before this the jitter pushed the centre bar to 1.12 and the wobble to 1.21, and it grew
        // silently over whatever sat under it.
        foreach (var n in new[] { 1, 2, 9, 14, 31 })
        {
            var p = WaveProfile.Build(n);
            Assert.Equal(1.0, p.Max(), 9);
            foreach (var v in p) Assert.InRange(v, 0.3, 1.0 + 1e-9);
        }
    }

    [Fact]
    public void The_envelope_is_the_same_every_time()
    {
        // Fixed by construction, not by a seeded Random whose sequence a runtime upgrade may change.
        Assert.Equal(WaveProfile.Build(14), WaveProfile.Build(14));
        Assert.Equal(WaveProfile.Build(9), WaveProfile.Build(9));
    }

    [Fact]
    public void A_single_bar_does_not_divide_by_zero()
    {
        var p = WaveProfile.Build(1);
        Assert.Single(p);
        Assert.InRange(p[0], 0.8, 1.2);
    }
}

/// <summary>
/// The level path itself, end to end and without a room: synthesise the buffers a capture would hand
/// over, push them through the real meter and the real scale, and watch the bars rise and fall. A
/// self-test that never produced its real artifact is not a test (lesson 12).
/// </summary>
public class LevelMeterTests
{
    static readonly WaveFormat Pcm16 = new(16000, 16, 1);
    static readonly WaveFormat Float32 = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    /// <summary>One 10ms buffer of a 440Hz sine at the given amplitude, 16-bit mono - the shape a
    /// WASAPI capture actually delivers.</summary>
    static byte[] SineBuffer(double amplitude, int samples = 160, double freq = 440, int rate = 16000)
    {
        var b = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * freq * i / rate));
            BitConverter.GetBytes(v).CopyTo(b, i * 2);
        }
        return b;
    }

    [Fact]
    public void Rms_of_a_full_scale_sine_is_the_textbook_value()
    {
        var b = SineBuffer(1.0);
        Assert.Equal(0.707, LevelMeter.RmsOf(b, b.Length, Pcm16), 2);
    }

    [Fact]
    public void Rms_of_silence_is_zero()
    {
        var b = new byte[320];
        Assert.Equal(0f, LevelMeter.RmsOf(b, b.Length, Pcm16));
    }

    [Fact]
    public void Rms_reads_float_buffers_too()
    {
        var b = new byte[400];
        for (var i = 0; i < 100; i++) BitConverter.GetBytes(0.5f).CopyTo(b, i * 4);
        Assert.Equal(0.5, LevelMeter.RmsOf(b, b.Length, Float32), 3);
    }

    [Fact]
    public void An_unreadable_format_reports_nothing_rather_than_guessing()
    {
        var b = SineBuffer(1.0);
        Assert.Equal(0f, LevelMeter.RmsOf(b, b.Length, new WaveFormat(16000, 24, 1)));
    }

    [Fact]
    public void The_loudest_recent_buffer_wins_the_window()
    {
        // A quiet buffer between two loud ones must not flicker the row.
        var m = new LevelMeter();
        m.ReportAt(0.4f, 0);
        m.ReportAt(0.01f, 20);
        Assert.Equal(0.4f, m.LevelAt(30));
    }

    [Fact]
    public void A_source_that_stops_decays_to_zero_on_its_own()
    {
        var m = new LevelMeter();
        m.ReportAt(0.4f, 0);
        Assert.Equal(0.4f, m.LevelAt(150));                      // still inside the previous bucket
        Assert.Equal(0f, m.LevelAt(2 * LevelMeter.BucketMs + 1)); // two empty buckets = capture stopped
    }

    [Fact]
    public void A_sine_burst_then_silence_rises_fast_and_settles()
    {
        // The whole contract in one run: 1s of speech-level tone, then 1s of nothing, sampled at the
        // wave's own 30Hz. Capture buffers arrive every 10ms, the wave reads every 33ms.
        var meter = new LevelMeter();
        var scale = new LevelScale();
        var silence = new byte[320];
        var tone = SineBuffer(0.12);
        var read = new List<float>();

        for (var ms = 0; ms < 2000; ms += 10)
        {
            var buf = ms < 1000 ? tone : silence;
            meter.ReportAt(LevelMeter.RmsOf(buf, buf.Length, Pcm16), ms);
            if (ms % 30 == 0) read.Add(scale.Push(meter.LevelAt(ms)));
        }

        var duringSpeech = read.Take(33).ToList();      // the first ~1s of reads
        var afterSilence = read.Skip(50).ToList();      // well after the tone stopped

        Assert.True(duringSpeech[1] > 0.9f, $"speech should fill the row at once, got {duringSpeech[1]}");
        Assert.True(duringSpeech.All(v => v > 0.5f), "the row must not sag while somebody is talking");
        Assert.True(afterSilence.All(v => v == 0f), "silence must read as silence");
    }
}
