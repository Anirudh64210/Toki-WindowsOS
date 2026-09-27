using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// A month calendar in Toki's own language: arrows to step months, a weekday header, always six rows,
/// today ringed, the chosen day filled, a Today shortcut. Neighbouring-month days are shown dimmed.
/// </summary>
public static class DayPicker
{
    /// <summary>Open the picker under <paramref name="anchor"/>. <paramref name="onPick"/> fires with
    /// the chosen date; the popup closes itself.</summary>
    public static void Show(FrameworkElement anchor, DateTime selected, Action<DateTime> onPick)
    {
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            // The card is inset inside its own shadow gutter; both offsets give that back.
            VerticalOffset = 6 - Theme.GlassCard.ShadowGutter,
            HorizontalOffset = -8 - Theme.GlassCard.ShadowGutter,
        };

        popup.Child = BuildCalendar(selected, day =>
        {
            popup.IsOpen = false;
            onPick(day);
        });

        // The notch must count an open picker as "still in use" or it retracts out from under it.
        NotchPopups.Register(popup);
        popup.Closed += (_, _) => NotchPopups.Unregister(popup);
        popup.IsOpen = true;
    }

    /// <summary>
    /// The calendar itself, independent of the popup, so the render harness can draw it into a PNG;
    /// a control that only exists inside an open Popup is one no screenshot can see.
    /// </summary>
    internal static FrameworkElement BuildCalendar(DateTime selected, Action<DateTime> pick)
    {
        var month = new DateTime(selected.Year, selected.Month, 1);
        var host = new StackPanel();

        void Render()
        {
            host.Children.Clear();
            host.Children.Add(Header(month, step =>
            {
                month = month.AddMonths(step);
                Render();
            }));
            host.Children.Add(WeekdayRow());
            host.Children.Add(Grid(month, selected, pick));
            host.Children.Add(TodayRow(() => pick(DateTime.Today)));
        }
        Render();

        // Day cells are deliberately NOT re-radiused to the card: they are separate pills with air
        // around them, not a panel seated against the card's inner edge, so no concentric pair applies.
        return new Theme.GlassCard(14)
        {
            CardPadding = new Thickness(12, 10, 12, 8), Content = host,
        };
    }

    // ── the pieces ──

    static FrameworkElement Header(DateTime month, Action<int> step)
    {
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(2, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = month.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        grid.Children.Add(title);

        var prev = Arrow("", "Previous month", () => step(-1));   // ChevronUp
        var next = Arrow("", "Next month", () => step(+1));       // ChevronDown
        System.Windows.Controls.Grid.SetColumn(prev, 1);
        System.Windows.Controls.Grid.SetColumn(next, 2);
        grid.Children.Add(prev);
        grid.Children.Add(next);
        return grid;
    }

    /// <summary>A real Button, never a Border with a mouse handler: inside a StaysOpen=false popup the
    /// mouse-up is swallowed as the popup closes, so only Click fires (see <see cref="NotePicker"/>).</summary>
    static Button Arrow(string glyph, string tip, Action go)
    {
        var text = new TextBlock
        {
            Text = glyph, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");

        var surface = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent, Child = text,
        };
        var b = Bare(surface, tip, go);
        b.MouseEnter += (_, _) => surface.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
        b.MouseLeave += (_, _) => surface.Background = Brushes.Transparent;
        return b;
    }

    static FrameworkElement WeekdayRow()
    {
        var row = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
        // Start the week where this machine's culture starts it.
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        for (var i = 0; i < 7; i++)
        {
            var t = new TextBlock
            {
                Text = names[(first + i) % 7], FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2),
            };
            t.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
            row.Children.Add(t);
        }
        return row;
    }

    static FrameworkElement Grid(DateTime month, DateTime selected, Action<DateTime> pick)
    {
        var grid = new UniformGrid { Columns = 7, Rows = 6 };

        var firstOfWeek = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)month.DayOfWeek - firstOfWeek + 7) % 7;
        var start = month.AddDays(-offset);

        // ALWAYS six rows, or the popup jumps in height while paging and "next month" becomes
        // a moving target under the cursor.
        for (var i = 0; i < 42; i++) grid.Children.Add(Day(start.AddDays(i), month.Month, selected, pick));
        return grid;
    }

    static FrameworkElement Day(DateTime day, int currentMonth, DateTime selected, Action<DateTime> pick)
    {
        var isSelected = day.Date == selected.Date;
        var isToday = day.Date == DateTime.Today;
        var otherMonth = day.Month != currentMonth;

        var text = new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.CurrentCulture), FontSize = 13.5,
            FontWeight = isSelected || isToday ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        // Neighbouring-month days stay readable but recede.
        text.SetResourceReference(TextBlock.ForegroundProperty,
            isSelected ? "Ink" : otherMonth ? "TextDim" : "Text");
        if (otherMonth && !isSelected) text.Opacity = 0.55;

        var surface = new Border
        {
            Width = 32, Height = 30, CornerRadius = new CornerRadius(9), Child = text,
            Margin = new Thickness(1),
            // Today is RINGED, the chosen day is FILLED: two different facts, two different marks.
            BorderThickness = new Thickness(isToday && !isSelected ? 1 : 0),
        };
        // BLUE, NOT PINK. Pink is a reserved CONTENT colour (questions); chrome that says "this one
        // is chosen" is Toki-active blue, which is exactly why the loader shimmer moved off pink in
        // 2026-08. The day picker was the last place it survived, and a magenta calendar reads as a
        // different product. Ink stays the on-accent text in both themes.
        if (isSelected) surface.SetResourceReference(Border.BackgroundProperty, "Blue");
        else surface.Background = Brushes.Transparent;
        if (isToday && !isSelected)
        {
            surface.SetResourceReference(Border.BorderBrushProperty, "Blue");
            Theme.GlassSurface.CrispRim(surface);   // only the ringed cell has a rim to snap
        }

        var b = Bare(surface, day.ToString("dddd d MMMM", CultureInfo.CurrentCulture), () => pick(day));
        if (!isSelected)
        {
            b.MouseEnter += (_, _) => surface.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
            b.MouseLeave += (_, _) => surface.Background = Brushes.Transparent;
        }
        return b;
    }

    static FrameworkElement TodayRow(Action go)
    {
        var text = new TextBlock
        {
            Text = "Today", FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Blue");

        var surface = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 6, 12, 6),
            Background = Brushes.Transparent, Child = text, HorizontalAlignment = HorizontalAlignment.Right,
        };
        var b = Bare(surface, "Jump to today", go);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.Margin = new Thickness(0, 6, 0, 0);
        b.MouseEnter += (_, _) => surface.SetResourceReference(Border.BackgroundProperty, "HoverOverlay");
        b.MouseLeave += (_, _) => surface.Background = Brushes.Transparent;
        return b;
    }

    /// <summary>A button that renders exactly its content and no chrome.</summary>
    static Button Bare(FrameworkElement content, string tip, Action click)
    {
        var b = new Button
        {
            Content = content, Cursor = Cursors.Hand, Focusable = false, ToolTip = tip,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Template = BareTemplate(),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static ControlTemplate BareTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }
}

/// <summary>Render-harness host for the calendar's content (a UserControl, which is what Shot takes).</summary>
public sealed class DayPickerShot : UserControl
{
    public DayPickerShot() => Content = DayPicker.BuildCalendar(DateTime.Today.AddDays(6), _ => { });
}
