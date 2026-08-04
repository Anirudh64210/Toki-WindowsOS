using Toki.Llm;
using Xunit;

namespace Toki.Tests.Brain;

/// <summary>
/// Turn-type routing golden set: every utterance must reach the RIGHT handler (small-talk, persona,
/// identity, command, voice-create, misheard-clarify, general, or grounded meeting-QA) via the real
/// predicates in pipeline order. Covers taxonomy §5, §11, §12, §14.5.
/// </summary>
[Collection("BrainSeams")]
public sealed class TurnTypeGoldenTests : IClassFixture<BrainFixture>
{
    readonly BrainFixture _fx;
    public TurnTypeGoldenTests(BrainFixture fx) { _fx = fx; UserProfile.SettingsOverride = new AppSettings(); }

    Route R(string q) => _fx.RouteOf(q);

    // ── §5.1/5.2/5.7 small-talk: greetings, thanks, affection, goodbyes — never retrieval ──
    [Theory]
    [InlineData("hi")]
    [InlineData("hey")]
    [InlineData("hey toki")]
    [InlineData("hello there")]
    [InlineData("good morning")]
    [InlineData("good afternoon")]
    [InlineData("yo")]
    [InlineData("howdy")]
    [InlineData("sup")]
    [InlineData("what's up")]
    [InlineData("thanks")]
    [InlineData("thank you")]
    [InlineData("thanks so much")]
    [InlineData("ty")]
    [InlineData("appreciate it")]
    [InlineData("cheers")]
    [InlineData("nice")]
    [InlineData("cool")]
    [InlineData("perfect")]
    [InlineData("awesome")]
    [InlineData("got it")]
    [InlineData("how are you")]
    [InlineData("how's it going")]
    [InlineData("you good?")]
    [InlineData("bye")]
    [InlineData("goodbye")]
    [InlineData("see ya")]
    [InlineData("good night")]
    [InlineData("that's all")]
    [InlineData("i love you toki")]
    [InlineData("you're the best")]
    [InlineData("you're amazing")]
    [InlineData("marry me")]
    [InlineData("are you there")]
    [InlineData("can you hear me")]
    public void SmallTalk_Routes(string q) => Assert.Equal(Route.SmallTalk, R(q));

    // ── §5.4 identity: who/what are you — SmallTalk or Persona both give a correct, non-retrieval answer ──
    [Theory]
    [InlineData("who are you")]
    [InlineData("what are you")]
    [InlineData("what's your name")]
    [InlineData("what is toki")]
    [InlineData("introduce yourself")]
    public void Identity_IsHandledNotRetrieved(string q)
        => Assert.True(R(q) is Route.SmallTalk or Route.Persona or Route.Identity, $"'{q}' routed to {R(q)}");

    // ── §5.4 "are you an AI/robot/chatgpt" — Persona (in-character, honest) ──
    [Theory]
    [InlineData("are you an ai")]
    [InlineData("are you chatgpt")]
    [InlineData("are you a robot")]
    [InlineData("are you human")]
    [InlineData("why are you so rude")]
    public void PersonaMeta_Routes(string q) => Assert.Equal(Route.Persona, R(q));

    // ── §5.5 PRIVACY — must be caught and answered accurately, NEVER sent to retrieval ──
    [Theory]
    [InlineData("are you sending my data to the cloud")]
    [InlineData("is this private")]
    [InlineData("is my data safe")]
    [InlineData("where does my data go")]
    [InlineData("where are my notes stored")]
    [InlineData("are you always listening")]
    [InlineData("are you always recording")]
    [InlineData("is the mic always on")]
    [InlineData("do you store my audio")]
    [InlineData("do you upload my meetings")]
    [InlineData("is this on device")]
    [InlineData("is everything local")]
    [InlineData("do you use my data to train")]
    [InlineData("who can see my notes")]
    [InlineData("does toki phone home")]
    public void Privacy_IsAnsweredByPersona_NeverRetrieved(string q)
        => Assert.Equal(Route.Persona, R(q));

    // ── §5.6 user-facts + identity read ──
    [Theory]
    [InlineData("call me Anirudh")]
    [InlineData("my name is Sid")]
    [InlineData("i go by Sam")]
    public void UserFact_Captures(string q) => Assert.Equal(Route.UserFact, R(q));

    [Theory]
    [InlineData("what's my name")]
    [InlineData("who am i")]
    [InlineData("do you know my name")]
    public void Identity_Reads(string q) => Assert.Equal(Route.Identity, R(q));

    // ── §11.1/11.2/11.3 commands (never prose) ──
    [Theory]
    [InlineData("open the library")]
    [InlineData("go home")]
    [InlineData("open settings")]
    [InlineData("show templates")]
    [InlineData("new note")]
    [InlineData("start a new chat")]
    [InlineData("new chat")]
    [InlineData("clear the chat")]
    [InlineData("start recording")]
    [InlineData("start taking notes")]
    [InlineData("record this meeting")]
    [InlineData("switch to dark theme")]
    [InlineData("switch to light theme")]
    [InlineData("turn on ghost mode")]
    [InlineData("use my api key")]
    [InlineData("switch to the on-device model")]
    [InlineData("help")]
    [InlineData("what can you do")]
    [InlineData("take me to the library")]
    public void Commands_Route(string q) => Assert.Equal(Route.Command, R(q));

    // ── §11.4 calendar create → voice-create (confirm-gated downstream) ──
    [Theory]
    [InlineData("schedule a call with Ana tomorrow at 3")]
    [InlineData("set up a meeting Friday at 10")]
    [InlineData("book a meeting with Bob next week at 2")]
    [InlineData("schedule lunch with Sam Monday at noon")]
    public void CalendarCreate_Routes(string q) => Assert.Equal(Route.VoiceCreate, R(q));

    // ── §12 general knowledge / parametric — NO retrieval, NO citations ──
    [Theory]
    [InlineData("what does OKR mean")]
    [InlineData("what does MRR stand for")]
    [InlineData("how do I write a good PRD")]
    [InlineData("what's the difference between a PM and a PO")]
    [InlineData("what is a SMART goal")]
    [InlineData("how do I say no politely in an email")]
    [InlineData("what's the capital of France")]
    [InlineData("write me a haiku about mondays")]
    [InlineData("give me tips for staying focused")]
    [InlineData("best practices for onboarding new hires")]
    public void GeneralKnowledge_Routes(string q) => Assert.Equal(Route.General, R(q));

    // ── grounded meeting-QA (has a real topic present in the corpus) ──
    [Theory]
    [InlineData("what did we decide about pricing")]
    [InlineData("what did we say about the postgres migration")]
    [InlineData("where did we land on onboarding")]
    [InlineData("what was discussed about kubernetes")]
    public void GroundedTopic_Routes(string q) => Assert.Equal(Route.Grounded, R(q));

    // ── the boundary: an absent topic phrased WITHOUT a meeting cue is a general turn (no fabricated
    //    grounding). (With a "we decided about X" cue it stays a grounded meeting-QA that honestly
    //    misses at answer time — asserted in the behavior suite.) ──
    [Theory]
    [InlineData("tell me about the berlin office")]
    [InlineData("tell me about kangaroos")]
    public void AbsentTopic_NoCue_IsGeneral(string q) => Assert.Equal(Route.General, R(q));
}
