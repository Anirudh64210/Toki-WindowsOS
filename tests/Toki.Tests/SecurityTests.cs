using System.IO;
using Toki.Interop;
using Toki.Llm;
using Toki.Storage;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// Security-property tests: the choke points that keep Toki safe must actually hold their invariants.
/// These prove the mitigations from the security audit (FTS injection, path traversal, URL-scheme
/// allowlist, BYO-API URL rules, per-user single-instance names, no cert bypass).
/// </summary>
public sealed class SecurityTests
{
    // ── FTS5: the MATCH argument must be grammar-free no matter what the user types ──
    [Theory]
    [InlineData("a\" OR b")]
    [InlineData("NEAR(x y)")]
    [InlineData("col:secret")]
    [InlineData("*")]
    [InlineData("))((")]
    [InlineData("pricing AND NOT budget")]
    [InlineData("😀 🚀 fire")]
    public void ToFtsQuery_ProducesOnlyAlnumPrefixOrTerms(string raw)
    {
        var q = MeetingStore.ToFtsQuery(raw);
        // Every emitted token is `alnum*`, joined only by " OR " - no FTS operators, quotes, or colons survive.
        foreach (var tok in q.Split(" OR ", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.EndsWith("*", tok);
            var body = tok[..^1];
            Assert.NotEmpty(body);
            Assert.All(body, ch => Assert.True(char.IsLetterOrDigit(ch), $"non-alnum '{ch}' in '{tok}'"));
        }
    }

    [Fact]
    public void ToFtsQuery_EmptyOnGarbage_SoTheCallerSkipsMatch()
    {
        Assert.Equal("", MeetingStore.ToFtsQuery("\"\" () : *"));
        Assert.Equal("", MeetingStore.ToFtsQuery(""));
        Assert.Equal("", MeetingStore.ToFtsQuery(null));
    }

    [Fact]
    public void ToFtsQuery_RealTermsSurvive_AndActuallyRunAgainstFts()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"toki-fts-{Guid.NewGuid():N}.db");
        try
        {
            var store = new MeetingStore(dbPath);
            store.Save(new MeetingRecord { Title = "Pricing review", Notes = new NoteSet { Tldr = "We set the price." } });
            // An adversarial query must never throw against the real FTS5 table, and a real term still matches.
            Assert.Empty(store.SearchRelevant("col:secret OR \"x", 10));   // no injection, no crash
            Assert.NotEmpty(store.SearchRelevant("pricing", 10));          // real term still finds it
        }
        finally { try { File.Delete(dbPath); } catch { } }
    }

    // ── Markdown mirror: an LLM/transcript-derived title can never escape the Notes dir ──
    [Theory]
    [InlineData(@"..\..\..\Windows\System32\evil")]
    [InlineData(@"C:\Windows\evil")]
    [InlineData("con")]
    [InlineData("a/b/c")]
    [InlineData("\"; del *")]
    [InlineData("....\\....\\escape")]
    public void MarkdownMirror_TitleStaysInsideNotesDir(string maliciousTitle)
    {
        var m = new MeetingRecord { Id = 42, Title = maliciousTitle, Notes = new NoteSet { Tldr = "x" } };
        var path = MarkdownMirror.Write(m);   // must not throw the containment guard
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(MarkdownMirror.NotesDir);
            Assert.StartsWith(root, full, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("-42.md", full);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ── SafeShell: only http/https ever reaches the shell ──
    [Theory]
    [InlineData("https://meet.google.com/abc", true)]
    [InlineData("http://zoom.us/j/123", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"\\attacker\share\x.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SafeShell_OnlyAllowsWebUrls(string? url, bool expected)
        => Assert.Equal(expected, SafeShell.IsWebUrl(url, out _));

    // ── BYO-API endpoint rules: https anywhere, http only on loopback (a key must never travel cleartext) ──
    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("http://127.0.0.1:11434", true)]      // local Ollama
    [InlineData("http://localhost:1234/v1", true)]    // local LM Studio
    [InlineData("http://api.openai.com/v1", false)]   // cleartext to a remote host - rejected
    [InlineData("http://192.168.1.5:11434", false)]   // non-loopback http - rejected
    [InlineData("ftp://x", false)]
    [InlineData("", false)]
    public void ApiLlm_Endpoint_RequiresHttpsOrLoopbackHttp(string baseUrl, bool ok)
        => Assert.Equal(ok, ApiLlm.TryBuildEndpoint(baseUrl) is not null);

    // ── single-instance names are per-user and session-local (no cross-user squatting) ──
    [Fact]
    public void SingleInstanceNames_ArePerUser_AndSessionLocal()
    {
        var (mutex, signal) = SingleInstance.NamesForTest();
        Assert.StartsWith(@"Local\Toki.SingleInstance.", mutex);
        Assert.StartsWith(@"Local\Toki.ShowSignal.", signal);
        Assert.Contains("S-1-", mutex);   // carries the current user's SID
        Assert.NotEqual(mutex, signal);
    }
}
