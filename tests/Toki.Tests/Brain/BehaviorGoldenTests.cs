using System.IO;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests.Brain;

/// <summary>
/// End-to-end behavior golden set (taxonomy §4, §6, §14) through the real Ask pipeline with a seeded
/// store + a fake model - the HONESTY rails, person/self filters, and no-fabrication traps. A fake LLM
/// records whether it was even invoked, so "an honest miss never reaches the model" is checkable.
/// </summary>
[Collection("NotesQaSeams")]
public sealed class BehaviorGoldenTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-beh-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;
    readonly FakeLlm _llm = new();

    public BehaviorGoldenTests()
    {
        _store = new MeetingStore(_dbPath);
        NotesQa.StoreOverride = _store;
        NotesQa.LlmOverride = _llm;
        UserProfile.SettingsOverride = new AppSettings();
        Seed();
    }

    public void Dispose()
    {
        NotesQa.StoreOverride = null;
        NotesQa.LlmOverride = null;
        UserProfile.SettingsOverride = null;
        try { File.Delete(_dbPath); } catch { }
    }

    sealed class FakeLlm : ILlm
    {
        public string? User;
        public bool Ready => true;
        public Task<string> CompleteAsync(string s, string u, int m, CancellationToken ct = default) => Task.FromResult("ok");
        public async IAsyncEnumerable<string> StreamAsync(string s, string u, int m,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default, IReadOnlyList<string>? stop = null)
        { User = u; yield return "Here's the answer from the notes."; await Task.CompletedTask; }
    }

    MeetingRecord M(string title, string tldr, params ActionItem[] a) => new()
    {
        Kind = MeetingKind.Meeting, Title = title, StartedAt = DateTimeOffset.Now.AddDays(-2),
        Notes = new NoteSet { Tldr = tldr, Actions = a.ToList() },
        Transcript = [new TranscriptSegment(Speaker.You, 0, 900, tldr)],
    };

    void Seed()
    {
        _store.Save(M("Pricing sync", "We agreed the one-time $20 price, no subscription.",
            new ActionItem { Text = "Send the pricing deck", Owner = "Ana" },
            new ActionItem { Text = "Draft the launch email", Owner = "You" }));
        _store.Save(M("Postgres migration", "Reviewed the migration; replication lag is the risk.",
            new ActionItem { Text = "Write the runbook", Owner = "Dev" }));
    }

    static Task<NotesQa.Answer> Ask(string q)
        => NotesQa.AskStreamAsync(q, _ => { }, _ => { }, default, null, concise: false, screenMeetingId: null);

    // ── §14.1/14.6 fabrication trap: absent topic → honest miss, NO citations, model NOT invoked ──
    [Theory]
    [InlineData("what did we decide about the berlin office")]
    [InlineData("what did we say about the merger")]
    [InlineData("summarize the budget meeting")]
    [InlineData("what happened with the layoffs")]
    public async Task AbsentTopic_IsHonestMiss_NoCitations(string q)
    {
        _llm.User = null;
        var ans = await Ask(q);
        Assert.Empty(ans.Sources);                                   // no fake citations on a miss
        Assert.DoesNotContain("$20", ans.Text);                      // never leaks an unrelated fact
    }

    // ── §14.6 real person + absent topic: don't fabricate a quote ──
    [Fact]
    public async Task RealPerson_AbsentTopic_DoesNotFabricate()
    {
        // (The first assert here used to be a tautology - its haystack was always "x" or "" - so
        // for months this test survived on the Sources check alone. Found by the 2026-08-18 test
        // audit. Now it asserts the actual honesty property: no invented Ana-said-X content.)
        var ans = await Ask("what did Ana say about the merger");
        Assert.Empty(ans.Sources);                                        // a miss cites nothing
        Assert.DoesNotContain("Ana said", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$20", ans.Text);                           // no unrelated fact leaks in
    }

    // ── §6.2 existence yes/no honesty: present topic answers, absent topic honestly denies ──
    [Fact]
    public async Task PresentTopic_Answers_FromTheRightMeeting()
    {
        var ans = await Ask("what did we decide about pricing");
        // A grounded topic reaches the model WITH the pricing meeting in the prompt.
        Assert.NotNull(_llm.User);
        Assert.Contains("Pricing sync", _llm.User!);
    }

    // ── §3.6 person task filter: only that person's items ──
    [Fact]
    public async Task PersonTasks_FilterToThatPerson()
    {
        var ans = await Ask("what are Ana's open tasks");
        Assert.Contains("pricing deck", ans.Text);
        Assert.DoesNotContain("runbook", ans.Text);      // Dev's
        Assert.DoesNotContain("launch email", ans.Text); // You's
    }

    // ── §14.8 self vs owner: "assigned to me" lists only You's items ──
    [Fact]
    public async Task AssignedToMe_ListsOnlyYours()
    {
        var ans = await Ask("what's assigned to me");
        Assert.Contains("launch email", ans.Text);
        Assert.DoesNotContain("pricing deck", ans.Text);  // Ana's
    }

    // ── unknown person → honest miss, model not invoked ──
    [Fact]
    public async Task UnknownPerson_IsHonestMiss()
    {
        _llm.User = null;
        var ans = await Ask("what are Zoltan's open tasks");
        Assert.DoesNotContain("pricing deck", ans.Text);   // never dumps a real person's items as theirs
    }

    // ── §12 general knowledge: no forced retrieval, no citations ──
    [Fact]
    public async Task GeneralKnowledge_NoCitations()
    {
        var ans = await Ask("what does OKR mean");
        Assert.Empty(ans.Sources);
    }

    // ── THE reported bug, end-to-end: "what tasks did I have on <a day with a meeting>" lists that
    //    day's tasks (provenance), NOT an overdue dump of every task from every meeting. ──
    [Fact]
    public async Task TasksOnASpecificPastDay_AreProvenance_NotAnOverdueDump()
    {
        // A meeting on a specific past day with its own task; plus an unrelated old overdue task.
        var day = DateTimeOffset.Now.AddDays(-3);
        _store.Save(new MeetingRecord
        {
            Kind = MeetingKind.Meeting, Title = "Day-X planning", StartedAt = day,
            Notes = new NoteSet { Tldr = "Planned the launch.", Actions = { new ActionItem { Text = "Book the venue", Owner = "You" } } },
            Transcript = [new TranscriptSegment(Speaker.You, 0, 900, "planning")],
        });
        _store.Save(new MeetingRecord
        {
            Kind = MeetingKind.Meeting, Title = "Ancient sync", StartedAt = DateTimeOffset.Now.AddDays(-40),
            Notes = new NoteSet { Tldr = "old", Actions = { new ActionItem { Text = "Old overdue chore", Owner = "You", Due = "2024-01-01" } } },
            Transcript = [new TranscriptSegment(Speaker.You, 0, 900, "old")],
        });

        var ans = await Ask($"what tasks did I have on {day:MMMM d}");

        Assert.Contains("Book the venue", ans.Text);          // that day's task
        Assert.DoesNotContain("Old overdue chore", ans.Text); // NOT an overdue dump from other days
        Assert.DoesNotContain("Overdue", ans.Text);           // not the deadline-bucket header
    }

    // ── §5.5 data-privacy answered accurately (local-only), never retrieved ──
    [Theory]
    [InlineData("are you sending my data to the cloud")]
    [InlineData("is this private")]
    [InlineData("where does my data go")]
    public async Task DataPrivacy_SaysLocalOnly_NoRetrieval(string q)
    {
        _llm.User = null;
        var ans = await Ask(q);
        Assert.Empty(ans.Sources);
        Assert.Null(_llm.User);                       // never reached the model
        Assert.Contains("this PC", ans.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── §5.5 always-listening answered accurately (no always-on mic), never retrieved ──
    [Theory]
    [InlineData("are you always listening")]
    [InlineData("is the mic always on")]
    public async Task Listening_SaysNoAlwaysOn_NoRetrieval(string q)
    {
        _llm.User = null;
        var ans = await Ask(q);
        Assert.Empty(ans.Sources);
        Assert.Null(_llm.User);
        Assert.Contains("no always-on", ans.Text, StringComparison.OrdinalIgnoreCase);
    }
}
