using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Toki.Calendar;
using Toki.Llm;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>Voice/text create-a-meeting: deterministic detection matrix + the confirm-before-create gate.
/// No mic, no model, no network — the create is a recording fake (<see cref="CalendarCommand.CreateOverride"/>).</summary>
[Collection("NotesQaSeams")]   // shares NotesQa's static seams — never parallel with the other brain tests
public sealed class CalendarCommandTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-cal-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;
    readonly AppSettings _settings = new();
    int _creates;
    NewEvent? _lastEvent;

    public CalendarCommandTests()
    {
        _store = new MeetingStore(_dbPath);
        NotesQa.StoreOverride = _store;
        NotesQa.LlmOverride = new FakeLlm();          // any fall-through turn ("yes" with no pending) stays fast
        UserProfile.SettingsOverride = _settings;
        CalendarCommand.ConnectedOverride = true;
        CalendarCommand.CreateOverride = (ev, ct) =>
        {
            _creates++; _lastEvent = ev;
            return Task.FromResult(new CalEvent("id", ev.Summary, ev.Start.ToUniversalTime(), ev.End.ToUniversalTime(),
                false, null, null, "https://cal", Array.Empty<CalAttendee>()));
        };
    }

    public void Dispose()
    {
        NotesQa.StoreOverride = null;
        NotesQa.LlmOverride = null;
        UserProfile.SettingsOverride = null;
        CalendarCommand.ConnectedOverride = null;
        CalendarCommand.CreateOverride = null;
        try { File.Delete(_dbPath); } catch { }
    }

    static readonly DateTimeOffset Now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero); // Mon Aug 3, 9am

    // ── detection matrix ──

    [Theory]
    [InlineData("schedule a call with Ana tomorrow at 3")]
    [InlineData("hey doki, book a meeting with the design team friday at 10am")]
    [InlineData("put lunch with sam on my calendar tomorrow at noon")]
    [InlineData("set up a sync monday 9am")]
    [InlineData("remind me to send the deck tomorrow at 5")]
    [InlineData("can you schedule a call with ana tomorrow at 3")]
    public void Detects_scheduling_commands(string q) => Assert.NotNull(CalendarCommand.TryDetect(q, Now));

    [Theory]
    [InlineData("what meetings do I have tomorrow")]   // question veto
    [InlineData("when is my call with Ana")]
    [InlineData("did we schedule the follow-up")]
    [InlineData("summarize the planning meeting")]
    [InlineData("who set up the meeting yesterday")]
    [InlineData("should we schedule another sync")]
    [InlineData("add a new note in the library")]      // note veto → NewNote's job
    [InlineData("we scheduled a call for friday")]     // past narration, no imperative lead
    public void Ignores_questions_and_non_creates(string q) => Assert.Null(CalendarCommand.TryDetect(q, Now));

    [Fact]
    public void Extracts_title_time_and_person()
    {
        var d = CalendarCommand.TryDetect("schedule a call with Ana tomorrow at 3", Now)!;
        Assert.Equal("Call with Ana", d.Title);
        Assert.NotNull(d.Start);
        Assert.Equal(4, d.Start!.Value.Day);          // tomorrow
        Assert.Equal(15, d.Start!.Value.Hour);        // "at 3" → 3 PM (business-hours reading)
        Assert.Equal("Ana", d.PersonMentioned);
        Assert.Empty(d.Emails);                        // a spoken name is NEVER an email
    }

    [Fact]
    public void Yes_no_matchers_are_tight()
    {
        Assert.True(CalendarCommand.IsYes("yes"));
        Assert.True(CalendarCommand.IsYes("do it"));
        Assert.True(CalendarCommand.IsYes("Okay."));
        Assert.True(CalendarCommand.IsNo("no"));
        Assert.True(CalendarCommand.IsNo("cancel"));
        Assert.False(CalendarCommand.IsYes("yesterday"));
        Assert.False(CalendarCommand.IsNo("notes"));
    }

    // ── the confirm gate ──

    [Fact]
    public async Task Turn_one_never_creates_and_arms_a_pending_confirm()
    {
        var ctx = new ConversationContext();
        var a1 = await NotesQa.AskAsync("schedule a call with Ana tomorrow at 3", ctx);
        Assert.Equal(0, _creates);
        Assert.NotNull(ctx.PendingCreate);
        Assert.Contains("yes", a1.Text);
    }

    [Fact]
    public async Task Yes_creates_exactly_once_with_honest_fields()
    {
        var ctx = new ConversationContext();
        await NotesQa.AskAsync("schedule a call with Ana tomorrow at 3", ctx);
        var a2 = await NotesQa.AskAsync("yes", ctx);
        Assert.Equal(1, _creates);
        Assert.Null(ctx.PendingCreate);
        Assert.StartsWith("Done", a2.Text);
        Assert.Contains("Ana", _lastEvent!.Summary);
        Assert.False(_lastEvent.AddMeet);
        Assert.Empty(_lastEvent.AttendeeEmails ?? new List<string>());
    }

    [Fact]
    public async Task No_cancels_and_creates_nothing()
    {
        var ctx = new ConversationContext();
        await NotesQa.AskAsync("book a meeting with sam tomorrow at 4", ctx);
        var a = await NotesQa.AskAsync("no", ctx);
        Assert.Equal(0, _creates);
        Assert.Null(ctx.PendingCreate);
        Assert.Contains("scrapped", a.Text);
    }

    [Fact]
    public async Task Stray_yes_after_an_unrelated_turn_never_creates()
    {
        var ctx = new ConversationContext();
        await NotesQa.AskAsync("set up a sync monday 9am", ctx);
        await NotesQa.AskAsync("thanks", ctx);        // unrelated → tier 0.4 clears the pending
        Assert.Null(ctx.PendingCreate);
        await NotesQa.AskAsync("yes", ctx);           // nothing pending → nothing to create
        Assert.Equal(0, _creates);
    }

    [Fact]
    public async Task No_time_asks_when_then_confirms_then_creates()
    {
        var ctx = new ConversationContext();
        var a1 = await NotesQa.AskAsync("schedule a call with Ana", ctx);
        Assert.Contains("When", a1.Text);
        Assert.NotNull(ctx.PendingCreate);
        Assert.Null(ctx.PendingCreate!.Start);

        var a2 = await NotesQa.AskAsync("tomorrow at 3", ctx);
        Assert.Contains("yes", a2.Text);              // now a confirm prompt
        Assert.Equal(0, _creates);                    // still not created — awaiting confirm

        await NotesQa.AskAsync("yes", ctx);
        Assert.Equal(1, _creates);
    }

    // ── multi-turn slot-fill (the owner's "actually make it 6 to 7pm tomorrow") ──

    [Fact]
    public async Task Correction_before_yes_updates_the_time_then_creates_the_new_one()
    {
        var ctx = new ConversationContext();
        await NotesQa.AskAsync("schedule a call with Ana tomorrow at 3", ctx);   // confirm armed at 3pm
        var a2 = await NotesQa.AskAsync("actually make it 6 to 7pm tomorrow", ctx);
        Assert.Equal(0, _creates);                                    // NOT created — still confirming
        Assert.NotNull(ctx.PendingCreate);
        Assert.Contains("yes", a2.Text);                             // re-confirm, not lost
        Assert.Equal(18, ctx.PendingCreate!.Start!.Value.ToLocalTime().Hour);
        Assert.Equal(19, ctx.PendingCreate!.End!.Value.ToLocalTime().Hour);

        await NotesQa.AskAsync("yes", ctx);
        Assert.Equal(1, _creates);
        Assert.Equal(18, _lastEvent!.Start.ToLocalTime().Hour);       // created with the CORRECTED time
        Assert.Equal(19, _lastEvent!.End.ToLocalTime().Hour);
    }

    [Fact]
    public async Task Title_correction_before_yes_renames_the_draft()
    {
        var ctx = new ConversationContext();
        await NotesQa.AskAsync("schedule a meeting tomorrow at 3", ctx);
        var a = await NotesQa.AskAsync("call it design sync", ctx);
        Assert.Equal(0, _creates);
        Assert.Contains("Design sync", a.Text);                      // re-confirm shows the new title
        await NotesQa.AskAsync("yes", ctx);
        Assert.Equal(1, _creates);
        Assert.Contains("Design sync", _lastEvent!.Summary);
    }

    [Fact]
    public async Task Yes_with_a_correction_is_a_change_not_a_create()
    {
        var ctx = new ConversationContext();
        await NotesQa.AskAsync("schedule a call tomorrow at 3", ctx);
        var a = await NotesQa.AskAsync("yes but make it 4pm tomorrow", ctx);
        Assert.Equal(0, _creates);                                    // "yes but …" isn't a bare yes → a change
        Assert.NotNull(ctx.PendingCreate);
        Assert.Equal(16, ctx.PendingCreate!.Start!.Value.ToLocalTime().Hour);
        await NotesQa.AskAsync("yes", ctx);
        Assert.Equal(1, _creates);
        Assert.Equal(16, _lastEvent!.Start.ToLocalTime().Hour);
    }

    [Fact]
    public async Task Not_connected_is_honest_and_never_creates()
    {
        CalendarCommand.ConnectedOverride = false;
        var ctx = new ConversationContext();
        var a = await NotesQa.AskAsync("schedule a call with Ana tomorrow at 3", ctx);
        Assert.Contains("connect", a.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ctx.PendingCreate);               // no pending armed when we can't create
        await NotesQa.AskAsync("yes", ctx);
        Assert.Equal(0, _creates);
    }

    sealed class FakeLlm : ILlm
    {
        public bool Ready => true;
        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default) => Task.FromResult("ok");
        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<string>? stop = null)
        { yield return "ok"; await Task.CompletedTask; }
    }
}
