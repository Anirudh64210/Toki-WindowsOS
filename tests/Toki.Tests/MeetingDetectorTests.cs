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

        d.PollOnce([Chrome]);          // 1st poll - not yet
        Assert.Null(prompted);
        d.PollOnce([Chrome]);          // 2nd consecutive - now
        Assert.Equal(Chrome, prompted);
    }

    [Fact]
    public void ResetsDebounceWhenAppDisappears()
    {
        var d = new MeetingDetector();
        var count = 0;
        d.MeetingDetected += _ => count++;

        d.PollOnce([Chrome]);
        d.PollOnce([]);        // gone - counter resets
        d.PollOnce([Chrome]);  // only 1 consecutive again
        Assert.Equal(0, count);
        d.PollOnce([Chrome]);  // 2 consecutive
        Assert.Equal(1, count);
    }

    // SUPERSEDES PromptedThenDismissed_ReSurfacesAfterCooldown_IfStillOnMic (2026-09-23). That test
    // asserted the OPPOSITE of this one: a dismissed prompt came back once the 10-minute cooldown
    // expired, deliberately, so that a single "Not now" did not silence a whole meeting.
    //
    // The owner overruled it from real use: "every 10 mins or so it re asks, it becomes redundant and
    // pointless, if they want to they can manually do it". Time does not make a refusal stale. The
    // old rule is kept here in words because the reasoning behind it was sound and someone will
    // propose it again.
    [Fact]
    public void A_declined_prompt_never_comes_back_during_the_same_call()
    {
        var d = new MeetingDetector();
        var count = 0;
        d.MeetingDetected += _ => count++;

        d.PollOnce([Chrome]); d.PollOnce([Chrome]);    // debounced -> the one prompt
        Assert.Equal(1, count);
        d.Decline(Chrome);

        // A long meeting. The old cooldown would have re-asked several times over this.
        for (var i = 0; i < 400; i++) d.PollOnce([Chrome]);
        Assert.Equal(1, count);
    }

    [Fact]
    public void A_mute_toggle_is_not_a_new_meeting()
    {
        // The mic drops for a moment when you mute, change headset, or the driver hiccups. Treating
        // that as "the call ended" is what would let a refusal come straight back.
        var d = new MeetingDetector();
        var count = 0;
        d.MeetingDetected += _ => count++;

        d.PollOnce([Chrome]); d.PollOnce([Chrome]);
        d.Decline(Chrome);

        for (var blip = 0; blip < 5; blip++)
        {
            d.PollOnce([]);                            // off the mic for one poll
            for (var i = 0; i < 10; i++) d.PollOnce([Chrome]);
        }
        Assert.Equal(1, count);
    }

    [Fact]
    public void A_later_call_in_the_same_app_may_ask_again()
    {
        // The refusal is scoped to the CALL, not to the app forever. Once the app has genuinely left
        // the mic, the next meeting is a new question.
        var d = new MeetingDetector();
        var count = 0;
        d.MeetingDetected += _ => count++;

        d.PollOnce([Chrome]); d.PollOnce([Chrome]);
        Assert.Equal(1, count);
        d.Decline(Chrome);

        for (var i = 0; i < 10; i++) d.PollOnce([]);   // call really over
        d.PollOnce([Chrome]); d.PollOnce([Chrome]);    // a new meeting starts
        Assert.Equal(2, count);
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
        d.PollOnce([Chrome]);   // back - reset
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
