using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Whisper (the ask-by-voice decoder) was trained on YouTube captions, so on near-silence it emits a
/// few stock caption phrases. Those whole-output hallucinations are rejected as "nothing said" - but a
/// real greeting a user actually speaks must pass through (the small-talk layer answers it).
/// </summary>
public class WhisperNoiseTests
{
    [Theory]
    [InlineData("you")]
    [InlineData("You.")]
    [InlineData("Thanks for watching!")]
    [InlineData("Please subscribe")]
    [InlineData("♪♪")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("")]
    public void StockCaptionHallucinationsAreRejected(string text)
        => Assert.True(WhisperTranscriber.IsNoiseHallucination(text));

    [Theory]
    [InlineData("Hi Toki")]
    [InlineData("thank you")]        // a real greeting - small-talk handles it, so it must pass
    [InlineData("bye")]
    [InlineData("what did we decide about pricing")]
    [InlineData("you owe me the deck")]   // 'you' as part of a real sentence must survive
    public void RealUtterancesPassThrough(string text)
        => Assert.False(WhisperTranscriber.IsNoiseHallucination(text));
}
