using System;
using System.IO;
using System.Linq;
using Toki.Llm;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// No em dashes anywhere a person reads (owner, 2026-08-06: they look ugly in Toki's surfaces).
///
/// Two halves, because there are two ways one arrives: somebody types it into the source, or the model
/// generates it. This file guards the first; <see cref="GeneralChat.PlainDashes"/> handles the second and
/// is tested here too.
/// </summary>
public sealed class NoEmDashTests
{
    const char Em = '—';
    const char HorizontalBar = '―';

    /// <summary>The only places an em dash is FUNCTIONAL rather than decorative: a word separator for the
    /// evidence tokenizer, a value the model emits meaning "this field is empty", and the character
    /// PlainDashes exists to remove. All three are listed by name rather than silently exempted.</summary>
    static readonly string[] AllowedFiles = { "Evidence.cs", "LlmNoteWriter.cs", "GeneralChat.cs" };

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void No_em_dash_survives_in_any_shipped_source_file()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = Directory
            .EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml"))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !AllowedFiles.Contains(Path.GetFileName(f)))
            .Where(f => File.ReadAllText(f).IndexOf(Em) >= 0)
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "em dash found in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_two_functional_uses_are_still_exactly_two()
    {
        // If one of these ever stops being needed, delete it AND its entry above rather than leaving a
        // blanket exemption behind for a whole file.
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var name in AllowedFiles)
        {
            var file = Directory.EnumerateFiles(src, name, SearchOption.AllDirectories).Single();
            var count = File.ReadAllText(file).Count(c => c == Em);
            Assert.True(count == 1, $"{name} should contain exactly one functional em dash, found {count}");
        }
    }

    // ── the model's output ──

    [Theory]
    [InlineData("we agreed — finally — on the price", "we agreed - finally - on the price")]
    [InlineData("nothing here", "nothing here")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void PlainDashes_replaces_em_dashes_and_leaves_everything_else(string? input, string expected)
    {
        Assert.Equal(expected, GeneralChat.PlainDashes(input));
    }

    [Fact]
    public void The_horizontal_bar_some_models_emit_is_caught_too()
    {
        Assert.Equal("a - b", GeneralChat.PlainDashes($"a {HorizontalBar} b"));
    }

    [Fact]
    public void An_en_dash_is_left_alone_because_a_time_range_needs_one()
    {
        // "9:41 – 10:11 AM" is Toki's own formatting, not the model's, and it is correct.
        Assert.Equal("9:41 – 10:11", GeneralChat.PlainDashes("9:41 – 10:11"));
    }

    [Fact]
    public void A_reply_from_the_model_comes_back_without_em_dashes()
    {
        Assert.DoesNotContain(Em, GeneralChat.Sanitize($"Toki: sure {Em} here's the thing"));
    }
}
