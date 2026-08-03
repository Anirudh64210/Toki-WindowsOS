using System.IO;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// End-to-end tests of the Ask pipeline's DETERMINISTIC routes (routing, person filter, screen pin,
/// honest miss, persona) against a temp-file store via <see cref="NotesQa.StoreOverride"/>. These
/// paths never touch the LLM or the user's real database/settings. Each screenshot bug from the
/// owner's live session is a regression test here.
/// </summary>
[Collection("NotesQaSeams")]   // NotesQa.StoreOverride/LlmOverride are static — never run the two fixtures that set them in parallel
public sealed class NotesQaTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-qa-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;

    public NotesQaTests()
    {
        _store = new MeetingStore(_dbPath);
        NotesQa.StoreOverride = _store;
        // In-memory profile: tests never read or write the real %APPDATA%\Toki\settings.json.
        UserProfile.SettingsOverride = new AppSettings();
    }

    public void Dispose()
    {
        NotesQa.StoreOverride = null;
        NotesQa.LlmOverride = null;
        UserProfile.SettingsOverride = null;
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>Fake model: records what the pipeline asked of it (prompts, budget, stops) and streams
    /// a canned answer — so the ANALYZE route (breadth, prompt shape, budget) is testable end-to-end
    /// without loading the real model or touching the user's settings.</summary>
    sealed class FakeLlm : ILlm
    {
        public string? System, User;
        public int MaxTokens;
        public IReadOnlyList<string>? Stop;
        public bool Ready => true;
        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
            => Task.FromResult("ok");
        public async IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        {
            System = system; User = user; MaxTokens = maxTokens; Stop = stop;
            yield return "Here's my read: the work is on track overall.";
            await Task.CompletedTask;
        }
    }

    static MeetingRecord Meeting(string title, string summary, params ActionItem[] actions) => new()
    {
        Kind = MeetingKind.Meeting,
        Title = title,
        StartedAt = DateTimeOffset.Now,
        Notes = new NoteSet { Tldr = summary, Actions = actions.ToList() },
        Transcript = [new TranscriptSegment(Speaker.You, 0, 900, summary)],
    };

    static Task<NotesQa.Answer> Ask(string q, long? screenId = null, ConversationContext? ctx = null)
        => NotesQa.AskStreamAsync(q, _ => { }, _ => { }, default, ctx, concise: false, screenMeetingId: screenId);

    // ── screenshot bug #1: topical person question dumped as "Your 10 most recent meetings" ──
    [Fact]
    public async Task TopicalPersonQuestion_IsAnHonestMiss_NotAMeetingList()
    {
        for (var i = 0; i < 10; i++)
            _store.Save(Meeting($"Sync {i}", "We reviewed the sprint and the MR rate."));

        var ans = await Ask("What was discussed about Kai Sinat in my meetings?");

        Assert.DoesNotContain("most recent meeting", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("couldn't find", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ans.Sources);   // no fake citations on a miss
    }

    // ── honest miss needs NO model (resolution runs before the model check) ──
    [Fact]
    public async Task LookupMiss_IsHonest_WithoutAModel()
    {
        _store.Save(Meeting("Budget planning", "We set the Q3 budget."));
        var ans = await Ask("what did we decide about kangaroos");
        Assert.Contains("couldn't find", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(ans.Ok);
    }

    // ── screenshot bug #3: "what did Mac have as action items in this meeting?" listed ALL items ──
    [Fact]
    public async Task PersonFilter_ListsOnlyThatPersonsItems()
    {
        var m = Meeting("Quality sync", "MR rate and quality.",
            new ActionItem { Text = "Update the MR dashboard", Owner = "Mac" },
            new ActionItem { Text = "Ship the rate fix", Owner = "Mac" },
            new ActionItem { Text = "Write the survey", Owner = "Sid" },
            new ActionItem { Text = "Book the review", Owner = "Sid" },
            new ActionItem { Text = "Refresh the wiki", Owner = "Priya" });
        var id = _store.Save(m);

        var ans = await Ask("What did Mac have as action items in this meeting?", screenId: id);

        Assert.Contains("Mac", ans.Text);
        Assert.Contains("MR dashboard", ans.Text);
        Assert.Contains("rate fix", ans.Text);
        Assert.DoesNotContain("survey", ans.Text);
        Assert.DoesNotContain("wiki", ans.Text);
    }

    [Fact]
    public async Task PersonWithNoItemsInScope_IsAnHonestMiss_NamingRealOwners()
    {
        var a = _store.Save(Meeting("Design review", "Screens.",
            new ActionItem { Text = "Fix the nav", Owner = "Sid" }));
        _store.Save(Meeting("Data sync", "Pipelines.",
            new ActionItem { Text = "Backfill events", Owner = "Raj" }));   // Raj exists, elsewhere

        var ans = await Ask("what did Raj have as action items in this meeting", screenId: a);

        Assert.Contains("No action items for Raj", ans.Text);
        Assert.Contains("Sid", ans.Text);   // says who actually owns the items there
    }

    [Fact]
    public async Task UnknownName_NeverTriggersThePersonFilter()
    {
        var id = _store.Save(Meeting("Planning", "Roadmap.",
            new ActionItem { Text = "Draft the roadmap", Owner = "Sid" },
            new ActionItem { Text = "Cost the cloud plan", Owner = "Mac" }));

        // "Zorblax" matches no known owner → ordinary scoped list, ALL items (no silent wrong filter).
        var ans = await Ask("what did Zorblax have as action items in this meeting", screenId: id);
        Assert.Contains("roadmap", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cloud plan", ans.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalPersonTasks_FiltersAcrossMeetings()
    {
        _store.Save(Meeting("Sync A", "First.",
            new ActionItem { Text = "Ship the installer", Owner = "Mac" },
            new ActionItem { Text = "Send the recap", Owner = "Sid" }));
        _store.Save(Meeting("Sync B", "Second.",
            new ActionItem { Text = "File the report", Owner = "Mac", Done = true },   // done → not open
            new ActionItem { Text = "Test the build", Owner = "Mac" }));

        var ans = await Ask("what are Mac's open tasks");

        Assert.Contains("installer", ans.Text);
        Assert.Contains("Test the build", ans.Text);
        Assert.DoesNotContain("recap", ans.Text);          // Sid's
        Assert.DoesNotContain("File the report", ans.Text); // done
    }

    // ── screenshot bug #2: screen pin must beat conversation memory AND retrieval ──
    [Fact]
    public async Task ScreenPin_BeatsConversationMemory()
    {
        var a = _store.Save(Meeting("SUS Metrics and MR Rate Trends", "SUS metrics.",
            new ActionItem { Text = "Chart the SUS trend", Owner = "Sid" }));
        var b = _store.Save(Meeting("MR Rate Target and Quality Balance", "MR targets.",
            new ActionItem { Text = "Set the MR target", Owner = "Mac" }));

        // Conversation was about meeting A; the note OPEN ON SCREEN is B → "this meeting" = B.
        var ctx = new ConversationContext { LastIntent = QueryIntent.SummarizeOne, LastMeetingIds = [a] };
        var ans = await Ask("what are the action items in this meeting", screenId: b, ctx: ctx);

        Assert.Contains("MR Rate Target and Quality Balance", ans.Text);
        Assert.Contains("MR target", ans.Text);
        Assert.DoesNotContain("SUS trend", ans.Text);
        Assert.Single(ans.Sources);
        Assert.Equal(b, ans.Sources[0].Id);
    }

    // ── screenshot bug #4: meta questions must not be retrieval ──
    [Fact]
    public async Task WhyAreYouSoRude_IsPersona_NotRetrieval()
    {
        _store.Save(Meeting("Standup", "Someone was rude in the standup."));   // bait for retrieval
        var ans = await Ask("why are you so rude?");
        Assert.Contains("sorry", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("standup", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ans.Sources);
    }

    [Fact]
    public async Task DoYouSeeMyScreen_AffirmsAndNamesTheOpenNote()
    {
        var id = _store.Save(Meeting("MR Rate Target and Quality Balance", "Targets."));
        var ans = await Ask("do you see what's on my screen right now?", screenId: id);
        Assert.Contains("MR Rate Target and Quality Balance", ans.Text);
    }

    // ── self-reference: "assigned to ME" means the user (owner "You"), never someone else's item ──
    // (The live screenshot bug: "Was anything assigned to me?" listed an item owned by "Lily, Mac".)
    [Fact]
    public async Task AssignedToMe_WithNoSelfItems_IsHonest_NamingTheRealOwners()
    {
        var id = _store.Save(Meeting("Simplify Community MR Rate Metric", "MR metric work.",
            new ActionItem { Text = "Implement the simplified KPI transition", Owner = "Lily, Mac" }));

        var ans = await Ask("Was anything assigned to me in this meeting?", screenId: id);

        Assert.Contains("assigned to you", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing", ans.Text);
        Assert.Contains("Lily, Mac", ans.Text);                        // says who actually owns it
        Assert.DoesNotContain("• Implement", ans.Text);                // never listed as if it were yours
    }

    [Fact]
    public async Task AssignedToMe_ListsOnlyYourItems()
    {
        var id = _store.Save(Meeting("Quality sync", "MR rate and quality.",
            new ActionItem { Text = "Send the follow-up survey", Owner = "You" },
            new ActionItem { Text = "Update the dashboard", Owner = "Mac" }));

        var ans = await Ask("was anything assigned to me in this meeting", screenId: id);

        Assert.Contains("You have 1 action item", ans.Text);
        Assert.Contains("follow-up survey", ans.Text);
        Assert.DoesNotContain("dashboard", ans.Text);
    }

    [Fact]
    public async Task GlobalSelfTasks_FilterToYou_AcrossMeetings()
    {
        _store.Save(Meeting("Sync A", "First.",
            new ActionItem { Text = "Draft the launch email", Owner = "You" },
            new ActionItem { Text = "Fix the pipeline", Owner = "Raj" }));
        _store.Save(Meeting("Sync B", "Second.",
            new ActionItem { Text = "Review the copy", Owner = "Me" }));

        var ans = await Ask("what's assigned to me?");

        Assert.Contains("launch email", ans.Text);
        Assert.Contains("Review the copy", ans.Text);
        Assert.DoesNotContain("pipeline", ans.Text);   // Raj's
    }

    [Fact]
    public async Task GlobalSelfTasks_WithNoneOfYours_IsHonest_NamingOwners()
    {
        _store.Save(Meeting("Sync", "Stuff.",
            new ActionItem { Text = "Ship the fix", Owner = "Mac" }));

        var ans = await Ask("is anything assigned to me?");

        Assert.Contains("Nothing", ans.Text);
        Assert.Contains("Mac", ans.Text);
        Assert.DoesNotContain("Ship the fix", ans.Text);
    }

    // ── multi-owner values ("Lily, Mac") resolve per person for the by-name filter too ──
    [Fact]
    public async Task MultiOwnerItem_MatchesEachNamedPerson()
    {
        _store.Save(Meeting("KPI sync", "KPIs.",
            new ActionItem { Text = "Implement the KPI transition", Owner = "Lily, Mac" },
            new ActionItem { Text = "Write the recap", Owner = "Sid" }));

        var ans = await Ask("what are Mac's open tasks");

        Assert.Contains("KPI transition", ans.Text);
        Assert.DoesNotContain("recap", ans.Text);
    }

    // ── the ANALYZE route: breadth + reasoning prompt + real token budget ──
    [Fact]
    public async Task AnalyticalQuestion_ReadsAcrossManyMeetings_WithAReasoningBudget()
    {
        for (var i = 1; i <= 5; i++)
            _store.Save(Meeting($"SLO review {i}", $"We discussed the SLO error budget and the {i} rollout."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what are the main themes across my SLO meetings");

        Assert.True(ans.Ok);
        Assert.Contains("read", ans.Text);                       // the fake's streamed answer came through
        Assert.True(ans.Sources.Count >= 4,                      // breadth: NOT the lookup top-3
            $"expected 4+ source meetings for a synthesis question, got {ans.Sources.Count}");
        Assert.True(fake.MaxTokens >= 400,                       // room to actually think, not the 170-token lookup cap
            $"expected an analysis token budget, got {fake.MaxTokens}");
        Assert.Contains("SLO review 1", fake.User);              // the prompt really carries several meetings
        Assert.Contains("SLO review 4", fake.User);
        Assert.Contains("analysis", fake.User, StringComparison.OrdinalIgnoreCase);   // the reasoning prompt, not the lookup one
    }

    [Fact]
    public async Task AnalyticalQuestion_WithNoTopicAnchor_UsesRecentMeetings_NeverADeadEnd()
    {
        for (var i = 1; i <= 4; i++)
            _store.Save(Meeting($"Weekly sync {i}", "Progress on the rollout and the hiring plan."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what should I focus on?");

        Assert.True(ans.Ok);
        Assert.True(ans.Sources.Count >= 3, $"expected the recent-meeting pool, got {ans.Sources.Count}");
        Assert.DoesNotContain("couldn't find", ans.Text, StringComparison.OrdinalIgnoreCase);
    }

    // A factual lookup keeps its narrow top-3 + honest deterministic miss (no regression from Analyze).
    [Fact]
    public async Task FactualLookup_KeepsTheNarrowPath_AndHonestMiss()
    {
        for (var i = 1; i <= 5; i++)
            _store.Save(Meeting($"Sync {i}", "We reviewed the sprint."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what did we decide about kangaroos");

        Assert.Contains("couldn't find", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fake.User);   // an honest miss never reaches the model
    }

    // ── person-matcher unit behaviour ──
    [Theory]
    [InlineData("what did mac have as action items", "Mac")]           // case-insensitive
    [InlineData("what are Mac's open tasks", "Mac")]                   // possessive
    [InlineData("did priya commit to anything", "Priya Kumar")]        // first name of a full name
    public void MatchPeople_ResolvesAgainstKnownOwners(string text, string expected)
        => Assert.Contains(expected, NotesQa.MatchPeople(new[] { "Mac", "Sid", "Priya Kumar" }, text));

    [Fact]
    public void MatchPeople_IgnoresPseudoOwnersAndUnknowns()
    {
        Assert.Empty(NotesQa.MatchPeople(new[] { "You", "me" }, "what did you have to do"));
        Assert.Empty(NotesQa.MatchPeople(new[] { "Mac" }, "what did zorblax say"));
    }

    // ═══ P9.1 — the live-run failure cluster ═══

    static MeetingRecord MeetingOn(DateTimeOffset at, string title, string summary, params ActionItem[] actions)
    {
        var m = Meeting(title, summary, actions);
        m.StartedAt = at;
        return m;
    }

    // ── B: self-reference breadth ("did they mention me or give me any tasks?" listed ALL 5 items) ──
    [Fact]
    public async Task MentionMeOrGiveMeTasks_IsSelfScoped_HonestMiss()
    {
        var id = _store.Save(Meeting("SUS Metrics and MR Rate Trends", "Metrics work.",
            new ActionItem { Text = "track narrow MR rate vs. prior-year data", Owner = "Christopher" },
            new ActionItem { Text = "revise SUS metrics to include security work", Owner = "Ted" },
            new ActionItem { Text = "prioritize security work in sprint planning", Owner = "Priya" },
            new ActionItem { Text = "recalibrate MR rate benchmark", Owner = "Ted" },
            new ActionItem { Text = "monitor SLO impact", Owner = "Mac" }));

        var ans = await Ask("Did they mention me or give me any tasks in this meeting?", screenId: id);

        Assert.Contains("assigned to you", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing", ans.Text);
        Assert.Contains("Christopher", ans.Text);                       // honest: names the real owners
        Assert.DoesNotContain("• track narrow", ans.Text);              // never dumps the full list as yours
    }

    [Theory]
    [InlineData("did I get any tasks?")]
    [InlineData("any tasks for me?")]
    [InlineData("was I assigned anything in this meeting?")]
    [InlineData("did I get anything?")]
    [InlineData("anything for me?")]
    public async Task SelfAssignment_Phrasings_FilterToYou(string q)
    {
        _store.Save(Meeting("Quality sync", "Quality.",
            new ActionItem { Text = "Send the follow-up survey", Owner = "You" },
            new ActionItem { Text = "Update the dashboard", Owner = "Mac" }));

        var ans = await Ask(q);

        Assert.Contains("follow-up survey", ans.Text);
        Assert.DoesNotContain("dashboard", ans.Text);   // Mac's item is never presented as yours
    }

    // The P9 guard survives: BROAD "what are my open tasks" stays the global list (all owners).
    [Fact]
    public async Task BroadMyOpenTasks_IsStillTheGlobalList()
    {
        _store.Save(Meeting("Sync", "Stuff.",
            new ActionItem { Text = "Send the survey", Owner = "You" },
            new ActionItem { Text = "Update the dashboard", Owner = "Mac" }));

        var ans = await Ask("what are my open tasks");

        Assert.Contains("survey", ans.Text);
        Assert.Contains("dashboard", ans.Text);   // in a meeting you recorded, all open items are your business
    }

    // ── C: oldest/first recency identification is a DB fact — deterministic, never an LLM shrug ──
    [Fact]
    public async Task OldestMeeting_WithTitleAsk_IsDeterministic()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-30), "Rounak's weekly 1:1", "You planned the landing page."));
        _store.Save(MeetingOn(now.AddDays(-10), "Design review", "Screens reviewed."));
        _store.Save(MeetingOn(now.AddDays(-1), "Launch sync", "Launch prep."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("What was the oldest meeting that I recorded? What was the title?");

        Assert.Contains("Rounak's weekly 1:1", ans.Text);
        Assert.Contains("oldest meeting", ans.Text);
        Assert.Null(fake.User);   // answered from the DB, no model involved
    }

    [Fact]
    public async Task FirstMeeting_LeadsWithTitleAndDate_ThenGist()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-21), "Rounak's weekly 1:1", "You planned the landing page pricing section."));
        _store.Save(MeetingOn(now.AddDays(-2), "Launch sync", "Launch prep."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("What was the first meeting I had?");

        Assert.StartsWith("Your first meeting was “Rounak's weekly 1:1”", ans.Text);
        Assert.Contains("landing page", ans.Text);   // the one-line gist follows the identification
        Assert.Null(fake.User);
    }

    [Fact]
    public async Task MostRecentMeetingTitle_IsDeterministicToo()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-9), "Old sync", "Old stuff."));
        _store.Save(MeetingOn(now.AddDays(-1), "Launch Fundraiser and Finalize Thermometer Design", "Fundraiser launch."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("What was the title of the most recent meeting I had?");

        Assert.Contains("Launch Fundraiser and Finalize Thermometer Design", ans.Text);
        Assert.Null(fake.User);
    }

    // ── C: an ambiguous date that hits SEVERAL meetings must list them, never silently pick one ──
    [Fact]
    public async Task AmbiguousDate_WithSeveralMeetings_ListsThemAll()
    {
        var day = DateTimeOffset.Now.AddDays(-6);
        DateTimeOffset At(int hour) => new(day.Year, day.Month, day.Day, hour, 0, 0, day.Offset);
        _store.Save(MeetingOn(At(9), "Prioritize keyboard shortcuts", "Shortcuts."));
        _store.Save(MeetingOn(At(11), "Fix authentication", "Auth."));
        _store.Save(MeetingOn(At(15), "Sprint retro", "Retro."));
        _store.Save(MeetingOn(day.AddDays(-3), "Different day planning", "Elsewhere."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask($"What was the meeting I had on {day:MMMM d}?");

        Assert.Contains("3 meetings", ans.Text);
        Assert.Contains("Prioritize keyboard shortcuts", ans.Text);
        Assert.Contains("Fix authentication", ans.Text);
        Assert.Contains("Sprint retro", ans.Text);
        Assert.Null(fake.User);   // multiplicity is a DB fact — no model, no silent pick
    }

    // ── D: a topic that matches a meeting TITLE must find that meeting (the "Amazon" recall miss) ──
    [Fact]
    public async Task TitleTermQuestion_IdentifiesTheMeeting_Deterministically()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-17), "Amazon's Resilience and Innovation",
            "Amazon has consistently proven its resilience and innovation despite challenges."));
        _store.Save(MeetingOn(now.AddDays(-16), "Prioritize keyboard shortcuts", "Shortcuts and local storage."));
        _store.Save(MeetingOn(now.AddDays(-15), "Fix authentication", "Auth improvements."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("What was the meeting I had regarding Amazon?");

        Assert.Contains("Amazon's Resilience and Innovation", ans.Text);
        Assert.Null(fake.User);   // a title hit is an identification fact — answered from the DB
    }

    [Fact]
    public async Task TitleTermRecall_ReachesTheLlmContext_OnContentQuestions()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-17), "Amazon's Resilience and Innovation", "Resilience despite challenges."));
        for (var i = 0; i < 5; i++)
            _store.Save(MeetingOn(now.AddDays(-i), $"Sync {i}", "We reviewed the sprint and metrics."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what did we discuss regarding amazon?");

        Assert.Contains(ans.Sources, s => s.Title.Contains("Amazon"));   // the title match is never lost
        Assert.NotNull(fake.User);                                        // content question → LLM, WITH the meeting
        Assert.Contains("Amazon's Resilience and Innovation", fake.User);
    }

    // Precision guard (P8 regression): generic/unknown topics still miss honestly — the title
    // backstop must not resurrect the "generic words match everything" bug.
    [Fact]
    public async Task UnknownTopic_StillAnHonestMiss_WithTitleBackstop()
    {
        var now = DateTimeOffset.Now;
        _store.Save(MeetingOn(now.AddDays(-3), "Weekly sync", "Sprint things."));
        var ans = await Ask("What was the meeting I had regarding kangaroos?");
        Assert.Contains("couldn't find", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ans.Sources);
    }

    // ── A: affection must never reach retrieval, even with bait content in the notes ──
    [Fact]
    public async Task ILoveYouToky_IsAffection_NeverRetrieval()
    {
        _store.Save(Meeting("UI and transcription improvements", "We love the hover-driven design and live transcription."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("I love you, Toky.");

        Assert.True(ans.Ok);
        Assert.Empty(ans.Sources);
        Assert.Null(fake.User);                                  // never reached the notes engine
        Assert.DoesNotContain("transcription", ans.Text);        // and never quoted the bait note
    }
}
