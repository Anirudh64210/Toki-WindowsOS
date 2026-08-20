using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Toki.State;
using Toki.Views;

namespace Toki.Dev;

/// <summary>
/// --hittest: proves every button is clickable across its WHOLE box (a hover highlight bigger than
/// the hit area is a lie about where the button is - a bare TextBlock only hit-tests its glyphs),
/// and that every label sits in the OPTICAL middle of its button (cap-height band, not line box;
/// see <see cref="Theme.OpticalCenter"/>). Walks the real visual tree of every notch state and
/// Library page and asks <see cref="VisualTreeHelper.HitTest(Visual, Point)"/> what a click lands on.
/// </summary>
static class HitTargets
{
    /// <summary>
    /// How far inside an EDGE to probe. Never the literal corners: a point near a rounded corner is
    /// genuinely outside the shape and SHOULD miss. Edge midpoints/quarter-points are inside any
    /// rounded rect and are what users actually aim at.
    /// </summary>
    const double Inset = 3;

    /// <summary>Hit targets below this are hard to aim at. The design floors are 32x34 and 34x34, so
    /// anything under 24 in either direction is a real defect rather than a tight-but-fine control.</summary>
    const double MinSide = 24;

    /// <summary>How far a label may sit off the true centre of its button before it reads as crooked.
    /// Sub-pixel is not achievable (glyph rasterisation quantises), 1px is invisible, 1.5px shows.</summary>
    const double MaxOffCentre = 1.5;

    sealed record Finding(string Where, string What, string Problem);

    public static int Run()
    {
        App.Headless = true;
        var findings = new List<Finding>();
        var checkedCount = 0;

        foreach (var (name, view, width) in Surfaces())
        {
            var host = new Window
            {
                Left = -32000, Top = -32000, Width = width + 40, Height = 900,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
                Content = new Border { Width = width, Child = view, VerticalAlignment = VerticalAlignment.Top },
            };
            try
            {
                host.Show();
                host.UpdateLayout();
                // A second pass: several views build their content on Loaded, so the first layout is empty.
                host.UpdateLayout();
                checkedCount += Audit(host, name, findings);
            }
            catch (Exception ex) { findings.Add(new Finding(name, "(building the view)", ex.Message)); }
            finally { try { host.Close(); } catch { } }
        }

        // THE REAL NOTCH WINDOW. The plain hosts above are a model of the app: no reveal transform,
        // no ClipToBounds, no layered-window transparency - exactly the things that can move where a
        // click lands without moving the pixels. Test the thing, not the model of the thing.
        PillWindow? pill = null;
        try
        {
            var state = new AppState();
            pill = new PillWindow(state) { Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
            pill.Show();
            foreach (var s in new[] { PillState.MeetingPrompt, PillState.RecordingOpen, PillState.Home,
                                      PillState.Settings, PillState.Notes, PillState.Calendar, PillState.Ask })
            {
                state.ForceForDemo(s);
                pill.UpdateLayout();
                pill.UpdateLayout();
                checkedCount += Audit(pill, $"REAL notch · {s}", findings);
            }
        }
        catch (Exception ex) { findings.Add(new Finding("REAL notch", "(driving the window)", ex.Message)); }
        finally { try { pill?.Close(); } catch { } }

        // The LIBRARY WINDOW - a separate window with its own buttons (Copy, Delete, the template
        // picker, the nav rail, every Settings row).
        LibraryWindow? lib = null;
        try
        {
            lib = LibraryWindow.PerfHarness();
            foreach (var page in new[] { "library", "ask", "templates", "settings" })
            {
                lib.PerfShowPage(page);
                lib.UpdateLayout();
                lib.UpdateLayout();
                if (Environment.GetEnvironmentVariable("TOKI_HITDEBUG") == "1")
                    Console.WriteLine($"  [debug] page={page}  ask={lib.AskPage.Visibility}/{lib.AskPage.IsVisible} "
                        + $"library={lib.LibraryPage.Visibility}/{lib.LibraryPage.IsVisible}");
                checkedCount += Audit(lib, $"Library window · {page}", findings);
            }
        }
        catch (Exception ex) { findings.Add(new Finding("Library window", "(opening it)", ex.Message)); }
        finally { try { lib?.Close(); } catch { } }

        Console.WriteLine($"[--hittest] checked {checkedCount} clickable controls across every notch state and Library page");
        PrintFontMetrics();
        Console.WriteLine();

        if (findings.Count == 0)
        {
            Console.WriteLine("RESULT: every clickable box is clickable edge to edge, and every label is centred.");
            return 0;
        }

        foreach (var g in findings.GroupBy(f => f.Where))
        {
            Console.WriteLine($"  {g.Key}");
            foreach (var f in g) Console.WriteLine($"      {f.What,-34} {f.Problem}");
        }
        Console.WriteLine();
        Console.WriteLine($"RESULT: {findings.Count} problem(s). A hover highlight that is bigger than the hit area is a lie about where the button is.");
        return 1;
    }

    /// <summary>
    /// --hovershot &lt;dir&gt;: draw every button's REGION over the real view and save it as a PNG, so
    /// "what lights up when I hover" is something to look at instead of describe. (Hover firing
    /// across the whole box and hover "only on the glyphs" can both be true when the box hugs its label.)
    /// </summary>
    public static int Shot(string dir, bool dark = false)
    {
        App.Headless = true;
        System.IO.Directory.CreateDirectory(dir);
        // The theme matters: a hover wash that reads on a white card can be invisible on the
        // pitch-black notch, so judge hover feedback in the theme the user actually sees.
        if (dark) Toki.Theme.ThemeManager.Apply(Toki.Theme.AppTheme.Dark);

        foreach (var (name, view, width) in Surfaces())
        {
            var overlay = new Canvas { IsHitTestVisible = false };
            var stack = new Grid();
            stack.Children.Add(view);
            stack.Children.Add(overlay);

            var host = new Window
            {
                Left = -32000, Top = -32000, Width = width + 40, Height = 900,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false,
                Content = new Border { Width = width, Child = stack, VerticalAlignment = VerticalAlignment.Top },
            };
            try
            {
                host.Show();
                host.UpdateLayout();
                host.UpdateLayout();

                var file = name.Replace(" · ", "-").Replace(" ", "");
                var target = (FrameworkElement)host.Content;

                // (a) THE REAL HOVER LOOK. IsMouseOver cannot be forced headlessly, so paint each
                // button's hover layer by hand with the brush its trigger uses. "Hl" is the dedicated
                // hover layer and rests at Opacity 0 with the right brush already set, so it only
                // needs turning up; styles that tint "Bd" directly need the brush supplied.
                foreach (var el in Clickables(host).OfType<System.Windows.Controls.Primitives.ButtonBase>())
                {
                    if (!IsReachable(el) || el.Template is null) continue;
                    if (el.Template.FindName("Hl", el) is Border hl) hl.Opacity = 1;
                    else if (el.Template.FindName("Bd", el) is Border bd)
                        bd.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
                }
                host.UpdateLayout();
                RenderHarness.SaveVisual(target, System.IO.Path.Combine(dir, $"{file}-HOVER.png"));

                // (b) The regions, outlined, so the hit area can be compared against the look above.
                foreach (var el in Clickables(host))
                {
                    if (!IsReachable(el)) continue;
                    Point at;
                    try { at = el.TranslatePoint(new Point(0, 0), overlay); } catch { continue; }

                    var box = new System.Windows.Shapes.Rectangle
                    {
                        Width = el.ActualWidth, Height = el.ActualHeight,
                        Fill = new SolidColorBrush(Color.FromArgb(50, 255, 0, 128)),
                        Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 0, 128)),
                        StrokeThickness = 1,
                    };
                    Canvas.SetLeft(box, at.X);
                    Canvas.SetTop(box, at.Y);
                    overlay.Children.Add(box);
                }
                host.UpdateLayout();
                RenderHarness.SaveVisual(target, System.IO.Path.Combine(dir, $"{file}-REGIONS.png"));
            }
            catch (Exception ex) { Console.WriteLine($"  {name}: {ex.Message}"); }
            finally { try { host.Close(); } catch { } }
        }

        Console.WriteLine($"[--hovershot] wrote button regions to {dir}");
        Console.WriteLine("Every pink rectangle is exactly what a click hits AND what the hover highlight covers.");
        return 0;
    }

    /// <summary>Examine every clickable thing in one window. Returns how many were checked.</summary>
    static int Audit(Window host, string name, List<Finding> findings)
    {
        // Hit-test against the window's CONTENT root, never the Window itself: the non-client area
        // (border, title bar) offsets the Window's coordinate space and shifts every probe point.
        if (host.Content is not UIElement root) return 0;

        var n = 0;
        foreach (var el in Clickables(host))
        {
            n++;
            findings.AddRange(Check(root, el, name));
        }
        return n;
    }

    /// <summary>
    /// The numbers behind the optical-centring correction, printed so a disagreement between the maths
    /// and somebody's eyes can be settled with data instead of opinion.
    /// </summary>
    static void PrintFontMetrics()
    {
        try
        {
            var family = (FontFamily)Application.Current.FindResource("UiFont");
            var face = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            if (!face.TryGetGlyphTypeface(out var g)) { Console.WriteLine("  (no glyph typeface)"); return; }

            Console.WriteLine($"  font  : {family.Source}");
            Console.WriteLine($"          LineSpacing={family.LineSpacing:F3}em  Baseline={family.Baseline:F3}em  "
                + $"CapsHeight={g.CapsHeight:F3}em  Descent={g.Baseline:F3}em");
            foreach (var size in new[] { 11.0, 12.0, 13.0 })
            {
                var n = Theme.OpticalCenter.OffsetFor(family, size, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                Console.WriteLine($"          at {size,4:F0}px  ->  nudge {n,6:F2}px  ({(n < 0 ? "up" : "down")})");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  (font metrics unavailable: {ex.Message})"); }
    }

    // ── what counts as "the user thinks this is a button" ──

    /// <summary>
    /// Every real button, plus anything wearing the hand cursor. The cursor is the honest test: it is
    /// the promise made to the user, so whatever shows it must behave like a button.
    /// </summary>
    static IEnumerable<FrameworkElement> Clickables(DependencyObject root)
    {
        foreach (var el in Descendants(root).OfType<FrameworkElement>())
        {
            if (!el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0) continue;
            // A button's own template children (its Border, its ContentPresenter) are not separate
            // targets - checking them would just re-check the button.
            if (InsideAButton(el)) continue;
            // Scrollbar furniture is the OS's shape, not ours, and the arrows are deliberately thin.
            if (InsideAScrollBar(el)) continue;
            // A view's own root is the surface the buttons sit ON. The recording bar is a 14px strip by
            // design; judging it as a "button too small to aim at" measures the notch, not a target.
            if (IsViewRoot(el)) continue;
            if (!IsReachable(el)) continue;

            if (el is ButtonBase) yield return el;
            else if (el.Cursor == System.Windows.Input.Cursors.Hand) yield return el;
        }
    }

    static bool InsideAButton(DependencyObject el)
    {
        for (var p = VisualTreeHelper.GetParent(el); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ButtonBase) return true;
        return false;
    }

    static bool InsideAScrollBar(DependencyObject el)
    {
        for (var p = el; p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    /// <summary>The outermost element of the view under test - the surface the buttons sit on, not a
    /// target on it. (The recording bar's root is a 14px strip by design.)</summary>
    static bool IsViewRoot(DependencyObject el)
    {
        // NOT simply "my parent is a UserControl": a UserControl renders its content through its own
        // template, so the visual parent is a ContentPresenter and the check silently never matched.
        for (var p = VisualTreeHelper.GetParent(el); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is UserControl uc) return ReferenceEquals(uc.Content, el);
        return false;
    }

    /// <summary>
    /// Is this element actually rendered where a mouse could reach it? An element inside a collapsed or
    /// clipped container still reports a size and IsVisible=true, but nothing can be clicked there - and
    /// reporting that as "dead" says something about the container, not about the button.
    /// </summary>
    static bool IsReachable(FrameworkElement el)
    {
        var bounds = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
        for (var p = VisualTreeHelper.GetParent(el); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is not FrameworkElement fe) continue;
            if (fe.ActualHeight <= 0 || fe.ActualWidth <= 0) return false;
            // Faded out - including a reveal animation that has not started yet, which is the state the
            // real notch is in when driven headlessly. Nothing is on screen to hover or click.
            if (fe.Opacity <= 0.01) return false;
            if (!fe.ClipToBounds) continue;

            Rect inParent;
            try { inParent = el.TransformToAncestor(fe).TransformBounds(bounds); } catch { continue; }
            inParent.Intersect(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            if (inParent.IsEmpty || inParent.Height < 2 || inParent.Width < 2) return false;
        }
        return true;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    // ── the two checks ──

    static IEnumerable<Finding> Check(UIElement root, FrameworkElement el, string where)
    {
        var label = Describe(el);
        var w = el.ActualWidth;
        var h = el.ActualHeight;

        if (w < MinSide || h < MinSide)
            yield return new Finding(where, label, $"only {w:F0}x{h:F0} - too small to aim at (floor {MinSide:F0})");

        // Is layout SLICING this control? A control bigger than its parent's slot keeps its own
        // reported size but gets a layout clip - a flat cut edge nothing in the numbers reveals.
        if (System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(el) is { } clip)
        {
            var kept = clip.Bounds;
            var cutH = h - kept.Height;
            var cutW = w - kept.Width;
            if (cutH > 0.5 || cutW > 0.5)
                yield return new Finding(where, label,
                    $"layout clips it - {w:F0}x{h:F0} squeezed into {kept.Width:F0}x{kept.Height:F0} (the parent's slot is too small)");
        }

        // Where would a click actually land? Edge midpoints and quarter-points - inside any rounded rect.
        var probes = new (string Name, Point P)[]
        {
            ("centre",       new Point(w / 2, h / 2)),
            ("left edge",    new Point(Inset, h / 2)),
            ("right edge",   new Point(w - Inset, h / 2)),
            ("top edge",     new Point(w / 2, Inset)),
            ("bottom edge",  new Point(w / 2, h - Inset)),
            ("upper-left",   new Point(w * 0.25, h * 0.25)),
            ("upper-right",  new Point(w * 0.75, h * 0.25)),
            ("lower-left",   new Point(w * 0.25, h * 0.75)),
            ("lower-right",  new Point(w * 0.75, h * 0.75)),
        };

        var dead = new List<string>();
        string? stealer = null;
        var offScreen = false;
        foreach (var (pname, p) in probes)
        {
            if (w <= Inset * 2 || h <= Inset * 2) break;
            Point inHost;
            try { inHost = el.TranslatePoint(p, root); } catch { continue; }

            // Scrolled out of view or clipped by a parent: that describes the viewport, not the
            // button. Not a defect - skip the whole control.
            var rootSize = root.RenderSize;
            if (inHost.X < 0 || inHost.Y < 0 || inHost.X > rootSize.Width || inHost.Y > rootSize.Height)
            { offScreen = true; break; }

            var hit = TopmostAt(root, inHost);
            if (hit is null) { dead.Add(pname); stealer ??= "nothing"; continue; }
            if (IsSelfOrDescendant(hit, el)) continue;

            // A click landing on an ANCESTOR means the element is scrolled/clipped out of view -
            // not a defect. (A SIBLING receiving the click is a real overlap and still reports.)
            if (IsSelfOrDescendant(el, hit)) { offScreen = true; break; }

            dead.Add(pname);
            stealer ??= Name(hit);
        }

        if (!offScreen && dead.Count > 0)
            yield return new Finding(where, label,
                dead.Count >= 9
                    // Everything blocked means something sits ON TOP - naming the culprit is the diagnosis.
                    ? $"fully blocked by {stealer}"
                    : $"dead at {string.Join(", ", dead)} (hit {stealer} instead)");

        // Centering, but ONLY for a simple label (a composite row has no single "the text"), and
        // measured against the VISIBLE FILL, not the control bounds - the eye judges the label
        // against the painted rectangle, which differs whenever the fill is inset.
        if (!offScreen && SoleText(el) is { } text && text.Text.Length > 0)
        {
            var fill = VisibleFill(el) ?? el;
            foreach (var f in Centring(fill, text, label, where)) yield return f;
        }
    }

    /// <summary>
    /// The grey area: the element that actually PAINTS the button. Not the control bounds - a button can
    /// be larger than its fill, and the eye judges the label against what it can see.
    /// </summary>
    static FrameworkElement? VisibleFill(FrameworkElement el)
    {
        FrameworkElement? best = null;
        var bestArea = 0.0;
        foreach (var d in Descendants(el).OfType<FrameworkElement>().Prepend(el))
        {
            var bg = d switch
            {
                Border b => b.Background,
                Control c => c.Background,
                Panel p => p.Background,
                _ => null,
            };
            if (bg is not SolidColorBrush { Color.A: > 8 }) continue;   // transparent-ish paints nothing
            var area = d.ActualWidth * d.ActualHeight;
            if (area > bestArea) { bestArea = area; best = d; }
        }
        return best;
    }

    static IEnumerable<Finding> Centring(FrameworkElement fill, TextBlock text, string label, string where)
    {
        var face = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        if (!face.TryGetGlyphTypeface(out var glyphs)) yield break;

        var size = text.FontSize;
        var caps = glyphs.CapsHeight * size;
        var baseline = text.FontFamily.Baseline * size;
        // An icon font often reports no cap height; there is no "middle of the capitals" to speak of, so
        // fall back to the line box rather than inventing a number.
        if (caps <= 0) caps = baseline;

        Rect box;
        try { box = text.TransformToAncestor(fill).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight)); }
        catch { yield break; }

        // Vertical is about the LETTERS. Horizontal is about the whole CONTENT: a button with an icon
        // beside its label ("• Stop") is correctly centred when the icon-plus-label group is centred,
        // and measuring the label alone reports a healthy button as 6.5px off - which it was doing.
        var group = box;
        if (Descendants(fill).OfType<ContentPresenter>().FirstOrDefault() is { ActualWidth: > 0 } cp)
        {
            try { group = cp.TransformToAncestor(fill).TransformBounds(new Rect(0, 0, cp.ActualWidth, cp.ActualHeight)); }
            catch { }
        }

        var dy = (box.Top + baseline - caps / 2) - fill.ActualHeight / 2;
        var dx = (group.Left + group.Width / 2) - fill.ActualWidth / 2;

        if (Math.Abs(dy) > MaxOffCentre)
            yield return new Finding(where, label, $"label sits {Math.Abs(dy):F1}px {(dy > 0 ? "LOW" : "HIGH")} in the fill");
        if (Math.Abs(dx) > MaxOffCentre)
            yield return new Finding(where, label, $"label sits {Math.Abs(dx):F1}px too far {(dx > 0 ? "RIGHT" : "LEFT")} in the fill");
    }

    /// <summary>Name the element that intercepted the click, walking up to the nearest named ancestor -
    /// an anonymous Border is unfindable, but "the thing inside LandingHero" is a lead.</summary>
    static string Name(DependencyObject hit)
    {
        var parts = new List<string>();
        for (var p = hit; p is not null && parts.Count < 6; p = VisualTreeHelper.GetParent(p))
        {
            var kind = p.GetType().Name;
            var nm = p is FrameworkElement { Name.Length: > 0 } fe ? $":{fe.Name}" : "";
            var vis = p is UIElement ue ? (ue.IsVisible ? "" : "!hidden") : "";
            parts.Add($"{kind}{nm}{vis}");
        }
        return string.Join(" < ", parts);
    }

    /// <summary>
    /// What a real click at this point would land on. NOT the simple HitTest overload: that walks
    /// raw geometry and returns collapsed or hit-test-disabled elements; only the filtered overload
    /// answers where a click actually goes.
    /// </summary>
    static DependencyObject? TopmostAt(UIElement root, Point p)
    {
        DependencyObject? found = null;
        VisualTreeHelper.HitTest(root,
            target => target is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result => { found = result.VisualHit; return HitTestResultBehavior.Stop; },
            new PointHitTestParameters(p));
        return found;
    }

    static bool IsSelfOrDescendant(DependencyObject hit, DependencyObject root)
    {
        for (var p = hit; p is not null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, root)) return true;
        return false;
    }

    static TextBlock? FindText(DependencyObject el) =>
        Descendants(el).OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));

    /// <summary>The label, when the control IS a label - exactly one piece of text in it. A row with a
    /// date badge, a title and two chips has no single label and is not a centering question.</summary>
    static TextBlock? SoleText(DependencyObject el)
    {
        var all = Descendants(el).OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>
    /// How far the text's OPTICAL centre (the middle of the cap-height band, which is what the eye reads
    /// as the middle of a word) sits from the middle of its button. Positive = too low.
    /// </summary>
    static double? OffCentre(FrameworkElement box, TextBlock text)
    {
        var face = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        if (!face.TryGetGlyphTypeface(out var glyphs)) return null;

        var size = text.FontSize;
        // Top of the text's line box, in the button's own coordinates.
        double top;
        try { top = text.TranslatePoint(new Point(0, 0), box).Y; } catch { return null; }

        // Within a line box, the baseline sits at Baseline*em from the top; capitals rise CapsHeight*em
        // above it. So the cap band runs from (baseline - caps) to baseline.
        var baseline = text.FontFamily.Baseline * size;
        var caps = glyphs.CapsHeight * size;
        var opticalMiddle = top + baseline - caps / 2;

        return opticalMiddle - box.ActualHeight / 2;
    }

    static string Describe(FrameworkElement el)
    {
        var name = string.IsNullOrEmpty(el.Name) ? "" : $"{el.Name} ";
        var content = el switch
        {
            ContentControl { Content: string s } when s.Length > 0 => Trim(s),
            _ => FindText(el) is { } t ? Trim(t.Text) : "",
        };
        var kind = el.GetType().Name;
        return $"{name}{kind}{(content.Length > 0 ? $" \"{content}\"" : "")}";
    }

    static string Trim(string s)
    {
        s = s.Replace("\n", " ").Trim();
        return s.Length <= 18 ? s : s[..16] + "..";
    }

    // ── every surface that has buttons on it ──

    static IEnumerable<(string Name, FrameworkElement View, double Width)> Surfaces()
    {
        // The notch, state by state. Widths match the design sizes the real overlay uses.
        var states = new (PillState S, double W)[]
        {
            (PillState.MeetingPrompt, 288), (PillState.RecordingBar, 236), (PillState.RecordingOpen, 288),
            (PillState.Summarizing, 232), (PillState.Notes, 360), (PillState.NoSpeech, 300),
            (PillState.Home, 344), (PillState.Library, 404), (PillState.Settings, 380),
            (PillState.Ask, 344), (PillState.Calendar, 360), (PillState.About, 380),
        };

        foreach (var (s, w) in states)
        {
            FrameworkElement? v = null;
            // The calendar's per-row action strip (Edit / Copy link / Open / Delete) only exists while a
            // row is hovered, and hover cannot happen headlessly. Force it open, or the very controls
            // this test was written for are never examined.
            if (s == PillState.Calendar) CalendarRows.DebugExpand = true;
            try { v = BuildState(s); } catch { }
            if (v is not null) yield return ($"notch · {s}", v, w);
            CalendarRows.DebugExpand = false;
        }

        // The calendar's CREATE COMPOSER is a MODE of the Calendar page rather than a PillState, so
        // the loop above cannot reach it - force it open or its buttons go unaudited.
        FrameworkElement? composer = null;
        try
        {
            var c = new CalendarView();
            c.DebugOpenComposer("Design sync tomorrow 2pm with ana@work.com");
            composer = c;
        }
        catch { }
        if (composer is not null) yield return ("notch · Calendar composer", composer, 360);

        // The month picker the Day field opens. A Popup is invisible to this walk, so its day cells -
        // 42 of them, all clickable - are audited through the same standalone host the render harness
        // uses to picture them.
        FrameworkElement? days = null;
        try { days = new Views.DayPickerShot(); } catch { }
        if (days is not null) yield return ("notch · Calendar day picker", days, 280);

        // The prep card, which carries its own action row.
        FrameworkElement? prep = null;
        try { App.CurrentPrep = PrepSamples.Matched(); prep = new PrepCardView(); } catch { }
        if (prep is not null) yield return ("notch · PrepCard", prep, 356);
    }

    static FrameworkElement BuildState(PillState s) => s switch
    {
        PillState.MeetingPrompt => new MeetingPromptView(),
        PillState.RecordingBar => new RecordingBarView(),
        PillState.RecordingOpen => new RecordingOpenView(),
        PillState.Summarizing => new SummarizingView(),
        PillState.Notes => new NotesView(),
        PillState.NoSpeech => new NoSpeechView(),
        PillState.Home => new HomeView(),
        PillState.Library => new LibraryView(),
        PillState.Settings => new SettingsView(),
        PillState.Ask => new AskView(),
        PillState.Calendar => new CalendarView(),
        PillState.About => new AboutView(),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };
}
