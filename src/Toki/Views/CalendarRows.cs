using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Calendar;
using Toki.Theme;

namespace Toki.Views;

/// <summary>
/// Shared, code-built pieces for the calendar surfaces (Home "COMING UP" glance + the Calendar page),
/// so both read identically. Premium agenda language (owner's reference designs): a coloured accent
/// bar per event, title + "time · place", overlapping attendee initials, an amber relative-time hint
/// when imminent, and an obvious Join. Everything pulls colours/fonts from Theme.xaml by key, so a
/// live theme swap recolours it (rows are rebuilt on ThemeChanged). No provider-specific types here —
/// only <see cref="CalEvent"/>.
/// </summary>
static class CalendarRows
{
    static Brush Br(string key) => (Brush)Application.Current.Resources[key];
    static FontFamily Ui => (FontFamily)Application.Current.Resources["UiFont"];
    static FontFamily Icons => (FontFamily)Application.Current.Resources["IconFont"];

    // ── per-event accent bar + per-person avatar colours ──
    // Deterministic (string.GetHashCode is randomised per process — never use it for a stable colour).
    // Coral is deliberately absent: it means record/destructive only. Amber on events would collide
    // with the imminent treatment, so it's only in the person palette.

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

    // ── attendee avatars: overlapping initial circles (no photos on-device — initials are honest) ──

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

    public static FrameworkElement GlanceRow(CalEvent e, Action onOpen)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = AccentBar(e, new Thickness(1, 4, 0, 4));
        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

        var mid = new StackPanel { Margin = new Thickness(9, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock
        {
            Text = e.Summary, FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("Text"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        mid.Children.Add(SubRow(e, GlanceSubtitle(e)));
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var right = RightAffordance(e);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return Clickable(grid, onOpen);
    }

    // The subtitle line: meta text + the attendee stack beside it (reference: avatars under the title,
    // so the right edge stays reserved for the Join affordance).
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

        // Join is always visible (joining must never require discovery); the copy glyph beside it makes
        // SHARING the Meet link just as discoverable; the amber countdown gives it urgency when imminent.
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

        // Hover reveals an inline action strip (Vercel/Wispr style) below the row. Indent 52 (the time
        // gutter), NOT the title x — the full set of links must fit the page width.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(52, 5, 0, 3) };
        if (canEdit) strip.Children.Add(ActionLink("Edit", false, onEdit));
        // Share: copy the JOINABLE Google Meet link — the only link worth sharing. (The calendar-page
        // link is pointless to send someone; "Open in Google" below already covers viewing the event.)
        if (e.HasMeetLink && e.MeetLink is { Length: > 0 } meetUrl)
            strip.Children.Add(CopyLink(meetUrl));
        strip.Children.Add(ActionLink("Open in Google", false, () => OpenUrl(e.HtmlLink)));
        strip.Children.Add(ActionLink("Delete", true, onDelete));

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
        el.BeginAnimation(FrameworkElement.HeightProperty,
            new System.Windows.Media.Animation.DoubleAnimation(from, to, Motion.Dur(growing ? Motion.Micro : Motion.Exit))
            { EasingFunction = growing ? Motion.EaseOut : Motion.EaseIn });
    }

    // A text-link action (brightens on hover) — the strip's buttons.
    static FrameworkElement ActionLink(string label, bool danger, Action action)
    {
        var restKey = danger ? "Danger" : "TextDim";
        var tb = new TextBlock
        {
            Text = label, FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Br(restKey), Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        tb.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; action(); };
        tb.MouseEnter += (_, _) => tb.Foreground = Br(danger ? "Danger" : "Text");
        tb.MouseLeave += (_, _) => tb.Foreground = Br(restKey);
        return tb;
    }

    // "Copy link" → puts the meeting URL on the clipboard, then flips to "Copied ✓" (green) for a beat so
    // the action is confirmed. Never throws (the clipboard can be momentarily locked by another app).
    static FrameworkElement CopyLink(string url)
    {
        var tb = new TextBlock
        {
            Text = "Copy Meet link", FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Br("TextDim"), Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var copied = false;
        tb.MouseEnter += (_, _) => { if (!copied) tb.Foreground = Br("Text"); };
        tb.MouseLeave += (_, _) => { if (!copied) tb.Foreground = Br("TextDim"); };
        tb.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            var ok = false;
            try { System.Windows.Clipboard.SetText(url); ok = true; } catch { }
            if (!ok) return;
            copied = true;
            tb.Text = "Copied ✓"; tb.Foreground = Br("Green");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (_, _) => { t.Stop(); copied = false; tb.Text = "Copy Meet link"; tb.Foreground = Br("TextDim"); };
            t.Start();
        };
        return tb;
    }

    // The ALWAYS-VISIBLE share affordance: a small copy glyph beside Join on rows that have a Meet
    // link. Click → link on the clipboard, glyph flips to a green ✓ for a beat. Quiet (TextDim at
    // rest) so it doesn't compete with Join; the hover strip's "Copy Meet link" stays for wordy folks.
    static FrameworkElement CopyIcon(string url)
    {
        var tb = new TextBlock
        {
            Text = "",   // Fluent: Copy
            FontFamily = Icons, FontSize = 12, Foreground = Br("TextDim"),
            Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(3),
            ToolTip = "Copy Meet link",
        };
        var copied = false;
        tb.MouseEnter += (_, _) => { if (!copied) tb.Foreground = Br("Text"); };
        tb.MouseLeave += (_, _) => { if (!copied) tb.Foreground = Br("TextDim"); };
        tb.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            var ok = false;
            try { System.Windows.Clipboard.SetText(url); ok = true; } catch { }   // clipboard can be locked
            if (!ok) return;
            copied = true;
            tb.Text = ""; tb.Foreground = Br("Green"); tb.ToolTip = "Copied";   // Fluent: CheckMark
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (_, _) => { t.Stop(); copied = false; tb.Text = ""; tb.Foreground = Br("TextDim"); tb.ToolTip = "Copy Meet link"; };
            t.Start();
        };
        return tb;
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
        // "9:41 – 10:11 AM" — drop the first meridiem when both sides share it.
        var sameMeridiem = (s.Hour < 12) == (en.Hour < 12);
        var startFmt = sameMeridiem ? "h:mm" : "h:mm tt";
        return $"{s.ToString(startFmt, CultureInfo.InvariantCulture)} – {en.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    }

    // Agenda sub-line: duration · place. (People are no longer words here — the avatar stack beside
    // this text carries them; "Meet" as a venue would be redundant next to the Join button.)
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

    /// <summary>Starts within 15 minutes (and hasn't started) — gets the amber urgency treatment.</summary>
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

    // Web links ONLY — calendar data (a conferenceData join link on any invite) is
    // attacker-influenceable, so every open goes through the one shell choke point.
    public static void OpenUrl(string? url) => Toki.Interop.SafeShell.OpenUrl(url);

    // Smooth opacity fade (Motion.Micro, ease-out) — hover reveals appear/vanish gently.
    static void Fade(UIElement el, double to) =>
        el.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(to, Motion.Dur(Motion.Micro))
            { EasingFunction = Motion.EaseOut });
}
