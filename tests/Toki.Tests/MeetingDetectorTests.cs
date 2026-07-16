using Toki.Detection;

namespace Toki.Tests;

public class MeetingDetectorTests
{
    static DetectedApp Chrome => new(@"C:\Program Files\Google\Chrome\chrome.exe", "chrome.exe", false);
    static DetectedApp Obs => new(@"C:\obs\obs64.exe", "obs64.exe", false);

    [Fact]
    public void DenylistsScreenRecordersAndSelf()
    {
        Assert.True(MeetingDetector.IsDenylisted(Obs));
        Assert.True(MeetingDetector.IsDenylisted(new DetectedApp("x", "Toki.exe", false)));
        Assert.False(MeetingDetector.IsDenylisted(Chrome));
    }

    [Fact]
    public void DebouncesBeforePrompting()
    {
        var d = new MeetingDetector();
        DetectedApp? prompted = null;
        d.MeetingDetected += a => prompted = a;

        d.PollOnce([Chrome]);          // 1st poll — not yet
        Assert.Null(prompted);
        d.PollOnce([Chrome]);          // 2nd consecutive — now
        Assert.Equal(Chrome, prompted);
    }

    [Fact]
    public void ResetsDebounceWhenAppDisappears()
    {
        var d = new MeetingDetector();
        var count = 0;
        d.MeetingDetected += _ => count++;

        d.PollOnce([Chrome]);
        d.PollOnce([]);        // gone — counter resets
        d.PollOnce([Chrome]);  // only 1 consecutive again
        Assert.Equal(0, count);
        d.PollOnce([Chrome]);  // 2 consecutive
        Assert.Equal(1, count);
    }

    [Fact]
    public void SnoozeSuppressesForCooldownThenAllows()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var d = new MeetingDetector(() => now);
        var count = 0;
        d.MeetingDetected += _ => count++;

        d.Snooze(Chrome);
        d.PollOnce([Chrome]);
        d.PollOnce([Chrome]);
        Assert.Equal(0, count);          // still cooling down

        now = now.AddMinutes(11);        // past the 10-min cooldown
        d.PollOnce([Chrome]);
        d.PollOnce([Chrome]);
        Assert.Equal(1, count);
    }

    [Fact]
    public void DoesNotPromptWhenCanPromptIsFalse()
    {
        var d = new MeetingDetector { CanPrompt = () => false };
        var count = 0;
        d.MeetingDetected += _ => count++;
        d.PollOnce([Chrome]);
        d.PollOnce([Chrome]);
        Assert.Equal(0, count);
    }

    [Fact]
    public void AutoStopsWhenTargetReleasesMicForThreePolls()
    {
        var d = new MeetingDetector();
        var ended = 0;
        d.MeetingEnded += () => ended++;
        d.BeginRecording(Chrome);

        d.PollOnce([Chrome]);   // present
        d.PollOnce([]);         // absent 1
        d.PollOnce([]);         // absent 2
        Assert.Equal(0, ended);
        d.PollOnce([]);         // absent 3 -> ended
        Assert.Equal(1, ended);
    }

    [Fact]
    public void PresenceResetsAutoStopCounter()
    {
        var d = new MeetingDetector();
        var ended = 0;
        d.MeetingEnded += () => ended++;
        d.BeginRecording(Chrome);

        d.PollOnce([]);         // absent 1
        d.PollOnce([]);         // absent 2
        d.PollOnce([Chrome]);   // back — reset
        d.PollOnce([]);
        d.PollOnce([]);
        Assert.Equal(0, ended);
    }

    [Fact]
    public void DoesNotPromptWhileRecording()
    {
        var d = new MeetingDetector();
        var prompts = 0;
        d.MeetingDetected += _ => prompts++;
        d.BeginRecording(Chrome);
        d.PollOnce([Chrome, Obs, new DetectedApp("z", "zoom.exe", false)]);
        d.PollOnce([Chrome, new DetectedApp("z", "zoom.exe", false)]);
        Assert.Equal(0, prompts);
    }
}
