namespace Toki.Transcription;

public enum Speaker { You, Them }

/// <summary>One utterance in the transcript, tagged with who said it and when
/// (meeting-relative milliseconds).</summary>
public sealed record TranscriptSegment(Speaker Speaker, int StartMs, int EndMs, string Text)
{
    public TimeSpan Start => TimeSpan.FromMilliseconds(StartMs);
}
