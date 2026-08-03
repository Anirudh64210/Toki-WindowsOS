using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toki.Calendar;

namespace Toki.Views;

/// <summary>
/// Shared, code-built pieces for the calendar surfaces (Home "COMING UP" glance + the Calendar page),
/// so both read identically. Everything pulls colours/fonts from Theme.xaml by key, so a live theme
/// swap recolours it. No provider-specific types here — only <see cref="CalEvent"/>.
/// </summary>
static class CalendarRows
{
    const string More = "";   // Segoe Fluent: More (ellipsis)

    static Brush Br(string key) => (Brush)Application.Current.Resources[key];
    static FontFamily Ui => (FontFamily)Application.Current.Resources["UiFont"];
    static FontFamily Icons => (FontFamily)Application.Current.Resources["IconFont"];

    // ── date badge (stacked day number + month) ──

    public static FrameworkElement DateBadge(CalEvent e, double width = 34)
    {
        var local = e.StartLocal;
        var today = local.Date == DateTimeOffset.Now.Date;
        var day = new TextBlock
        {
            Text = local.Day.ToString(), FontFamily = Ui, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = today ? Br("Amber") : Br("Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var mon = new TextBlock
        {
            Text = local.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant(),
            FontFamily = Ui, FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Br("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -1, 0, 0),
        };
        return new StackPanel { Width = width, VerticalAlignment = VerticalAlignment.Center, Children = { day, mon } };
    }

    // ── Home "COMING UP" glance row ──

    public static FrameworkElement GlanceRow(CalEvent e, Action onOpen)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = DateBadge(e);
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var mid = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock
        {
            Text = e.Summary, FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("Text"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        mid.Children.Add(new TextBlock
        {
            Text = TimeRange(e), FontFamily = Ui, FontSize = 10, Foreground = Br("TextDim"),
            Margin = new Thickness(0, 1, 0, 0),
        });
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var right = RightAffordance(e);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        return Clickable(grid, onOpen);
    }

    // ── Calendar page agenda row ──

    public static FrameworkElement AgendaRow(CalEvent e, Action<CalEvent, FrameworkElement> onMore)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
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

        var mid = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock
        {
            Text = e.Summary, FontFamily = Ui, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("Text"), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var sub = SubtitleText(e);
        if (sub.Length > 0)
            mid.Children.Add(new TextBlock { Text = sub, FontFamily = Ui, FontSize = 10, Foreground = Br("TextDim"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        // Right: Join (always visible if there's a link) + a hover-revealed "…" menu.
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (e.HasMeetLink) right.Children.Add(JoinButton(e, Imminent(e)));
        var more = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"], Content = More, FontSize = 15,
            Width = 26, Height = 26, Opacity = 0, Margin = new Thickness(2, 0, 0, 0), ToolTip = "More",
        };
        more.Click += (_, ev) => { ev.Handled = true; onMore(e, more); };
        right.Children.Add(more);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var row = Clickable(grid, () => onMore(e, more));   // whole-row click opens the same menu
        row.MouseEnter += (_, _) => more.Opacity = 1;
        row.MouseLeave += (_, _) => more.Opacity = 0;
        return row;
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
            wrap.Children.Add(new TextBlock
            {
                Text = RelativeTime(e), FontFamily = Ui, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Foreground = Br("Amber"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            wrap.Children.Add(JoinButton(e, true));
            return wrap;
        }
        return new TextBlock
        {
            Text = RelativeTime(e), FontFamily = Ui, FontSize = 10.5, Foreground = Br("TextDim"),
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

    static string SubtitleText(CalEvent e)
    {
        var parts = new List<string>();
        if (!e.IsAllDay) parts.Add(DurationText(e));
        var others = e.Attendees.Where(a => !a.IsSelf).ToList();
        if (others.Count == 1) parts.Add(others[0].Label);
        else if (others.Count > 1) parts.Add($"{others.Count + 1} people");
        else if (!string.IsNullOrWhiteSpace(e.Location)) parts.Add(e.Location!);
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

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
