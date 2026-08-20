using Toki.Llm;
using Xunit;

namespace Toki.Tests.Brain;

/// <summary>
/// Wake-name ASR robustness (taxonomy §15): push-to-talk renders "Toki" as Doki/Toky/Docie/Toku/…, and
/// a mis-heard address must never turn a command or a greeting into a notes search. The fuzzy WakeName
/// address is the pipeline's real ASR-tolerance feature - this exercises it across the canned routes.
/// </summary>
[Collection("BrainSeams")]
public sealed class WakeNameGoldenTests : IClassFixture<BrainFixture>
{
    readonly BrainFixture _fx;
    public WakeNameGoldenTests(BrainFixture fx) { _fx = fx; UserProfile.SettingsOverride = new AppSettings(); }
    Route R(string q) => _fx.RouteOf(q);

    // The fuzzy renderings of "Toki" the ASR produces.
    static readonly string[] Names = { "toki", "toky", "doki", "doky", "docie", "toku", "tokey", "tokie" };

    // (address template with {n}, the base utterance, expected route). {n} = a wake-name rendering.
    public static IEnumerable<object[]> Cases()
    {
        var bases = new (string Q, Route R)[]
        {
            ("hey {n}", Route.SmallTalk),
            ("{n} hi", Route.SmallTalk),
            ("thanks {n}", Route.SmallTalk),
            ("good morning {n}", Route.SmallTalk),
            ("{n} open the library", Route.Command),
            ("hey {n} start recording", Route.Command),
            ("{n} go home", Route.Command),
            ("open settings {n}", Route.Command),
            ("{n} are you an ai", Route.Persona),
            ("hey {n} are you always listening", Route.Persona),
            ("{n} what's my name", Route.Identity),
        };
        var seen = new HashSet<string>();
        foreach (var n in Names)
            foreach (var (q, r) in bases)
            {
                var text = q.Replace("{n}", n);
                if (seen.Add(text)) yield return new object[] { text, r };
            }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WakeNameAddress_DoesNotBreakRouting(string q, Route expected)
        => Assert.Equal(expected, R(q));
}
