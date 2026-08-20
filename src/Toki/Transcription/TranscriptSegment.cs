namespace Toki.Transcription;

/// <summary>
/// Which STREAM an utterance came from - the mic or the meeting's audio. NOT "who spoke": it
/// stays a two-value enum because the <c>you:</c>/<c>them:</c> search prefixes filter on it, and
/// it is a certain fact about where the bytes came from rather than an inference about a voice.
/// Who actually spoke lives in <see cref="TranscriptSegment.Tag"/>.
/// </summary>
public enum Speaker { You, Them }

/// <summary>One utterance in the transcript, tagged with who said it and when
/// (meeting-relative milliseconds).</summary>
public sealed record TranscriptSegment(Speaker Speaker, int StartMs, int EndMs, string Text)
{
    public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);

    /// <summary>
    /// Who actually said this once speakers are told apart: a real name when the conversation gave
    /// one away, else "Speaker 2". Null means diarisation has not run (or found nothing). Nothing
    /// about the VOICE is stored - only this text (owner decision: no persistent voiceprints).
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>What to show against this line.</summary>
    public string Label => Tag ?? (Speaker == Toki.Transcription.Speaker.You ? "You" : "Them");
}
