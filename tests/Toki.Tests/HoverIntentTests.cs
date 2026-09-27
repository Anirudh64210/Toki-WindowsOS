using Toki.State;
using Xunit;

namespace Toki.Tests;

/// <summary>
/// THE NOTCH OPENS WHEN YOU MEANT IT AND NOT WHEN YOU DIDN'T.
///
/// The peek sits in the top-centre band every cursor crosses on the way to browser tabs. Judging
/// that on "inside the rectangle, for long enough" fails in BOTH directions, and the owner reported
/// both on 2026-09-23: "random hovers it opens up, random hovers it don't."
///
/// The old rule was one line: `_overMs = over ? _overMs + 60 : 0`, with an 850ms target. It had no
/// idea how fast the cursor was moving, so a transit that paused opened the notch; and it zeroed the
/// whole dwell on a SINGLE sample outside a 3px-slack rectangle, so a jitter threw away 800ms of
/// honest intent.
///
/// Every test below is written against the tick rate and thresholds the notch actually ships
/// (HoverPollMs 60, PeekMaxStep 10, OpenDwellMs 360, PeekReEntryGraceMs 300).
///
/// MUTATION PROBES (each fails against the pre-2026-09-23 behaviour):
///   · A_cursor_passing_through_does_not_open_it     - the old code had no speed gate at all
///   · A_single_jittery_sample_does_not_lose_the_dwell - the old code reset to 0 on any miss
///   · A_cursor_plunging_past_does_not_open_it       - direction was never considered
/// </summary>
public sealed class HoverIntentTests
{
    const int Tick = 60, MaxStep = 10, Dwell = 360, Grace = 300;

    static bool Run(HoverIntent h, bool over, int stepPx, int stepY, int ticks)
    {
        var fired = false;
        for (var i = 0; i < ticks; i++)
            if (h.Tick(over, stepPx, stepY, Tick, MaxStep, Dwell, Grace)) fired = true;
        return fired;
    }

    [Fact]
    public void A_settled_cursor_opens_it()
    {
        var h = new HoverIntent();
        // A hand at rest still trembles a pixel or two; that is an arrival.
        Assert.True(Run(h, over: true, stepPx: 2, stepY: 0, ticks: Dwell / Tick));
    }

    [Fact]
    public void It_does_not_open_before_the_dwell_is_earned()
    {
        var h = new HoverIntent();
        Assert.False(Run(h, over: true, stepPx: 1, stepY: 0, ticks: Dwell / Tick - 1));
    }

    [Fact]
    public void A_cursor_passing_through_does_not_open_it()
    {
        // The dominant false-open: crossing the top-centre band on the way to a browser tab. Inside
        // the rectangle the whole time, and moving the whole time. MUTATION PROBE: the old timer
        // opens after 850ms of this regardless of speed.
        var h = new HoverIntent();
        Assert.False(Run(h, over: true, stepPx: 40, stepY: 0, ticks: 200));
    }

    [Fact]
    public void A_cursor_plunging_past_does_not_open_it()
    {
        // Descending THROUGH the peek on the way into the page. stepPx is inside the speed gate, so
        // only the direction rule can reject this - which is why that rule's downward bound is half
        // the speed gate rather than equal to it.
        var h = new HoverIntent();
        Assert.False(Run(h, over: true, stepPx: 9, stepY: 9, ticks: 200));
    }

    [Fact]
    public void Settling_onto_it_with_a_little_downward_drift_still_opens()
    {
        // The direction rule must not punish a normal arrival. Reaching a TOP-edge notch means
        // moving up, then coming to rest - and "rest" includes a pixel or two of downward slack.
        var h = new HoverIntent();
        Assert.True(Run(h, over: true, stepPx: 4, stepY: 4, ticks: Dwell / Tick));
    }

    [Fact]
    public void A_cursor_still_homing_in_keeps_what_it_earned()
    {
        // Inside but moving fast HOLDS the dwell - the user may be closing in on the target. It
        // earns nothing while it flies, but it is not punished either.
        var h = new HoverIntent();
        Run(h, over: true, stepPx: 1, stepY: 0, ticks: 4);     // 240ms banked
        Run(h, over: true, stepPx: 50, stepY: 0, ticks: 10);   // a fast correction inside the rect
        Assert.Equal(240, h.DwellMs);
        Assert.True(Run(h, over: true, stepPx: 1, stepY: 0, ticks: 2));   // settles: 360ms, opens
    }

    [Fact]
    public void A_single_jittery_sample_does_not_lose_the_dwell()
    {
        // THE "sometimes it doesn't open" BUG. The peek is 52x42 with 3px of slack; one sample a
        // pixel outside used to zero everything. MUTATION PROBE: the old code needs the full dwell
        // again after this and returns false.
        var h = new HoverIntent();
        Run(h, over: true, stepPx: 1, stepY: 0, ticks: 5);    // 300ms banked
        Run(h, over: false, stepPx: 1, stepY: 0, ticks: 1);   // one sample outside
        Assert.Equal(300, h.DwellMs);
        Assert.True(Run(h, over: true, stepPx: 1, stepY: 0, ticks: 1));
    }

    [Fact]
    public void Leaving_for_real_does_lose_the_dwell()
    {
        // The grace forgives a jitter, not a departure. Otherwise the notch would open on a hover
        // the user abandoned ten seconds ago.
        var h = new HoverIntent();
        Run(h, over: true, stepPx: 1, stepY: 0, ticks: 5);            // 300ms banked
        Run(h, over: false, stepPx: 1, stepY: 0, ticks: Grace / Tick + 2);
        Assert.Equal(0, h.DwellMs);
        Assert.False(Run(h, over: true, stepPx: 1, stepY: 0, ticks: 5));
    }

    [Fact]
    public void It_fires_once_not_every_tick_while_held()
    {
        // A cursor parked on the peek must not re-fire the transition sixty times a second.
        var h = new HoverIntent();
        var fires = 0;
        for (var i = 0; i < 100; i++)
            if (h.Tick(true, 1, 0, Tick, MaxStep, Dwell, Grace)) fires++;
        Assert.Equal(100 / (Dwell / Tick), fires);
    }

    [Fact]
    public void The_dwell_is_readable_so_the_caller_can_widen_its_hit_box()
    {
        // Enter tight, stay loose: PillWindow switches from 3px to PeekExitSlackPx once DwellMs > 0,
        // so a commitment in progress is harder to lose than it was to start.
        var h = new HoverIntent();
        Assert.Equal(0, h.DwellMs);
        h.Tick(true, 1, 0, Tick, MaxStep, Dwell, Grace);
        Assert.True(h.DwellMs > 0);
    }

    [Fact]
    public void Reset_clears_both_counters()
    {
        var h = new HoverIntent();
        Run(h, over: true, stepPx: 1, stepY: 0, ticks: 3);
        Run(h, over: false, stepPx: 1, stepY: 0, ticks: 1);
        h.Reset();
        Assert.Equal(0, h.DwellMs);
        Assert.Equal(0, h.OutMs);
    }
}
