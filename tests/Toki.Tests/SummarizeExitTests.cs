using System;
using System.IO;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE LOADER MUST ALWAYS END.
///
/// AppState allows exactly two ways out of Summarizing (Notes, NoSpeech), and only one line in the
/// app takes either: the dispatcher call at the tail of the background summarize lambda. Anything
/// that skips that line leaves the notch spinning a progress bar forever over notes that were
/// already written, saved and mirrored. The app looks hung while the work is finished.
///
/// SHIPPED 2026-09-04. The thin-input gate ended with a bare `return;` meaning "skip the model", but
/// a `return` inside that lambda means "abandon the pipeline". A two-minute recording produced its
/// note in 8 seconds and then sat in Summarizing for 15 minutes until the owner gave up. The same
/// shape would return from any unguarded throw, so the fix is structural: the advance lives in a
/// `finally`, not at the end of the happy path.
///
/// The second half guards the loader's clock. PillWindow caches one view per state for the life of
/// the process, so a field initialiser measured UPTIME: it read "1275m 37s" on a two-minute meeting
/// because the view had been built the previous evening.
///
/// These read source because both failures are shapes, not values. Each predicate is exercised
/// twice: once against the real file, and once as a MUTATION PROBE against the code as it shipped
/// broken, so the detector is proven to discriminate rather than merely to pass.
/// </summary>
public sealed class SummarizeExitTests
{
    static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Toki.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }

    static string AppSource => File.ReadAllText(Path.Combine(Root, "src", "Toki", "App.xaml.cs"));
    static string LoaderSource => File.ReadAllText(Path.Combine(Root, "src", "Toki", "Views", "SummarizingView.xaml.cs"));

    // ---- predicates ---------------------------------------------------------------------------

    /// <summary>Offset of the dispatcher call that advances the notch out of Summarizing.</summary>
    static int AdvanceOffset(string src)
    {
        var go = src.IndexOf("State.TryGo(PillState.Summarizing);", StringComparison.Ordinal);
        Assert.True(go > 0, "the summarize entry point moved - this file is now guessing");
        var advance = src.IndexOf("Current.Dispatcher.BeginInvoke", go, StringComparison.Ordinal);
        Assert.True(advance > go, "the summarize lambda no longer ends by advancing the notch");
        return advance;
    }

    /// <summary>The lambda body preceding the advance: everything that could skip it.</summary>
    static string BodyBeforeAdvance(string src)
    {
        var run = src.IndexOf("Task.Run(() =>", src.IndexOf("State.TryGo(PillState.Summarizing);", StringComparison.Ordinal), StringComparison.Ordinal);
        return src[run..AdvanceOffset(src)];
    }

    /// <summary>Any valueless `return;`. Deliberately strict: this lambda should contain none, because
    /// distinguishing "returns from a nested lambda" by text scan is exactly the kind of cleverness
    /// that let the original bug through.</summary>
    static bool HasBareReturn(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line == "return;") return true;
        }
        return false;
    }

    /// <summary>Is the advance the first statement of a `finally`? Anything further away means the
    /// nearest finally belongs to something else and the advance is back on the happy path.</summary>
    static bool AdvanceIsInFinally(string src, int advance)
    {
        var fin = src.LastIndexOf("finally", advance, StringComparison.Ordinal);
        if (fin < 0) return false;
        var between = src[fin..advance];
        return between.Length < 200 && !between.Contains("Task.Run", StringComparison.Ordinal);
    }

    /// <summary>Is the elapsed clock stamped when the wait starts, rather than when the view is built?
    /// A readonly field initialiser can only ever be the latter.</summary>
    static bool ClockStampedOnLoad(string src)
    {
        if (src.Contains("readonly DateTime _started", StringComparison.Ordinal)) return false;
        var loaded = src.IndexOf("Loaded += (_, _) =>", StringComparison.Ordinal);
        var unloaded = src.IndexOf("Unloaded += (_, _) =>", StringComparison.Ordinal);
        if (loaded < 0 || unloaded < loaded) return false;
        return src[loaded..unloaded].Contains("_started = DateTime.UtcNow;", StringComparison.Ordinal);
    }

    /// <summary>Is the elapsed timer's handler subscribed outside Loaded? Loaded fires once per
    /// summarize on a cached view, so a += there stacks a handler per meeting.</summary>
    static bool TickSubscribedOnce(string src)
    {
        var loaded = src.IndexOf("Loaded += (_, _) =>", StringComparison.Ordinal);
        var unloaded = src.IndexOf("Unloaded += (_, _) =>", StringComparison.Ordinal);
        if (loaded < 0 || unloaded < loaded) return false;
        return !src[loaded..unloaded].Contains("_elapsedTimer.Tick +=", StringComparison.Ordinal)
            && src.Contains("_elapsedTimer.Tick +=", StringComparison.Ordinal);
    }

    // ---- against the real source ----------------------------------------------------------------

    [Fact]
    public void The_notch_advance_runs_from_a_finally()
    {
        var src = AppSource;
        Assert.True(AdvanceIsInFinally(src, AdvanceOffset(src)),
            "the Summarizing exit is back on the happy path - a throw or an early return now hangs the loader");
    }

    [Fact]
    public void No_early_return_can_skip_the_summarize_exit()
    {
        Assert.False(HasBareReturn(BodyBeforeAdvance(AppSource)),
            "a bare `return;` in the summarize lambda strands the notch in Summarizing forever");
    }

    [Fact]
    public void The_thin_input_gate_skips_the_model_without_leaving_the_pipeline()
    {
        var body = BodyBeforeAdvance(AppSource);
        var thin = body.IndexOf("thin input", StringComparison.Ordinal);
        Assert.True(thin > 0, "the thin-input gate is gone");
        var after = body[thin..];
        Assert.Contains("else", after[..Math.Min(1200, after.Length)]);
    }

    [Fact]
    public void The_elapsed_clock_is_stamped_when_the_wait_starts() =>
        Assert.True(ClockStampedOnLoad(LoaderSource),
            "the loader's clock is anchored to construction again, so it reports process uptime");

    [Fact]
    public void The_elapsed_tick_is_subscribed_once() =>
        Assert.True(TickSubscribedOnce(LoaderSource),
            "the elapsed handler is subscribed inside Loaded, which stacks one per meeting on the cached view");

    // ---- MUTATION PROBES: the same predicates, fed the code as it shipped broken -----------------

    const string PreFixApp = @"
        State.TryGo(PillState.Summarizing);
        Task.Run(() =>
        {
            var folder = """";
            if (substantive < Llm.LlmNoteWriter.ThinInputWords)
            {
                Log(""notes: thin input"");
                Store.ReplaceNotes(meeting.Id, meeting.Notes);
                return;
            }
            InferOwners(meeting);
            Current.Dispatcher.BeginInvoke(() =>
            {
                State.TryGo(target);
            });
        });";

    const string PreFixLoader = @"
    public SummarizingView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            StartShimmer();
            _elapsedTimer.Tick += (_, _) => TickElapsed();
            _elapsedTimer.Start();
        };
        Unloaded += (_, _) => { _elapsedTimer.Stop(); };
    }
    readonly DateTime _started = DateTime.UtcNow;";

    [Fact]
    public void Probe_bare_return_detector_catches_the_shipped_bug() =>
        Assert.True(HasBareReturn(BodyBeforeAdvance(PreFixApp)),
            "the detector would not have caught the return it exists for");

    [Fact]
    public void Probe_finally_detector_catches_the_shipped_bug() =>
        Assert.False(AdvanceIsInFinally(PreFixApp, AdvanceOffset(PreFixApp)),
            "the detector passes code whose advance sits on the happy path, so it proves nothing");

    [Fact]
    public void Probe_clock_detector_catches_the_shipped_bug() =>
        Assert.False(ClockStampedOnLoad(PreFixLoader),
            "the detector passes a readonly construction-time clock, the exact shape that read 1275m");

    [Fact]
    public void Probe_tick_detector_catches_the_shipped_bug() =>
        Assert.False(TickSubscribedOnce(PreFixLoader),
            "the detector passes a Tick subscribed inside Loaded, which stacks one handler per meeting");
}
