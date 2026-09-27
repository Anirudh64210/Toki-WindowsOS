using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Toki is set on a FIVE-STEP scale and has a floor.
///
/// It had seventeen distinct sizes, thirty of them at 11px or below and three at 9px - which on the
/// owner's 125% display is eleven physical pixels. Their verdict, 2026-09-27: "the fully small text
/// is the worst obvious tell for AI generated slop". They are right, and the reason is structural: a
/// screen with seventeen sizes has no hierarchy, because a reader cannot rank seventeen things. Five
/// steps can be ranked at a glance, which is what "understand heirarchy of sizes in text and
/// readability" means in practice.
///
///     12    caption   meta lines, timestamps, counts, licence chips  (THE FLOOR)
///     13.5  body      note text, list snippets, field contents
///     15    strong    row labels, button text, section headings
///     17    section   page headings inside the notch
///     20    page      window page titles
///     27    display   the note title, in the serif - one per page
///
/// These are source assertions because the thing under test is what a future edit is allowed to
/// TYPE, not what a particular control measures at runtime. The size of a given label is a design
/// decision; the existence of a scale is an invariant.
/// </summary>
public sealed class TypeScaleTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        return d?.FullName ?? throw new System.InvalidOperationException("repo root not found");
    }

    static IEnumerable<(string File, int Line, double Size, string Text)> EverySize()
    {
        var root = Path.Combine(RepoRoot(), "src", "Toki");
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                         f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var pat = new Regex(@"FontSize\s*=\s*""?([0-9]+(?:\.[0-9]+)?)""?");
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
            {
                // An IconFont size is a GLYPH BOX, not type: it is chosen to fit a disc or a row, and
                // the Fluent chrome glyphs are hinted on a 10px grid. Excluded on purpose, and the
                // window either side is checked because the FontFamily often sits on its own line.
                var from = System.Math.Max(0, i - 2);
                var to = System.Math.Min(lines.Length - 1, i + 2);
                var ctx = string.Join(" ", lines[from..(to + 1)]);
                if (ctx.Contains("IconFont")) continue;

                foreach (Match m in pat.Matches(lines[i]))
                    yield return (Path.GetFileName(f), i + 1, double.Parse(m.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture), lines[i].Trim());
            }
        }
    }

    static readonly double[] Scale = { 12, 13.5, 15, 17, 20, 27 };

    /// <summary>MUTATION PROBE: fails against the code as it stood on 2026-09-27, which carried
    /// 9, 9.5, 10, 10.5, 11 and 11.5 in thirty places.</summary>
    [Fact]
    public void No_text_is_set_below_twelve_pixels()
    {
        var tooSmall = EverySize().Where(s => s.Size < 12).ToList();
        Assert.True(tooSmall.Count == 0,
            "Text below the 12px floor:\n" + string.Join("\n",
                tooSmall.Select(s => $"  {s.File}:{s.Line}  {s.Size}px  {s.Text}")));
    }

    /// <summary>MUTATION PROBE: fails against 2026-09-27, which used seventeen distinct sizes.</summary>
    [Fact]
    public void Every_size_is_on_the_scale()
    {
        var off = EverySize().Where(s => !Scale.Contains(s.Size)).ToList();
        Assert.True(off.Count == 0,
            "Sizes off the five-step scale (12 / 13.5 / 15 / 17 / 20, plus 27 for the serif title):\n"
            + string.Join("\n", off.Select(s => $"  {s.File}:{s.Line}  {s.Size}px  {s.Text}")));
    }

    /// <summary>
    /// A settings page that explains each of its own rows reads as generated copy. The owner ruled on
    /// this for the notch on 2026-08-20 ("a row whose label does not carry it should be renamed, not
    /// annotated") and again for the window on 2026-09-27 ("subtext within headings and buttons on
    /// the window needs to be removed"). This pins the shape that produced it: a muted caption
    /// pulled up under a heading by a negative top margin.
    /// </summary>
    [Fact]
    public void No_caption_is_tucked_under_a_heading()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Toki", "LibraryWindow.xaml"));
        Assert.DoesNotContain("Style=\"{StaticResource Dim}\" FontSize=\"13.5\" Margin=\"0,-8,0,12\"", xaml);
    }
}
