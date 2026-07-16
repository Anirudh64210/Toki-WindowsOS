using Toki.State;

namespace Toki.Tests;

public class AppStateTests
{
    [Fact]
    public void StartsHidden()
    {
        Assert.Equal(PillState.Hidden, new AppState().Current);
    }

    [Fact]
    public void HiddenCanOpenHome()
    {
        var s = new AppState();
        Assert.True(s.TryGo(PillState.Home));
        Assert.Equal(PillState.Home, s.Current);
    }

    [Fact]
    public void HiddenCannotJumpToNotes()
    {
        var s = new AppState();
        Assert.False(s.TryGo(PillState.Notes));
        Assert.Equal(PillState.Hidden, s.Current);
    }

    [Fact]
    public void InvalidTransitionRaisesNoEvent()
    {
        var s = new AppState();
        var fired = false;
        s.StateChanged += (_, _) => fired = true;
        s.TryGo(PillState.Summarizing);
        Assert.False(fired);
    }

    [Fact]
    public void StartsHiddenAndCanOpenHomeOrPrompt()
    {
        var s = new AppState();
        Assert.Equal(PillState.Hidden, s.Current);
        Assert.True(s.CanGo(PillState.Home));
        Assert.True(s.CanGo(PillState.MeetingPrompt));
    }

    [Fact]
    public void FullHappyPathFromPromptToNotes()
    {
        var s = new AppState();
        Assert.True(s.TryGo(PillState.MeetingPrompt));
        Assert.True(s.TryGo(PillState.RecordingBar));
        Assert.True(s.TryGo(PillState.RecordingOpen));
        Assert.True(s.TryGo(PillState.RecordingBar));
        Assert.True(s.TryGo(PillState.Summarizing));
        Assert.True(s.TryGo(PillState.Notes));
        Assert.True(s.TryGo(PillState.Hidden));
    }

    [Fact]
    public void DismissingPromptReturnsToHidden()
    {
        var s = new AppState();
        s.TryGo(PillState.MeetingPrompt);
        Assert.True(s.TryGo(PillState.Hidden));
        Assert.False(s.CanGo(PillState.RecordingBar));
    }

    [Fact]
    public void SummarizingCannotReturnToRecording()
    {
        var s = new AppState();
        s.ForceForDemo(PillState.Summarizing);
        Assert.False(s.TryGo(PillState.RecordingBar));
        Assert.False(s.TryGo(PillState.RecordingOpen));
    }

    [Fact]
    public void EnteringRecordingStampsWallClockStart()
    {
        var s = new AppState();
        Assert.Null(s.RecordingStartedAt);
        var before = DateTimeOffset.UtcNow;
        s.TryGo(PillState.MeetingPrompt);
        s.TryGo(PillState.RecordingBar);
        Assert.NotNull(s.RecordingStartedAt);
        Assert.InRange(s.RecordingStartedAt!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void CollapsingAndExpandingKeepsTheSameStart()
    {
        var s = new AppState();
        s.TryGo(PillState.MeetingPrompt);
        s.TryGo(PillState.RecordingBar);
        var start = s.RecordingStartedAt;
        s.TryGo(PillState.RecordingOpen);
        s.TryGo(PillState.RecordingBar);
        Assert.Equal(start, s.RecordingStartedAt);
    }

    [Fact]
    public void StateChangedCarriesOldAndNew()
    {
        var s = new AppState();
        (PillState Old, PillState New)? seen = null;
        s.StateChanged += (o, n) => seen = (o, n);
        s.TryGo(PillState.Home);
        Assert.Equal((PillState.Hidden, PillState.Home), seen);
    }

    [Fact]
    public void ForceForDemoBypassesRules()
    {
        var s = new AppState();
        s.ForceForDemo(PillState.Notes);
        Assert.Equal(PillState.Notes, s.Current);
    }

    [Fact]
    public void NoSpeechLeadsOnlyToHiddenOrHome()
    {
        var s = new AppState();
        s.ForceForDemo(PillState.NoSpeech);
        Assert.False(s.CanGo(PillState.Notes));
        Assert.False(s.CanGo(PillState.RecordingBar));
        Assert.True(s.CanGo(PillState.Hidden));
        Assert.True(s.CanGo(PillState.Home));
    }
}
