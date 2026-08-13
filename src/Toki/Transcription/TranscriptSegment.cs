namespace Toki.Transcription;

/// <summary>
/// Which STREAM an utterance came from - the microphone or the meeting's own audio.
///
/// This is not "who spoke". It stays a two-value enum on purpose even now that diarisation can tell the
/// remote people apart: it is what the <c>you:</c>/<c>them:</c> search prefixes filter on, what
/// <c>COUNT(DISTINCT speaker)</c> counts, and it is knowable with certainty because it is a fact about
/// where the bytes came from rather than an inference about a voice. Who actually spoke lives in
/// <see cref="TranscriptSegment.Tag"/>, which can be absent, wrong, or renamed by the user.
/// </summary>
public enum Speaker { You, Them }

/// <summary>One utterance in the transcript, tagged with who said it and when
/// (meeting-relative milliseconds).</summary>
public sealed record TranscriptSegment(Speaker Speaker, int StartMs, int EndMs, string Text)
{
    public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);

    /// <summary>
    /// Who actually said this, once the speakers have been told apart: a real name when the conversation
    /// gave one away, else "Speaker 2". Null means diarisation has not run (or found nothing), and the
    /// transcript falls back to the stream name.
    ///
    /// Nothing about the VOICE is stored - only this text. Close the meeting and Toki has no way to
    /// recognise the same person again, which is the point.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>What to show against this line.</summary>
    public string Label => Tag ?? (Speaker == Toki.Transcription.Speaker.You ? "You" : "Them");
}
