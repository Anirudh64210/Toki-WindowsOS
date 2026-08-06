using Toki.Llm;
using Xunit;

namespace Toki.Tests.Brain;

/// <summary>
/// Robustness overlay (taxonomy §15): a curated seed set of (utterance → route) is multiplied by
/// route-INVARIANT mutations - casing, punctuation, and (for the canned/command routes that strip a
/// wake-name address) leading/trailing "Toki" and politeness fluff. Push-to-talk ASR gives no
/// punctuation and mangled casing, so routing MUST survive all of it. This is where the suite passes
/// 1000+ cases while testing a real property.
/// </summary>
[Collection("BrainSeams")]
public sealed class RobustnessPerturbationTests : IClassFixture<BrainFixture>
{
    readonly BrainFixture _fx;
    public RobustnessPerturbationTests(BrainFixture fx) { _fx = fx; UserProfile.SettingsOverride = new AppSettings(); }

    // Seeds validated to route correctly in the base golden sets. Route strippable = canned/command
    // routes that strip a wake-name address + politeness, so those get extra address/fluff mutations.
    static readonly (string Q, Route R, bool Strippable)[] Seeds =
    {
        // small-talk
        ("hi", Route.SmallTalk, true), ("hey", Route.SmallTalk, true), ("good morning", Route.SmallTalk, true),
        ("thanks", Route.SmallTalk, true), ("thank you", Route.SmallTalk, true), ("appreciate it", Route.SmallTalk, true),
        ("how are you", Route.SmallTalk, true), ("bye", Route.SmallTalk, true), ("good night", Route.SmallTalk, true),
        ("i love you", Route.SmallTalk, true), ("you're the best", Route.SmallTalk, true), ("nice", Route.SmallTalk, true),
        ("cool", Route.SmallTalk, true), ("awesome", Route.SmallTalk, true), ("perfect", Route.SmallTalk, true),
        // persona / privacy
        ("are you an ai", Route.Persona, true), ("are you a robot", Route.Persona, true),
        ("why are you so rude", Route.Persona, true), ("are you sending my data to the cloud", Route.Persona, true),
        ("is this private", Route.Persona, true), ("are you always listening", Route.Persona, true),
        ("where does my data go", Route.Persona, true), ("do you store my audio", Route.Persona, true),
        ("is everything local", Route.Persona, true), ("who can see my notes", Route.Persona, true),
        // identity / user-fact
        ("what's my name", Route.Identity, true), ("who am i", Route.Identity, true),
        ("call me Anirudh", Route.UserFact, true), ("my name is Sid", Route.UserFact, true),
        // commands
        ("open the library", Route.Command, true), ("go home", Route.Command, true),
        ("open settings", Route.Command, true), ("show templates", Route.Command, true),
        ("new note", Route.Command, true), ("new chat", Route.Command, true),
        ("start recording", Route.Command, true), ("start taking notes", Route.Command, true),
        ("switch to dark theme", Route.Command, true), ("switch to light theme", Route.Command, true),
        ("turn on ghost mode", Route.Command, true), ("help", Route.Command, true),
        ("what can you do", Route.Command, true), ("take me to the library", Route.Command, true),
        // voice create
        ("schedule a call with Ana tomorrow at 3", Route.VoiceCreate, true),
        ("set up a meeting Friday at 10", Route.VoiceCreate, true),
        // general knowledge
        ("what does OKR mean", Route.General, false), ("how do I write a good PRD", Route.General, false),
        ("what is a SMART goal", Route.General, false), ("what's the capital of France", Route.General, false),
        ("write me a haiku about mondays", Route.General, false), ("give me tips for staying focused", Route.General, false),
        // grounded topic
        ("what did we decide about pricing", Route.Grounded, false),
        ("where did we land on the postgres migration", Route.Grounded, false),
        ("what was discussed about kubernetes", Route.Grounded, false),
        ("what did Ana say about pricing", Route.Grounded, false),
        // tasks
        ("what are my open tasks", Route.ListTasks, false), ("what's on my plate", Route.ListTasks, false),
        ("what's due this week", Route.ListTasks, false), ("what's overdue", Route.ListTasks, false),
        ("anything due tomorrow", Route.ListTasks, false), ("what did I commit to", Route.ListTasks, false),
        ("what's assigned to me", Route.ListTasks, false), ("what are Ana's open tasks", Route.ListTasks, false),
        ("what tasks did I have on August 2nd", Route.ListTasks, false),
        // counting / listing / identify
        ("how many meetings did I have this week", Route.Count, false),
        ("how many calls last month", Route.Count, false),
        ("list all my meetings", Route.ListMeetings, false),
        ("what meetings did I have last week", Route.ListMeetings, false),
        ("what was my first meeting", Route.IdentifyMeeting, false),
        ("what's my most recent meeting", Route.IdentifyMeeting, false),
        // analytical (grounded)
        ("what should I focus on", Route.Grounded, false), ("what are the recurring themes", Route.Grounded, false),
        // more small-talk
        ("yo", Route.SmallTalk, true), ("howdy", Route.SmallTalk, true), ("what's up", Route.SmallTalk, true),
        ("thanks so much", Route.SmallTalk, true), ("ty", Route.SmallTalk, true), ("got it", Route.SmallTalk, true),
        ("see ya", Route.SmallTalk, true), ("goodbye", Route.SmallTalk, true), ("that's all", Route.SmallTalk, true),
        ("you're amazing", Route.SmallTalk, true), ("marry me", Route.SmallTalk, true), ("cheers", Route.SmallTalk, true),
        // more persona / privacy
        ("are you chatgpt", Route.Persona, true), ("are you human", Route.Persona, true),
        ("do you use my data to train", Route.Persona, true), ("is my data safe", Route.Persona, true),
        ("is the mic always on", Route.Persona, true), ("does toki phone home", Route.Persona, true),
        ("do you upload my meetings", Route.Persona, true), ("is this on device", Route.Persona, true),
        // more identity/user-fact
        ("do you know my name", Route.Identity, true), ("i go by Sam", Route.UserFact, true),
        // more commands
        ("pull up the library", Route.Command, true), ("go to settings", Route.Command, true),
        ("record this meeting", Route.Command, true), ("use my api key", Route.Command, true),
        ("switch to the on-device model", Route.Command, true), ("clear the chat", Route.Command, true),
        ("start over", Route.Command, true), ("go dark", Route.Command, true), ("go light", Route.Command, true),
        // more general
        ("what does MRR stand for", Route.General, false), ("define agile", Route.General, false),
        ("how do I stay organized", Route.General, false), ("what's the difference between a PM and a PO", Route.General, false),
        ("best practices for onboarding new hires", Route.General, false),
        // more grounded topic
        ("what was the price we agreed on", Route.Grounded, false),
        ("did anyone mention replication lag", Route.Grounded, false),
        ("what did we say about onboarding", Route.Grounded, false),
        ("who owns the migration", Route.Grounded, false),
        // more tasks
        ("what do I need to do", Route.ListTasks, false), ("my to-dos", Route.ListTasks, false),
        ("what does Ana owe me", Route.ListTasks, false), ("what's due on Friday", Route.ListTasks, false),
        ("what are my tasks for next week", Route.ListTasks, false), ("am I behind on anything", Route.ListTasks, false),
        ("did I get any action items", Route.ListTasks, false), ("action items from last week", Route.ListTasks, false),
        // more counting/listing
        ("number of meetings in July", Route.Count, false), ("how many syncs this week", Route.Count, false),
        ("show me all my meetings", Route.ListMeetings, false),
        ("what was my oldest meeting", Route.IdentifyMeeting, false),
    };

    public static IEnumerable<object[]> Cases()
    {
        var seen = new HashSet<string>();
        foreach (var (q, r, strip) in Seeds)
        {
            var variants = new List<string>
            {
                q.ToUpperInvariant(), q.ToLowerInvariant(),
                q.TrimEnd('?', '.', '!'), q.TrimEnd('?', '.', '!') + "?", Capitalize(q),
            };
            if (strip)
            {
                variants.Add(q + ", toki");
                variants.Add("toki, " + q);
                variants.Add("hey toki, " + q);
            }
            foreach (var v in variants)
                if (seen.Add(v + "|" + r))                       // distinct (mutation, route) only
                    yield return new object[] { v, r };
        }
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Mutation_PreservesRoute(string mutated, Route expected)
        => Assert.Equal(expected, _fx.RouteOf(mutated));
}
