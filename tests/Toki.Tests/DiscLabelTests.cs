using System;
using System.IO;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// The initial in a speaker disc has to sit in the MIDDLE of its circle. Two attempts with a
/// TextBlock left it visibly low: centring text centres its LINE BOX, and the distance between
/// that box and the cap ink depends on the font, the size, and whether a line height was pinned or
/// inherited. Drawing the letter as geometry makes the layout box the ink itself, so centring the
/// element centres the letter with nothing to keep in sync.
///
/// Asserted against the SOURCE, like the rest of this file: the unit suite has no Application and
/// no theme dictionary, so it cannot build the control. The live version is in `--stickytest`.
/// </summary>
public class DiscLabelTests
{
    // Walk up to the solution, like the rest of the source-reading tests: a relative hop from the
    // working directory breaks the moment the test runner changes where it starts.
    static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    static string PillParts => File.ReadAllText(Path.Combine(Root, "src", "Toki", "Views", "PillParts.cs"));

    // MUTATION PROBE: returning a TextBlock is the bug, whatever nudge it carries.
    [Fact]
    public void TheDiscInitialIsDrawnAsGeometry()
    {
        var src = PillParts;
        var start = src.IndexOf("public static Shape DiscLabel", System.StringComparison.Ordinal);
        Assert.True(start > 0, "DiscLabel no longer returns a Shape - a text label cannot be reliably centred in a disc");

        var body = src[start..(src.IndexOf("\n    }", start, System.StringComparison.Ordinal) + 1)];
        Assert.Contains("BuildGeometry", body);
        Assert.Contains("Stretch.None", body);

        // The layout box must BE the ink. BuildGeometry lays the outline out from the TEXT origin,
        // so the glyph starts a baseline down with empty space above it, and a Path measured from
        // the origin inherits that space - centring the box then leaves the letter low (measured:
        // 1.61px low in a 16px disc). The geometry is translated by its own negative bounds, which
        // is derived, not tuned: a LITERAL offset here is the hand-nudging this replaced.
        Assert.Contains("geometry.Bounds", body);
        Assert.Contains("TranslateTransform(-ink.X, -ink.Y)", body);
        Assert.DoesNotContain("LineHeight", body);
    }

    // STANDING OWNER RULE: no full ovals. The design's shape for a chip or a button is a 13px
    // rounded rectangle (PillButton); a 999 radius is reserved for a genuine circle, like an
    // avatar disc. A name chip drawn as an oval shipped once and was rejected on sight.
    [Fact]
    public void SpeakerChipsAreNotOvals()
    {
        var src = File.ReadAllText(Path.Combine(Root, "src", "Toki", "Views", "SpeakerChips.cs"));
        Assert.DoesNotContain("CornerRadius(999", src);
        Assert.DoesNotContain("CornerRadius(99)", src);
    }
}
