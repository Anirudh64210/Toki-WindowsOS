namespace Toki.State;

/// <summary>
/// DID THE USER MEAN TO HOVER THIS, OR ARE THEY ON THEIR WAY SOMEWHERE ELSE?
///
/// The notch's peek sits in the top-centre band that every cursor crosses on its way to browser
/// tabs and the window controls. Judging that by "is the cursor inside the rectangle, for long
/// enough" fails in both directions at once, and the owner reported both on 2026-09-23: "random
/// hovers it opens up, random hovers it don't."
///
/// THE MODEL IS ARRIVAL, NOT TIME. A cursor that means to be here SLOWS DOWN here; a cursor passing
/// through keeps its speed. That is hoverIntent's insight (published default: under 6px per 100ms,
/// about 60px/s) and it is what the dwell alone can never express - a transit that happens to pause
/// looks exactly like an arrival to a timer.
///
/// Three rules, and only the first is about the clock:
///
///  1. Dwell accumulates ONLY while the cursor is inside AND settled AND not plunging past. A
///     cursor inside but still moving fast HOLDS its dwell rather than losing it - it may be homing
///     in - but earns nothing while it flies.
///  2. Leaving does not reset immediately. A single jittery sample at the boundary used to throw
///     away the whole dwell, which is the entire "sometimes it doesn't open" complaint; a short
///     grace keeps what was honestly earned. (WPF's own tooltips do this: BetweenShowDelay lets a
///     second tooltip appear immediately rather than re-serving the full InitialShowDelay.)
///  3. Once dwell has started, the caller judges "still inside" against a LOOSER rectangle than it
///     used to arrive. Something already open should be harder to dismiss than it was to summon.
///
/// With speed and direction carrying the rejection, the dwell no longer has to, which is why it
/// could come down from 850ms to 360ms. The long dwell was never a feature: it was the only defence
/// in place, and every millisecond of it was another chance to fail a hover the user meant.
///
/// Deliberately has no idea what a notch, a cursor or a rectangle is, so it can be tested.
/// </summary>
internal sealed class HoverIntent
{
    /// <summary>Time the cursor has spent genuinely arriving. Exposed so the caller can widen its
    /// hit rectangle once the user has started to commit (rule 3).</summary>
    public int DwellMs { get; private set; }

    /// <summary>How long the cursor has been outside, against the re-entry grace.</summary>
    public int OutMs { get; private set; }

    public void Reset() { DwellMs = 0; OutMs = 0; }

    /// <summary>Advance one poll. Returns true on the tick the dwell completes, and once only - the
    /// counter clears itself so a held cursor cannot re-fire.</summary>
    /// <param name="over">Inside the target, by whichever rectangle the caller deems current.</param>
    /// <param name="stepPx">Cursor travel since the previous poll, in physical pixels.</param>
    /// <param name="stepY">Its vertical component. Positive is DOWNWARD, away from a top-edge notch.</param>
    /// <param name="tickMs">Poll interval.</param>
    /// <param name="maxStepPx">Travel per poll above which the cursor is in transit, not arriving.</param>
    /// <param name="dwellMs">Settled time required to commit.</param>
    /// <param name="graceMs">How long a departure is forgiven before the dwell is lost.</param>
    public bool Tick(bool over, int stepPx, int stepY, int tickMs, int maxStepPx, int dwellMs, int graceMs)
    {
        if (over)
        {
            OutMs = 0;
            // Settled AND not descending through. The downward bound is deliberately HALF the speed
            // gate: at the same threshold the direction rule would reject nothing the speed gate had
            // not already caught, and a rule that can never fire is worse than no rule. Half means a
            // cursor may settle onto the peek with a little downward drift, but one travelling down
            // through it - on its way into the page - earns nothing. Upward travel is negative here
            // and always passes: reaching UP for a top-edge notch is the arrival.
            if (stepPx <= maxStepPx && stepY * 2 <= maxStepPx) DwellMs += tickMs;
        }
        else
        {
            OutMs += tickMs;
            if (OutMs > graceMs) DwellMs = 0;
        }

        if (DwellMs < dwellMs) return false;
        Reset();
        return true;
    }
}
