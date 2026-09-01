using System;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The Ask-path silence trim. Parakeet TDT returns an EMPTY decode when fed trailing silence
/// (NVIDIA-NeMo #15757), and a push-to-talk clip always ends in silence, so the quiet ends are
/// trimmed before the recognizer sees them - keeping a small pad so no consonant is clipped.
/// </summary>
public class SilenceTrimTests
{
    const int Rate = AudioLoader.TargetRate;   // 16k
    const int Pad = Rate / 8;                  // 125ms keep-pad each side

    static float[] Clip(int leadSilenceMs, int speechMs, int trailSilenceMs, float level = 0.5f)
    {
        int lead = Rate * leadSilenceMs / 1000, speech = Rate * speechMs / 1000, trail = Rate * trailSilenceMs / 1000;
        var s = new float[lead + speech + trail];
        for (var i = 0; i < speech; i++) s[lead + i] = (i % 2 == 0 ? level : -level);   // a loud alternating tone
        return s;
    }

    [Fact]
    public void TrailingSilenceIsTrimmedToThePad()
    {
        // MUTATION PROBE: 2s of speech + 1.5s of trailing silence. Untrimmed, Parakeet decodes empty;
        // trimmed, only the ~125ms pad of tail survives.
        var clip = Clip(0, 2000, 1500);
        var outp = TranscriptionService.TrimSilenceEnds(clip);
        var speechEnd = Rate * 2000 / 1000;
        Assert.True(outp.Length < clip.Length, "trailing silence should be removed");
        Assert.True(outp.Length <= speechEnd + Pad + 2, $"tail beyond the 125ms pad should be gone (len {outp.Length})");
        Assert.True(outp.Length >= speechEnd, "the speech itself must never be trimmed");
    }

    [Fact]
    public void LeadingSilenceIsKeptSoTheFirstWordIsNeverClipped()
    {
        // Trailing-only by design: a soft first word ("Hi" before a louder "Toki") must survive, so
        // the head is left intact even when it is quiet. Only the tail past the pad is removed.
        var clip = Clip(1000, 1000, 1000);
        var outp = TranscriptionService.TrimSilenceEnds(clip);
        var leadPlusSpeech = Rate * 2000 / 1000;   // 1s lead + 1s speech, both kept
        Assert.True(outp.Length >= leadPlusSpeech, "the head (and the whole first word) must be kept");
        Assert.True(outp.Length <= leadPlusSpeech + Pad + 2, "the trailing silence past the pad should be gone");
    }

    [Fact]
    public void AnAllSilentClipIsReturnedUnchangedForTheGateToJudge()
    {
        var clip = new float[Rate];   // 1s of pure zero
        Assert.Same(clip, TranscriptionService.TrimSilenceEnds(clip));
    }

    [Fact]
    public void AClipWithNoSilenceToTrimIsReturnedUnchanged()
    {
        var clip = Clip(0, 500, 0);
        Assert.Same(clip, TranscriptionService.TrimSilenceEnds(clip));
    }
}
