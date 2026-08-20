using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Toki.Dev;

/// <summary>
/// --fpstest: measures where the notch's animation frames go, instead of guessing. Runs the same
/// animated mascot in a transparent window, an opaque one, and a resizing one: the transparent vs
/// opaque gap is the layered window's share (its cost scales with AREA), the shortfall against the
/// display's refresh rate is the content's, and the resize run is the window morph's. Run it
/// before optimising anything visual - the two causes need opposite fixes.
/// </summary>
static class FrameTest
{
    const int SampleMs = 4000;

    public static int Run()
    {
        Console.WriteLine("[--fpstest] where the notch's frames go\n");

        // Tier 2 = full hardware acceleration; Tier 0 = software rendering, which invalidates the
        // rest of this report until fixed.
        var tier = RenderCapability.Tier >> 16;
        Console.WriteLine($"  render tier      : {tier}  ({(tier >= 2 ? "hardware accelerated" : tier == 1 ? "partial" : "SOFTWARE - nothing will be smooth")})");
        Console.WriteLine($"  display refresh  : {RefreshHz()} Hz\n");

        var transparent = Measure(layered: true);
        var opaque = Measure(layered: false);
        // The third case is what the notch actually does during a morph: the WINDOW ITSELF changing
        // size and position every frame - its bounds round to whole device pixels, so this can
        // look like stepping even with perfect content throughput.
        var resizing = Measure(layered: true, resize: true);

        Console.WriteLine($"  transparent window (the notch) : {transparent.Fps,6:F1} fps   worst frame {transparent.WorstMs,6:F1} ms");
        Console.WriteLine($"  opaque window (same content)   : {opaque.Fps,6:F1} fps   worst frame {opaque.WorstMs,6:F1} ms");
        Console.WriteLine($"  transparent + RESIZING (morph) : {resizing.Fps,6:F1} fps   worst frame {resizing.WorstMs,6:F1} ms"
                        + $"   frames over budget: {resizing.OverBudget}\n");

        var hz = RefreshHz();
        var windowCost = opaque.Fps > 0 ? (1 - transparent.Fps / opaque.Fps) * 100 : 0;
        var contentCost = hz > 0 ? (1 - opaque.Fps / hz) * 100 : 0;
        var morphCost = transparent.Fps > 0 ? (1 - resizing.Fps / transparent.Fps) * 100 : 0;

        Console.WriteLine($"  cost of TRANSPARENCY : {windowCost,5:F0}%  (the notch vs the same content in a normal window)");
        Console.WriteLine($"  cost of the CONTENT  : {contentCost,5:F0}%  (how far a normal window already falls short of the display)");
        Console.WriteLine($"  cost of the MORPH    : {morphCost,5:F0}%  (animating the WINDOW's own size, as every state change does)\n");

        // The REAL notch, driven through the real state machine. The number is dominated by the
        // per-transition VIEW SWAP, not the morph, and run-to-run noise is large: treat it as a
        // regression alarm only. The synthetic rows above are the trustworthy comparison.
        var live = MeasureLiveNotch();
        var rate = live.Transitions > 0 ? (double)live.OverBudget / live.Transitions : 0;
        Console.WriteLine($"  THE REAL NOTCH, morphing states : worst frame {live.WorstMs,6:F1} ms   "
                        + $"dropped frames per transition: {rate,4:F1}");
        Console.WriteLine( "                                    (mostly the per-transition VIEW REBUILD, not the morph;\n"
                        + "                                     treat as a regression alarm, not a benchmark)\n");

        Console.WriteLine(morphCost > 25 || resizing.OverBudget > 20
            ? "  VERDICT: the WINDOW MORPH dominates. Content and transparency are both fine; the cost\n"
              + "           is resizing and re-positioning the window itself on every frame. The fix is\n"
              + "           architectural: hold the window at a FIXED size and animate the card inside\n"
              + "           it (clip/transform), so the window's bounds never move during a transition."
            : windowCost > 25
                ? "  VERDICT: the layered/transparent window dominates. Animate less AREA - content\n"
                  + "           tuning will not fix this."
                : contentCost > 25
                    ? "  VERDICT: the CONTENT dominates. Cache the static art (BitmapCache) so the animated\n"
                      + "           parts are transforms of a bitmap rather than re-rasterised geometry."
                    : "  VERDICT: all three are within budget on this machine. Any roughness the eye is\n"
                      + "           catching is a design/timing issue, not a throughput one.");
        return 0;
    }

    readonly record struct Sample(double Fps, double WorstMs, int OverBudget);

    readonly record struct LiveSample(double WorstMs, int OverBudget, int Transitions);

    /// <summary>
    /// The actual PillWindow changing state. Reported PER TRANSITION (the transition count per run
    /// varies). Machine noise exceeds real effects here, so a sequential A/B measures the minute,
    /// not the code: to compare two morphs, interleave the conditions inside one run, alternating
    /// every WHOLE cycle (per-transition alternation hands one condition all the shrinks).
    /// </summary>
    static LiveSample MeasureLiveNotch()
    {
        var budgetMs = 1000.0 / Math.Max(30, RefreshHz());
        var sw = Stopwatch.StartNew();
        var last = 0L;
        var settled = 0;
        var over = 0;
        var worst = 0.0;
        var transitions = 0;

        EventHandler onFrame = (_, _) =>
        {
            var now = sw.ElapsedTicks;
            if (last != 0 && settled > 10)
            {
                var ms = (now - last) * 1000.0 / Stopwatch.Frequency;
                if (ms > worst) worst = ms;
                if (ms > budgetMs * 1.6) over++;
            }
            last = now;
            settled++;
        };

        var state = new State.AppState();
        PillWindow? pill = null;
        try
        {
            pill = new PillWindow(state) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            pill.Show();

            // The HOVER PAIRS go through AnimateSize - the path under measurement. Unrelated states
            // take the in-place branch and would report a morph that never ran.
            var cycle = new[]
            {
                State.PillState.RecordingBar, State.PillState.RecordingOpen,   // 34x14 to 272x48
                State.PillState.Collapsed, State.PillState.Home,               // 52x42 to 344 tall
            };

            // Warm up FIRST, unmeasured: the one-off view construction is tens of milliseconds and
            // has nothing to do with how a morph performs.
            foreach (var s in cycle)
            {
                state.ForceForDemo(s);
                Pump(350);
            }

            CompositionTarget.Rendering += onFrame;
            sw.Restart(); last = 0; settled = 0;

            var deadline = Stopwatch.StartNew();
            var i = 0;
            while (deadline.ElapsedMilliseconds < SampleMs * 2)
            {
                state.ForceForDemo(cycle[i++ % cycle.Length]);
                transitions++;
                Pump(400);   // longer than any morph, so each sample is one whole transition
            }
        }
        catch (Exception ex) { Console.WriteLine($"  (live notch sample failed: {ex.Message})"); }
        finally
        {
            CompositionTarget.Rendering -= onFrame;
            try { pill?.Close(); } catch { }
        }

        return new LiveSample(worst, over, transitions);
    }

    static void Pump(int ms)
    {
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < ms)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    static Sample Measure(bool layered, bool resize = false)
    {
        var host = new Window
        {
            Left = -32000, Top = -32000, Width = 420, Height = 120,
            WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
            AllowsTransparency = layered,
            Background = layered ? Brushes.Transparent : Brushes.Black,
            Topmost = false,
        };
        // The real thing: a mascot at the notch's own size, mid-animation, on a card-sized surface.
        var blob = new Controls.TokiBlob { Width = 44, Height = 40 };
        blob.SetMood(Controls.TokiBlob.TokiMood.Writing);   // the busiest mood - pen + breathe + blink
        host.Content = new Border
        {
            Background = layered ? new SolidColorBrush(Color.FromArgb(0x80, 0x21, 0x1E, 0x1E)) : Brushes.Black,
            CornerRadius = new CornerRadius(24),
            Child = blob,
        };

        var frames = 0;
        var worst = 0.0;
        var over = 0;
        var last = 0L;
        var budgetMs = 1000.0 / Math.Max(30, RefreshHz());
        var sw = Stopwatch.StartNew();
        EventHandler onFrame = (_, _) =>
        {
            var now = sw.ElapsedTicks;
            if (last != 0)
            {
                var ms = (now - last) * 1000.0 / Stopwatch.Frequency;
                // Ignore the first few frames: window creation and first rasterisation are one-off
                // costs that would misstate the steady state.
                if (frames > 10)
                {
                    if (ms > worst) worst = ms;
                    // More than 1.6 budgets means at least one dropped frame - the number the eye reacts to.
                    if (ms > budgetMs * 1.6) over++;
                }
            }
            last = now;
            frames++;
        };

        host.Show();
        // Drive the window's own bounds, the way the notch morph does: a size animation, with the
        // window re-centred on every SizeChanged (PillWindow.PlaceTopCenter).
        if (resize)
        {
            var grow = new System.Windows.Media.Animation.DoubleAnimation(280, 420, new Duration(TimeSpan.FromMilliseconds(700)))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = Theme.Motion.Drawer,
            };
            var tall = new System.Windows.Media.Animation.DoubleAnimation(60, 200, new Duration(TimeSpan.FromMilliseconds(700)))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                EasingFunction = Theme.Motion.Drawer,
            };
            host.SizeChanged += (_, _) => host.Left = -32000 + (420 - host.ActualWidth) / 2;
            host.BeginAnimation(FrameworkElement.WidthProperty, grow);
            host.BeginAnimation(FrameworkElement.HeightProperty, tall);
        }
        CompositionTarget.Rendering += onFrame;
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < SampleMs)
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
        CompositionTarget.Rendering -= onFrame;
        var elapsed = sw.Elapsed.TotalSeconds;
        try { host.Close(); } catch { }

        return new Sample(elapsed > 0 ? frames / elapsed : 0, worst, over);
    }

    // The display's actual refresh rate - the ceiling any of these numbers could reach. Worth asking
    // Windows rather than assuming 60: on a 144Hz panel a "60 fps" result is a failure, not a pass.
    static int RefreshHz()
    {
        try
        {
            var mode = new DEVMODE { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency > 1)
                return mode.dmDisplayFrequency;
        }
        catch { /* fall through */ }
        return 60;
    }

    const int EnumCurrentSettings = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct DEVMODE
    {
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
