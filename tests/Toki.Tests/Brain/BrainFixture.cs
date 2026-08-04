using System.IO;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;

namespace Toki.Tests.Brain;

/// <summary>
/// The turn-type a question routes to, decided by the SAME deterministic predicates the pipeline
/// (NotesQa.AskStreamAsync + the upstream command check) runs, in the same order. Lets a large golden
/// set assert routing without loading the model.
/// </summary>
public enum Route
{
    Command,            // CommandRouter.TryMatch (navigation/action) — runs upstream, wins first
    SmallTalk,          // greetings/thanks/affection
    Persona,            // questions about Toki itself / meta / privacy
    UserFact,           // "call me X" — a fact to store
    Identity,           // "what's my name" — a profile read
    VoiceCreate,        // "schedule a call with Ana tomorrow at 3"
    MishearClarify,     // command-shaped but matched nothing
    Count,              // deterministic count
    ListMeetings,       // deterministic meeting list
    ListTasks,          // deterministic task list
    IdentifyMeeting,    // "which/oldest/most-recent meeting" — DB fact
    General,            // RAG-vs-parametric: not about the meetings → model's own knowledge
    Grounded,           // LookupFact / SummarizeOne / Analyze grounded in retrieved notes
}

/// <summary>
/// A seeded, deterministic meeting corpus + a frozen reference clock, plus the <see cref="RouteOf"/>
/// classifier — the substrate for the 1000+ prompt golden set. All data is synthetic and local.
/// </summary>
public sealed class BrainFixture : IDisposable
{
    /// <summary>Frozen "now" for temporal parsing (a Wednesday). This week = Mon Aug 3 – Sun Aug 9, 2026.</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-brain-{Guid.NewGuid():N}.db");
    public MeetingStore Store { get; }

    // Meeting ids by nickname, for scope assertions.
    public long Pricing, Postgres, Onboarding, Standup, Kubernetes, OldSync, Retro;

    public BrainFixture()
    {
        Store = new MeetingStore(_dbPath);
        Seed();
    }

    MeetingRecord M(DateTimeOffset at, string title, string tldr, string transcript, params ActionItem[] actions) => new()
    {
        Kind = MeetingKind.Meeting, Title = title, StartedAt = at, DurationMs = 20 * 60000,
        Notes = new NoteSet { Tldr = tldr, Actions = actions.ToList() },
        Transcript =
        [
            new TranscriptSegment(Speaker.You, 0, 5000, transcript),
            new TranscriptSegment(Speaker.Them, 5000, 9000, tldr),
        ],
    };

    static ActionItem A(string text, string? owner = null, string? due = null, bool done = false)
        => new() { Text = text, Owner = owner, Due = due, Done = done };

    void Seed()
    {
        var mon = Now.Date.AddDays(-((int)Now.DayOfWeek + 6) % 7);   // Monday of this week (Aug 3)
        DateTimeOffset On(int daysFromMon, int hour = 10) => new DateTimeOffset(mon.AddDays(daysFromMon).AddHours(hour), Now.Offset);

        // This week's meetings.
        Pricing = Store.Save(M(On(0), "Pricing sync",
            "We settled on the one-time $20 price with no subscription.",
            "So on pricing, we agreed the one-time twenty dollar price, no subscription, keep it simple.",
            A("Send the pricing deck to Ana", "You", "Friday"),
            A("Confirm the brand font and hex", "Priya", "tomorrow"),
            A("Draft the launch email", "You")));                       // undated, this week

        Standup = Store.Save(M(On(1), "Monday standup",
            "Team status; the migration is the main blocker this sprint.",
            "Quick standup. Dev's blocked on the postgres migration, everyone else on track.",
            A("Unblock the migration", "Dev", "last Monday"),           // overdue
            A("Update the roadmap", "You", "next Tuesday")));           // next week

        Postgres = Store.Save(M(On(2), "Postgres migration review",
            "Reviewed the postgres migration plan and replication lag risk.",
            "On postgres, the migration plan looks solid but replication lag is a worry. Raj raised it.",
            A("Benchmark replication lag", "Raj", "next Friday"),
            A("Write the migration runbook", "Dev")));

        // Earlier this year.
        Onboarding = Store.Save(M(new DateTimeOffset(2026, 4, 12, 10, 0, 0, Now.Offset), "Optimize onboarding experience",
            "Analyzed onboarding flow; scheduled usability tests.",
            "The onboarding flow has drop-off at step three. We'll run usability tests with beta users.",
            A("Schedule usability test with beta users", null, "2024-04-10"),
            A("Compile customer feedback summary", null)));

        Kubernetes = Store.Save(M(new DateTimeOffset(2026, 4, 10, 14, 0, 0, Now.Offset), "Kubernetes deal calls",
            "Shared insights from recent Kubernetes deal calls with the sales team.",
            "The Kubernetes deals keep hitting the same objection on pricing. Sales needs a playbook.",
            A("Share Kubernetes insights with sales", "You", "2024-04-10")));

        // Last week + older, for temporal windows and oldest/first.
        Retro = Store.Save(M(Now.Date.AddDays(-9), "Sprint retro",
            "Retro on the sprint; velocity dipped, too much context-switching.",
            "Retro time. Velocity dipped this sprint, mostly context switching. Let's cut WIP.",
            A("Cut work-in-progress limit", "Sarah")));

        OldSync = Store.Save(M(new DateTimeOffset(2026, 3, 2, 9, 0, 0, Now.Offset), "Q1 kickoff",
            "Kicked off Q1 goals and the roadmap.",
            "Q1 kickoff. We set the north-star metric and the quarterly roadmap.",
            A("Publish the Q1 roadmap", "You", "done", done: true)));   // done → not open
    }

    /// <summary>Classify a question to its pipeline route using the REAL predicates, in the documented
    /// order. <paramref name="screenTitle"/> = the note open on screen (null = none).</summary>
    public Route RouteOf(string q, ConversationContext? ctx = null, string? screenTitle = null, long? screenId = null)
    {
        // 0) Command (runs upstream in App.HandleNotchText). ShowTasks is forwarded into Ask, so it is
        //    NOT a terminal command route — everything else is.
        if (CommandRouter.TryMatch(q) is { } cmd && cmd != AppCommand.ShowTasks) return Route.Command;
        // 0.5–0.8 canned turn types, in order.
        if (SmallTalk.TryReply(q) is not null) return Route.SmallTalk;
        if (Persona.TryReply(q, screenTitle) is not null) return Route.Persona;
        if (UserFacts.TryCapture(q, ctx) is not null) return Route.UserFact;
        if (UserFacts.TryIdentity(q) is not null) return Route.Identity;
        if (CalendarCommand.TryDetect(q, Now) is not null) return Route.VoiceCreate;
        if (CommandRouter.TryNearMiss(q) is not null) return Route.MishearClarify;

        // 1) Understand + resolve (screen/follow-up), mirroring AskStreamAsync's precedence.
        var parsed = QueryUnderstanding.Parse(q, Now);
        var plan = parsed;
        var qaIntent = parsed.Intent is QueryIntent.SummarizeOne or QueryIntent.LookupFact or QueryIntent.ListTasks or QueryIntent.Analyze;
        if (screenId is long && qaIntent && !parsed.HasDateRange && parsed.Rank is null)
        {
            // Proximal-deixis / underspecified-with-open-note pinning is a scope refinement, not a
            // route change — the intent below still decides the route.
        }
        plan = (ctx is not null ? QueryUnderstanding.ResolveFollowUp(q, ctx, Now) : null) ?? parsed;

        // 2) Deterministic routes.
        switch (plan.Intent)
        {
            case QueryIntent.Count: return Route.Count;
            case QueryIntent.ListMeetings: return Route.ListMeetings;
            case QueryIntent.ListTasks: return Route.ListTasks;
        }
        // A recency-rank question ("oldest/most-recent meeting") is resolved deterministically to a
        // specific meeting (the real IdentifyMeetingAnswer either names it or summarizes it) — label it
        // IdentifyMeeting. The exact identify-vs-summarize split is an internal refinement not modelled here.
        if (plan.Rank is not null) return Route.IdentifyMeeting;

        // 2.5) RAG-vs-parametric.
        if (TurnRouter.IsGeneralTurn(plan, Store)) return Route.General;
        return Route.Grounded;
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }
}
