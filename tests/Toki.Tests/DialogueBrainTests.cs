using System.IO;
using Toki.Llm;
using Toki.Storage;
using Toki.Transcription;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// P10 — the dialogue brain. End-to-end tests of the turn-type router around the meeting-QA
/// pipeline: user facts + identity ("call me Anirudh" → "what's my name"), general conversation
/// turns that must NOT hit retrieval, misheard-command resilience, the anti-fabrication relevance
/// floor, and no-citations-on-a-miss. All against a temp-file store (StoreOverride), an in-memory
/// profile (UserProfile.SettingsOverride) and a fake model (LlmOverride) — the real settings.json,
/// toki.db and model are never touched.
/// </summary>
[Collection("NotesQaSeams")]   // shares NotesQa's static seams with NotesQaTests — same collection, never parallel
public sealed class DialogueBrainTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"toki-brain-{Guid.NewGuid():N}.db");
    readonly MeetingStore _store;
    readonly AppSettings _settings = new();   // in-memory; never written to disk

    public DialogueBrainTests()
    {
        _store = new MeetingStore(_dbPath);
        NotesQa.StoreOverride = _store;
        UserProfile.SettingsOverride = _settings;
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
        public string? System, User;
        public int MaxTokens;
        public string Reply = "Sure — here's a thought.";
        public bool ReadyValue = true;
        public bool Ready => ReadyValue;
        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
            => Task.FromResult(Reply);
        public async IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
            IReadOnlyList<string>? stop = null)
        {
            System = system; User = user; MaxTokens = maxTokens;
            yield return Reply;
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

    static Task<NotesQa.Answer> Ask(string q, ConversationContext? ctx = null)
        => NotesQa.AskStreamAsync(q, _ => { }, _ => { }, default, ctx);

    // ═══ USER_FACT: "call me X" is a fact to remember, never a notes search ═══

    [Fact]
    public async Task CallMeAnirudh_IsCapturedAndAcknowledged_NeverRetrieval()
    {
        _store.Save(Meeting("Verify Attendance Mail ID", "We called Mail IDs by name."));   // bait
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        // The exact live utterance, connective lead and all.
        var ans = await Ask("But you call me anirudh");

        Assert.Contains("Anirudh", ans.Text);
        Assert.Empty(ans.Sources);
        Assert.Null(fake.User);                                   // never reached any model/retrieval
        Assert.DoesNotContain("meeting notes", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Anirudh", _settings.UserName);              // persisted through the settings API
    }

    [Fact]
    public async Task CallMe_ThenWhatsMyName_RecallsFromTheProfile()
    {
        var ctx = new ConversationContext();
        await Ask("call me Anirudh", ctx);

        var ans = await Ask("what is my name", ctx);

        Assert.Contains("Anirudh", ans.Text);
        Assert.Empty(ans.Sources);
    }

    // The exact live screenshot: "what is my name" was SEARCHED against the notes and answered
    // "I don't have your name in the meeting notes" with three citations.
    [Fact]
    public async Task WhatIsMyName_IsNeverANotesSearch_EvenWithBaitNotes()
    {
        _store.Save(Meeting("Controlled Document Standards Update", "Named conventions for documents."));
        _store.Save(Meeting("Document Compliance Review", "Naming and IDs reviewed."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what is my name");

        Assert.Null(fake.User);                                   // no model, no retrieval
        Assert.Empty(ans.Sources);                                // no citations, ever
        Assert.DoesNotContain("meeting notes", ans.Text, StringComparison.OrdinalIgnoreCase);
        // Nothing user-set yet → the honest Windows-account fallback with the "call me" hint.
        Assert.Contains("call me", ans.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("call me later")]        // manner/time — not a name
    [InlineData("i'm tired")]            // a state — not a name
    [InlineData("i am done")]
    public async Task NotANameStatement_NeverSetsTheName(string q)
    {
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;
        await Ask(q);
        Assert.Null(_settings.UserName);
    }

    [Fact]
    public async Task IWorkAt_IsRememberedAndShowsUpInAboutMe()
    {
        await Ask("I work at Contoso");
        var ans = await Ask("what do you know about me");
        Assert.Contains("Contoso", ans.Text);
        Assert.Contains(UserProfile.Facts, f => f.Contains("Contoso"));
    }

    // ═══ GENERAL turns: converse from the model's own knowledge — no retrieval, no citations ═══

    [Fact]
    public async Task GeneralKnowledgeQuestion_UsesTheGeneralPath_NotRetrieval()
    {
        _store.Save(Meeting("Weekly sync", "Sprint progress and the hiring plan."));   // unrelated notes exist
        var fake = new FakeLlm { Reply = "Paris." };
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what is the capital of france");

        Assert.Equal("Paris.", ans.Text);
        Assert.Empty(ans.Sources);                                        // no citations on general chat
        Assert.NotNull(fake.User);                                        // the model DID answer
        Assert.Contains("Today is", fake.User);                           // …with the GENERAL prompt (dated preamble)
        Assert.EndsWith("Reply:", fake.User!.TrimEnd());                  // …ending in the label-free "Reply:" cue (not "Reply as Toki:")
        Assert.DoesNotContain("Relevant meeting notes", fake.User);       // …never the grounded prompt
        Assert.DoesNotContain("Weekly sync", fake.User);                  // …and no note content leaked in
    }

    [Fact]
    public async Task GeneralTurn_CarriesTheConversation_SoFollowUpsWork()
    {
        var ctx = new ConversationContext();
        var fake = new FakeLlm { Reply = "Nice to hear!" };
        NotesQa.LlmOverride = fake;

        await Ask("call me Anirudh", ctx);
        await Ask("i'm planning a trip to japan", ctx);

        Assert.NotNull(fake.User);
        Assert.Contains("call me Anirudh", fake.User);                    // the transcript rides along
        Assert.Contains("Anirudh", fake.System);                          // and the profile name is known
    }

    [Fact]
    public async Task MeetingCue_KeepsTheTurnGrounded()
    {
        _store.Save(Meeting("Pricing sync", "We settled on the one-time $20 price."));
        var fake = new FakeLlm { Reply = "You settled on $20 — that was the pricing sync." };
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what did we decide about pricing");

        Assert.NotNull(fake.User);
        Assert.Contains("Pricing sync", fake.User);                       // grounded: notes in context
        Assert.Contains(ans.Sources, s => s.Title == "Pricing sync");
    }

    [Fact]
    public async Task GeneralTurn_WithoutAModel_GetsTheHonestFloor_NeverRetrieval()
    {
        // A model that isn't ready (stands in for "no model installed") → the deterministic floor.
        NotesQa.LlmOverride = new FakeLlm { ReadyValue = false };
        var ans = await Ask("tell me a fun fact about octopuses");
        Assert.Contains("Settings", ans.Text);
        Assert.Empty(ans.Sources);
        Assert.True(ans.Ok);
    }

    // ═══ Turn-router unit behaviour ═══

    [Theory]
    [InlineData("what did we decide about kangaroos")]        // "we decide" → their meeting world
    [InlineData("show me the notes from the standup")]
    [InlineData("who said the deadline moved")]
    [InlineData("was anything assigned to me")]
    [InlineData("what are the action items")]
    public void MeetingShapedTurns_HaveAMeetingCue(string q)
        => Assert.True(TurnRouter.HasMeetingCue(q));

    [Theory]
    [InlineData("what is the capital of france")]
    [InlineData("how do i write a for loop in python")]
    [InlineData("i'm stressed about tomorrow")]
    [InlineData("tell me a joke")]
    public void GeneralShapedTurns_HaveNoMeetingCue(string q)
        => Assert.False(TurnRouter.HasMeetingCue(q));

    // A no-cue turn whose terms DO hit the notes stays grounded (the corpus has evidence).
    [Fact]
    public async Task NoCueButNotesEvidence_StaysGrounded()
    {
        _store.Save(Meeting("Amazon's Resilience and Innovation", "Amazon resilience despite challenges."));
        var fake = new FakeLlm { Reply = "It came up in the Amazon meeting." };
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what's the latest thinking on amazon resilience");

        Assert.NotNull(fake.User);
        Assert.Contains("Amazon", fake.User);                             // grounded on the real note
    }

    // ═══ Anti-fabrication: the relevance floor + no citations on a miss ═══

    // An anchored analytical question whose anchor matches NOTHING must miss honestly — never
    // "analyse 5 random recent meetings" (the fallback that fabricated the theme-command answer).
    [Fact]
    public async Task AnchoredAnalyze_WithNoMatch_IsAnHonestMiss_NotRecentMeetingSoup()
    {
        for (var i = 0; i < 5; i++)
            _store.Save(Meeting($"Sync {i}", "Sprint progress and attendance tracking."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what are the themes across my meetings about zebras");

        Assert.Contains("couldn't find", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fake.User);                                           // the model never saw random notes
        Assert.Empty(ans.Sources);
    }

    // A model answer that declares a miss must ship with NO citations (the "I don't have your name
    // in the meeting notes — From <3 meetings>" screenshot shape).
    [Fact]
    public async Task ModelDeclaredMiss_DropsItsCitations()
    {
        _store.Save(Meeting("Budget planning", "We set the Q3 budget for the launch."));
        var fake = new FakeLlm { Reply = "The notes don't mention a launch date." };
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what did we decide about the launch");     // cue + evidence → grounded

        Assert.NotNull(fake.User);                                        // it did try
        Assert.Empty(ans.Sources);                                        // …but a miss carries no chips
    }

    [Fact]
    public async Task RealAnswer_KeepsItsCitations()
    {
        _store.Save(Meeting("Budget planning", "We set the Q3 budget for the launch."));
        var fake = new FakeLlm { Reply = "You set the Q3 budget — that was Budget planning." };
        NotesQa.LlmOverride = fake;

        var ans = await Ask("what did we decide about the launch");

        Assert.Contains(ans.Sources, s => s.Title == "Budget planning");
    }

    [Theory]
    [InlineData("I couldn't find anything about that in your notes.", true)]
    [InlineData("The notes don't mention a launch date.", true)]
    [InlineData("I don't have your name in the meeting notes.", true)]
    [InlineData("You settled on $20 in the pricing sync. The notes don't cover the annual plan though.", false)]  // real answer + caveat
    public void LooksLikeMiss_ChecksTheLeadOnly(string answer, bool miss)
        => Assert.Equal(miss, NotesQa.LooksLikeMiss(answer));

    // ═══ Misheard commands: degrade to a clarification, never to retrieval ═══

    // The exact live screenshot: ASR heard "switch to like theme"; the old pipeline retrieved and
    // FABRICATED a connection to an unrelated attendance meeting.
    [Fact]
    public async Task SwitchToLikeTheme_GetsAClarification_NeverANotesAnswer()
    {
        _store.Save(Meeting("Audio Troubleshooting Check-in", "Weekend attendance tracking via email."));
        var fake = new FakeLlm();
        NotesQa.LlmOverride = fake;

        var ans = await Ask("switch to like theme");

        Assert.Contains("theme", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("light theme", ans.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fake.User);                                           // no retrieval, no model
        Assert.Empty(ans.Sources);
        Assert.DoesNotContain("attendance", ans.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("switch to like theme")]
    [InlineData("turn on the goat mode theme")]
    [InlineData("switch to ghost")]
    [InlineData("use the other model")]
    [InlineData("start reporting")]              // the owner's real mis-hear for "start recording"
    [InlineData("begin reporting")]
    [InlineData("start recoding")]
    public void CommandShapedNearMisses_GetAClarification(string q)
    {
        Assert.Null(CommandRouter.TryMatch(q));                           // really is a near-miss
        Assert.NotNull(CommandRouter.TryNearMiss(q));
    }

    [Theory]
    [InlineData("start recording")]
    [InlineData("take notes")]
    [InlineData("start taking notes")]
    [InlineData("record this meeting")]
    public void RealRecordingCommands_MatchBeforeNearMiss(string q)
        => Assert.Equal(AppCommand.StartRecording, CommandRouter.TryMatch(q));

    // A greeting with a fuzzy ASR wake-name ("Hey Toky", "hey doki") must be small-talk — NEVER fall
    // through to notes retrieval and return an unrelated meeting (the notch "leak" the owner hit).
    [Theory]
    [InlineData("Hey Toky.")]
    [InlineData("hey doki")]
    [InlineData("hi toki")]
    [InlineData("hello tokyo")]
    [InlineData("thanks toky")]
    [InlineData("toky")]
    public void FuzzyWakeNameGreetings_AreSmallTalk_NotRetrieval(string q)
        => Assert.NotNull(SmallTalk.TryReply(q));

    // Acknowledgements must be small-talk, never notes retrieval (real bug: "okay cool" dug up an
    // unrelated meeting; "no i just meant okay cool thanks" got echoed back with citations).
    [Theory]
    [InlineData("okay cool")]
    [InlineData("ok cool thanks")]
    [InlineData("alright cool")]
    [InlineData("okay")]
    [InlineData("sounds good")]
    [InlineData("makes sense")]
    [InlineData("cool thanks")]
    [InlineData("no i just meant okay cool thanks")]
    public void Acknowledgements_AreSmallTalk_NotRetrieval(string q)
        => Assert.NotNull(SmallTalk.TryReply(q));

    // …but a real question or a meeting reference must still fall through to the Q&A.
    [Theory]
    [InlineData("what did we decide")]
    [InlineData("i meant the pricing meeting")]
    [InlineData("cool project ideas from the sync")]
    public void RealQuestions_AreNotSwallowedByAck(string q)
        => Assert.Null(SmallTalk.TryReply(q));

    // The exact artifact the owner saw ("Toki: Hey Anirudh! … 🚀") — a leading label + emoji — must be
    // stripped: the notch/answer voice is plain text, enforced in code, not just asked in the prompt.
    [Theory]
    [InlineData("Toki: Hey Anirudh! The day's off to a sharp start. 🚀", "Hey Anirudh! The day's off to a sharp start.")]
    [InlineData("🚀 Let's go", "Let's go")]
    [InlineData("Assistant: sure thing", "sure thing")]
    [InlineData("A mutex is a lock. 👍", "A mutex is a lock.")]
    public void GeneralChat_Sanitize_stripsLeadingLabelsAndEmojis(string input, string expected)
        => Assert.Equal(expected, GeneralChat.Sanitize(input));

    [Theory]
    [InlineData("why is the theme white")]                 // question → Q&A, never a clarification
    [InlineData("what did we decide about the model switch")]
    [InlineData("go over the model discussion from yesterday")]   // meeting content, not a command
    [InlineData("summarize my last meeting")]
    public void QuestionsAndContent_NeverGetTheClarification(string q)
        => Assert.Null(CommandRouter.TryNearMiss(q));

    [Fact]
    public void RealCommands_StillMatch_BeforeAnyNearMiss()
    {
        Assert.Equal(AppCommand.ThemeLight, CommandRouter.TryMatch("switch to light theme"));
        Assert.Equal(AppCommand.ThemeDark, CommandRouter.TryMatch("switch to dark theme"));
        Assert.Equal(AppCommand.GhostOn, CommandRouter.TryMatch("ghost mode on"));
    }

    // ═══ Working memory: every answered turn lands in the transcript ═══

    [Fact]
    public async Task Turns_AreRecorded_AcrossPaths()
    {
        _store.Save(Meeting("Pricing sync", "We settled on the $20 price."));
        var ctx = new ConversationContext();
        var fake = new FakeLlm { Reply = "You settled on $20." };
        NotesQa.LlmOverride = fake;

        await Ask("good morning toki", ctx);                              // small talk
        await Ask("call me Anirudh", ctx);                                // user fact
        await Ask("what did we decide about pricing", ctx);               // grounded QA

        Assert.True(ctx.Turns.Count >= 6, $"expected all turns recorded, got {ctx.Turns.Count}");
        Assert.Contains(ctx.Turns, t => t.Role == "user" && t.Text.Contains("pricing"));
        Assert.Contains(ctx.Turns, t => t.Role == "toki" && t.Text.Contains("$20"));
    }
}
