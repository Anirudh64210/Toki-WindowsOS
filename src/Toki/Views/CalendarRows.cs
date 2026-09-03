using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Calendar;
using Toki.Theme;

namespace Toki.Views;

/// <summary>
/// Shared, code-built pieces for the calendar surfaces (Home "COMING UP" glance + the Calendar page),
/// so both read identically. Colours/fonts come from Theme.xaml by key (rows are rebuilt on
/// ThemeChanged). No provider-specific types here, only <see cref="CalEvent"/>.
/// </summary>
static class CalendarRows
{
    static Brush Br(string key) => (Brush)Application.Current.Resources[key];
    static FontFamily Ui => (FontFamily)Application.Current.Resources["UiFont"];
    static FontFamily Icons => (FontFamily)Application.Current.Resources["IconFont"];

    // ── per-event accent bar + per-person avatar colours ──
    // Deterministic: string.GetHashCode is randomised per process, never use it for a stable colour.
    // Coral is absent (record/destructive only); amber on events would collide with the imminent
    // treatment, so it's only in the person palette.

    static readonly string[] EventPalette = { "Pink", "Blue", "Green", "Purple" };
    static readonly string[] PersonPalette = { "Pink", "Blue", "Green", "Purple", "Amber" };

    static int StableHash(string s)
    {
        var h = 17;
        foreach (var ch in s) h = (h * 31 + ch) & 0x7FFFFFFF;
        return h;
    }

    static Brush EventAccent(CalEvent e) => Br(EventPalette[StableHash(e.Id) % EventPalette.Length]);

    static Border AccentBar(CalEvent e, Thickness margin) => new()
    {
        Width = 3, CornerRadius = new CornerRadius(1.5), Background = EventAccent(e), Margin = margin,
    };

    // ── attendee avatars: overlapping initial circles (derived, never fetched) ──

    /// <summary>Up to <paramref name="max"/> overlapping initial circles + a "+N" bubble for the rest.</summary>
    public static FrameworkElement AvatarStack(IEnumerable<CalAttendee> people, double size = 16, int max = 3)
    {
        var list = people.ToList();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var shown = list.Take(max).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var p = shown[i];
            var accent = (SolidColorBrush)Br(PersonPalette[StableHash(p.Email ?? p.Label) % PersonPalette.Length]);
            var c = accent.Color;
            var tint = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
            var label = p.Label.TrimStart();
            var initial = label.Length > 0 ? char.ToUpperInvariant(label[0]).ToString() : "?";
            panel.Children.Add(Bubble(size, tint, initial, accent, overlap: i > 0));
        }
        if (list.Count > shown.Count)
            panel.Children.Add(Bubble(size, Br("Chip"), $"+{list.Count - shown.Count}", Br("TextDim"), overlap: shown.Count > 0));
        return panel;
    }

    static Border Bubble(double size, Brush bg, string text, Brush fg, bool overlap) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = bg,
        // The notch-coloured ring is what makes the overlap read as a stack, not a blob.
        BorderBrush = Br("Notch"), BorderThickness = new Thickness(1.5),
        Margin = new Thickness(overlap ? -(size * 0.30) : 0, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text, FontFamily = Ui, FontSize = Math.Max(7.5, Math.Round(size * 0.46)),
            FontWeight = FontWeights.SemiBold, Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -0.5, 0, 0),
        },
    };

    // ── Home "COMING UP" glance row: accent bar · title / avatars + "day · time · place" · Join ──

    /// <summary>
    /// A COMING UP row: a bare date column, then a glass capsule holding the accent line, title,
    /// time, and either the countdown + Join pill or a quiet relative time. The date lives OUTSIDE
    /// the capsule so a column of rows reads as a calendar down its left edge.
    /// </summary>
    public static FrameworkElement GlanceRow(CalEvent e, Action onOpen)
    {
        var row = new Grid { Height = 40, Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The date column.
        var date = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        date.Children.Add(new TextBlock
        {
            Text = e.StartLocal.Day.ToString(CultureInfo.InvariantCulture),
            FontFamily = Ui, FontSize = 16, FontWeight = FontWeights.Black,
            Foreground = Br("TextHi"), HorizontalAlignment = HorizontalAlignment.Center,
        });
        date.Children.Add(new TextBlock
        {
            Text = e.StartLocal.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant(),
            FontFamily = Ui, FontSize = 8, FontWeight = FontWeights.Bold,
            Foreground = Br("TextMid"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        });
        row.Children.Add(date);

        // The capsule.
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var accent = new Border
        {
            Width = 3, CornerRadius = new CornerRadius(1.5),
            Background = AccentBrush(e), Margin = new Thickness(0, 4, 8, 4),
        };
        inner.Children.Add(accent);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock
        {
            Text = e.Summary, FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextHi"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        mid.Children.Add(new TextBlock
        {
            Text = TimeRange(e), FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Medium,
            Foreground = Br("TextLow"), Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(mid, 1);
        inner.Children.Add(mid);

        var right = GlanceRight(e);
        Grid.SetColumn(right, 2);
        inner.Children.Add(right);

        var capsule = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 5, 12, 5),
            Child = inner, Cursor = System.Windows.Input.Cursors.Hand,
        };
        capsule.SetResourceReference(Border.BackgroundProperty, "GlassCapsule");
        Grid.SetColumn(capsule, 2);
        row.Children.Add(capsule);

        return Clickable(row, onOpen);
    }

    /// <summary>The row's right edge: an imminent meeting gets the countdown + Join pill;
    /// a distant one gets a quiet relative time.</summary>
    static FrameworkElement GlanceRight(CalEvent e)
    {
        var until = e.Until;
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        var soon = until.TotalMinutes <= 30 && until.TotalMinutes > -5;
        var label = new TextBlock
        {
            Text = Countdown(e), FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, soon ? "TextHi" : "TextMid");
        stack.Children.Add(label);

        if (soon && e.HasMeetLink)
        {
            var joinText = new TextBlock
            {
                Text = "Join", FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            joinText.SetResourceReference(TextBlock.ForegroundProperty, "JoinPillText");
            var join = new Border
            {
                CornerRadius = new CornerRadius(999), Padding = new Thickness(11, 4, 11, 4),
                Child = joinText, Margin = new Thickness(6, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Open the meeting link",
            };
            join.SetResourceReference(Border.BackgroundProperty, "JoinPill");
            // A real click, not just decoration - and it must not ALSO open the row.
            join.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; Interop.SafeShell.OpenUrl(e.MeetLink); };
            stack.Children.Add(join);
        }
        return stack;
    }

    static string Countdown(CalEvent e)
    {
        var until = e.Until;
        if (until.TotalMinutes <= 0) return "now";
        if (until.TotalMinutes < 60) return $"in {(int)Math.Ceiling(until.TotalMinutes)} min";
        if (until.TotalHours < 24) return $"in {(int)Math.Round(until.TotalHours)} h";
        return $"in {(int)Math.Round(until.TotalDays)} day{((int)Math.Round(until.TotalDays) == 1 ? "" : "s")}";
    }

    /// <summary>The capsule's accent line colour, stable per event. StableHash, never GetHashCode:
    /// an event that changed colour every launch would read as a different meeting.</summary>
    static readonly string[] RedesignAccents = { "AccentAmber", "AccentTeal", "Blue", "Green", "Purple" };
    static Brush AccentBrush(CalEvent e) => Br(RedesignAccents[StableHash(e.Id) % RedesignAccents.Length]);

    // The subtitle line: meta text + the attendee stack beside it.
    static FrameworkElement SubRow(CalEvent e, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = text, FontFamily = Ui, FontSize = 10, Foreground = Br("TextDim"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        });
        var others = e.Attendees.Where(a => !a.IsSelf).ToList();
        if (others.Count > 0)
        {
            var stack = AvatarStack(others);
            stack.Margin = new Thickness(7, 0, 0, 0);
            row.Children.Add(stack);
        }
        return row;
    }

    static string GlanceSubtitle(CalEvent e)
    {
        var parts = new List<string>();
        var day = e.StartLocal.Date; var today = DateTimeOffset.Now.Date;
        if (day != today)
            parts.Add(day == today.AddDays(1) ? "Tomorrow" : e.StartLocal.ToString("ddd d", CultureInfo.InvariantCulture));
        parts.Add(TimeRange(e));
        if (!string.IsNullOrWhiteSpace(e.Location)) parts.Add(e.Location!);
        return string.Join(" · ", parts);
    }

    // ── Calendar page agenda row ──

    /// <summary>Debug: render agenda rows in their hover-EXPANDED state (the harness can't hover).</summary>
    internal static bool DebugExpand;

    public static FrameworkElement AgendaRow(CalEvent e, Action onEdit, Action onDelete, bool canEdit)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var time = new TextBlock
        {
            Text = e.IsAllDay ? "All day" : e.StartLocal.ToString("h:mm", CultureInfo.InvariantCulture),
            FontFamily = Ui, FontSize = e.IsAllDay ? 10 : 11.5,
            Foreground = e.IsAllDay ? Br("TextDim") : Br("TextSoft"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 0);
        grid.Children.Add(time);

        var bar = AccentBar(e, new Thickness(8, 4, 0, 4));
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);

        var mid = new StackPanel { Margin = new Thickness(9, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock
        {
            Text = e.Summary, FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("Text"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var sub = SubtitleText(e);
        var hasOthers = e.Attendees.Any(a => !a.IsSelf);
        if (sub.Length > 0 || hasOthers) mid.Children.Add(SubRow(e, sub));
        Grid.SetColumn(mid, 2);
        grid.Children.Add(mid);

        // Join is always visible - joining must never require discovery.
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (e.HasMeetLink)
        {
            if (e.MeetLink is { Length: > 0 } meet) right.Children.Add(CopyIcon(meet));
            if (Imminent(e)) right.Children.Add(ImminentHint(e));
            right.Children.Add(JoinButton(e, Imminent(e)));
        }
        else if (Imminent(e)) right.Children.Add(ImminentHint(e, trailing: false));
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);

        // Hover reveals an inline action strip below the row. Indent = the time gutter, NOT the
        // title x: the full set of links must fit the page width.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(52, 5, 0, 3) };
        if (canEdit) strip.Children.Add(ActionLink("Edit", false, onEdit));
        // Share copies the JOINABLE Meet link - the only link worth sending someone.
        if (e.HasMeetLink && e.MeetLink is { Length: > 0 } meetUrl)
            strip.Children.Add(CopyLink(meetUrl));
        strip.Children.Add(ActionLink("Open in Google", false, () => OpenUrl(e.HtmlLink)));
        strip.Children.Add(ActionLink("Delete", true, onDelete));
        // Drop the last link's trailing margin, or the row clips its right edge and part of the
        // link looks pressable but is not.
        if (strip.Children[^1] is FrameworkElement last)
            last.Margin = new Thickness(0);

        return HoverRevealRow(grid, strip);
    }

    /// <summary>The amber "in N min" countdown that flanks Join on an imminent row.</summary>
    static TextBlock ImminentHint(CalEvent e, bool trailing = true) => new()
    {
        Text = RelativeTime(e), FontFamily = Ui, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
        Foreground = Br("Amber"), VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, trailing ? 8 : 0, 0),
    };

    // ── the hover-expand "section pill": a row that smoothly grows on hover to reveal an action strip ──

    static FrameworkElement HoverRevealRow(UIElement main, FrameworkElement strip)
    {
        strip.Opacity = DebugExpand ? 1 : 0;
        var stripHost = new Border { Height = DebugExpand ? double.NaN : 0, ClipToBounds = true, Child = strip };
        var col = new StackPanel();
        col.Children.Add(main);
        col.Children.Add(stripHost);
        var wrap = new Border
        {
            CornerRadius = new CornerRadius(10), Background = DebugExpand ? Br("HoverOverlay") : Brushes.Transparent,
            Padding = new Thickness(6, 3, 6, 3), Child = col, Cursor = System.Windows.Input.Cursors.Arrow,
        };

        System.Windows.Threading.DispatcherTimer? leave = null;
        wrap.MouseEnter += (_, _) =>
        {
            leave?.Stop();
            wrap.Background = Br("HoverOverlay");
            AnimateHeight(stripHost, 26);
            Fade(strip, 1);
        };
        wrap.MouseLeave += (_, _) =>
        {
            leave?.Stop();
            leave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };   // hysteresis
            leave.Tick += (_, _) =>
            {
                leave!.Stop();
                wrap.Background = Brushes.Transparent;
                AnimateHeight(stripHost, 0);
                Fade(strip, 0);
            };
            leave.Start();
        };
        return wrap;
    }

    // Motion tokens: expanding decelerates (EaseOut/Micro), collapsing accelerates away (EaseIn/Exit).
    static void AnimateHeight(FrameworkElement el, double to)
    {
        var from = el.ActualHeight;
        var growing = to > from;

        // Height is a LAYOUT property: animating it re-runs Measure and Arrange over this row's whole
        // subtree every frame, on the UI thread. It is kept here and nowhere else because an
        // accordion genuinely has to reflow its neighbours - a transform would slide the row over the
        // ones below instead of moving them. The mitigation is to keep it SHORT (Micro/Exit) and to
        // let reduced motion skip it outright rather than pay per-frame layout for a decoration.
        if (Motion.Reduced)
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Height = to;
            return;
        }

        el.BeginAnimation(FrameworkElement.HeightProperty,
            new System.Windows.Media.Animation.DoubleAnimation(from, to, Motion.Dur(growing ? Motion.Micro : Motion.Exit))
            { EasingFunction = growing ? Motion.EaseOut : Motion.EaseIn });
    }

    // A text-link action (brightens on hover). The label lives inside a HitTarget box:
    // the box is the button, never the glyphs alone.
    static FrameworkElement ActionLink(string label, bool danger, Action action)
    {
        var restKey = danger ? "Danger" : "TextDim";
        var tb = new TextBlock
        {
            Text = label, FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Br(restKey),
        };
        var box = HitTarget.Wrap(tb, corner: 7, padX: 6, padY: 4);
        box.Margin = new Thickness(0, 0, 6, 0);
        box.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; action(); };
        box.MouseEnter += (_, _) => tb.Foreground = Br(danger ? "Danger" : "Text");
        box.MouseLeave += (_, _) => tb.Foreground = Br(restKey);
        return box;
    }

    // "Copy link" puts the meeting URL on the clipboard, then flips to "Copied ✓" for a beat.
    // Never throws: the clipboard can be momentarily locked by another app.
    static FrameworkElement CopyLink(string url)
    {
        var tb = new TextBlock
        {
            Text = "Copy link", FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextDim"),
        };
        var box = HitTarget.Wrap(tb, corner: 7, padX: 6, padY: 4);
        box.Margin = new Thickness(0, 0, 6, 0);
        var copied = false;
        box.MouseEnter += (_, _) => { if (!copied) tb.Foreground = Br("Text"); };
        box.MouseLeave += (_, _) => { if (!copied) tb.Foreground = Br("TextDim"); };
        box.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            var ok = false;
            try { System.Windows.Clipboard.SetText(url); ok = true; } catch { }
            if (!ok) return;
            copied = true;
            tb.Text = "Copied ✓"; tb.Foreground = Br("Green");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (_, _) => { t.Stop(); copied = false; tb.Text = "Copy link"; tb.Foreground = Br("TextDim"); };
            t.Start();
        };
        return box;
    }

    // Always-visible share affordance: a small copy glyph beside Join on rows with a Meet link.
    // Quiet at rest so it doesn't compete with Join.
    static FrameworkElement CopyIcon(string url)
    {
        var tb = new TextBlock
        {
            Text = "",   // Fluent: Copy
            FontFamily = Icons, FontSize = 12, Foreground = Br("TextDim"),
        };
        // A 12px glyph is a very small thing to aim at. The box around it is the button.
        var box = HitTarget.Wrap(tb, corner: 8, padX: 7, padY: 5, tooltip: "Copy Meet link");
        box.MinWidth = HitTarget.MinSide;
        box.Margin = new Thickness(0, 0, 6, 0);
        var copied = false;
        box.MouseEnter += (_, _) => { if (!copied) tb.Foreground = Br("Text"); };
        box.MouseLeave += (_, _) => { if (!copied) tb.Foreground = Br("TextDim"); };
        box.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            var ok = false;
            try { System.Windows.Clipboard.SetText(url); ok = true; } catch { }   // clipboard can be locked
            if (!ok) return;
            copied = true;
            tb.Text = ""; tb.Foreground = Br("Green"); box.ToolTip = "Copied";   // Fluent: CheckMark
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (_, _) => { t.Stop(); copied = false; tb.Text = ""; tb.Foreground = Br("TextDim"); box.ToolTip = "Copy Meet link"; };
            t.Start();
        };
        return box;
    }

    public static FrameworkElement SkeletonRow()
    {
        Border Bar(double w, double h) => new()
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(5), Background = Br("SurfaceHi"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3),
        };
        var mid = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(Bar(150, 11));
        mid.Children.Add(Bar(90, 9));
        var g = new Grid { MinHeight = 44, Opacity = 0.5 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = Bar(30, 30); badge.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(badge);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        return g;
    }

    // ── the right-hand affordance: Join, or a relative-time hint ──

    static FrameworkElement RightAffordance(CalEvent e)
    {
        if (e.HasMeetLink)
        {
            var imminent = Imminent(e);
            if (!imminent) return JoinButton(e, false);
            var wrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            wrap.Children.Add(ImminentHint(e));
            wrap.Children.Add(JoinButton(e, true));
            return wrap;
        }
        return new TextBlock
        {
            Text = RelativeTime(e), FontFamily = Ui, FontSize = 10.5,
            Foreground = Imminent(e) ? Br("Amber") : Br("TextDim"),
            FontWeight = Imminent(e) ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    static Button JoinButton(CalEvent e, bool imminent)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources[imminent ? "StarButton" : "WhiteButton"],
            Content = "Join", Padding = new Thickness(12, 5, 12, 5), FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, ev) => { ev.Handled = true; OpenUrl(e.MeetLink); App.State.ForceForDemo(State.PillState.Hidden); };
        return b;
    }

    // ── formatting ──

    public static string TimeRange(CalEvent e)
    {
        if (e.IsAllDay) return "All day";
        var s = e.StartLocal; var en = e.EndLocal;
        // "9:41 – 10:11 AM" - drop the first meridiem when both sides share it.
        var sameMeridiem = (s.Hour < 12) == (en.Hour < 12);
        var startFmt = sameMeridiem ? "h:mm" : "h:mm tt";
        return $"{s.ToString(startFmt, CultureInfo.InvariantCulture)} – {en.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    }

    // Agenda sub-line: duration · place. People are carried by the avatar stack, not words.
    static string SubtitleText(CalEvent e)
    {
        var parts = new List<string>();
        if (!e.IsAllDay) parts.Add(DurationText(e));
        if (!string.IsNullOrWhiteSpace(e.Location)) parts.Add(e.Location!);
        return string.Join("  ·  ", parts);
    }

    public static string DurationText(CalEvent e)
    {
        var m = (int)Math.Round((e.EndUtc - e.StartUtc).TotalMinutes);
        if (m <= 0) return "";
        if (m < 60) return $"{m} min";
        var h = m / 60; var rem = m % 60;
        return rem == 0 ? $"{h} h" : $"{h} h {rem} m";
    }

    public static string RelativeTime(CalEvent e)
    {
        var until = e.Until;
        if (until <= TimeSpan.Zero) return e.EndUtc > DateTimeOffset.UtcNow ? "now" : "";
        if (until.TotalMinutes < 60) return $"in {Math.Max(1, (int)until.TotalMinutes)} min";
        if (until.TotalHours < 24) return $"in {(int)until.TotalHours} h";
        return $"in {(int)until.TotalDays} d";
    }

    /// <summary>Starts within 15 minutes (and hasn't started) - gets the amber urgency treatment.</summary>
    public static bool Imminent(CalEvent e) => e.Until > TimeSpan.Zero && e.Until <= TimeSpan.FromMinutes(15);

    // ── shared helpers ──

    /// <summary>Wrap content in a rounded, hover-highlighted, clickable Border (dividers-not-boxes: no
    /// resting border, just a hover wash).</summary>
    static Border Clickable(UIElement content, Action onClick)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Brushes.Transparent,
            Padding = new Thickness(6, 3, 6, 3), Cursor = System.Windows.Input.Cursors.Hand, Child = content,
        };
        b.MouseEnter += (_, _) => b.Background = Br("HoverOverlay");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    // Web links ONLY: calendar data (a conferenceData join link on any invite) is
    // attacker-influenceable, so every open goes through the one shell choke point.
    public static void OpenUrl(string? url) => Toki.Interop.SafeShell.OpenUrl(url);

    // Smooth opacity fade for hover reveals.
    static void Fade(UIElement el, double to) =>
        el.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(to, Motion.Dur(Motion.Micro))
            { EasingFunction = Motion.EaseOut });
}
