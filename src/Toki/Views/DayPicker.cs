using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Toki.Views;

/// <summary>
/// A month calendar, in Toki's own language - the control every calendar app opens when you click a date.
///
/// WHY IT REPLACED A LIST OF DAYS. The first version offered the next fourteen days as text rows. That
/// is fine for "tomorrow" and useless for everything else: a meeting three weeks out was simply
/// unreachable, and the list gave no sense of WHICH DAY of the week you were picking, which is most of
/// what people are actually deciding. Outlook and Google both open a month grid because a month grid
/// answers "is that a Friday", "is that the week after next" and "what date is the 3rd Tuesday" at a
/// glance, and a list answers none of them.
///
/// Shape follows the reference the owner supplied: month name and year, arrows to step months, a
/// weekday header, six rows so the grid never changes height as you page, today ringed, the chosen day
/// filled, and a Today shortcut. Days from the neighbouring months are shown and dimmed rather than
/// left blank, so the grid reads as a continuous calendar.
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
            VerticalOffset = 6, HorizontalOffset = -8,
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
    /// The calendar itself, independent of the popup - which is what lets the render harness draw it
    /// into a PNG. A control that only exists inside an open Popup is a control no screenshot can see,
    /// and unseeable UI is how the 14-day list shipped in the first place.
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

        var box = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 8),
            BorderThickness = new Thickness(1), Child = host,
        };
        box.SetResourceReference(Border.BackgroundProperty, "SurfaceHi");
        box.SetResourceReference(Border.BorderBrushProperty, "Hairline");
        return box;
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

    /// <summary>A real Button, never a Border with a mouse handler - inside a StaysOpen=false popup the
    /// mouse-UP is swallowed as the popup closes, so a Border's handler silently never fires. Click
    /// does. The same trap is documented in <see cref="NotePicker"/>, and it has been paid for twice.</summary>
    static Button Arrow(string glyph, string tip, Action go)
    {
        var text = new TextBlock
        {
            Text = glyph, FontSize = 10,
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
        // Start the week where this machine's culture starts it - a Monday-first user reading a
        // Sunday-first grid misreads every date on it.
        var first = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        for (var i = 0; i < 7; i++)
        {
            var t = new TextBlock
            {
                Text = names[(first + i) % 7], FontSize = 10.5,
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

        // ALWAYS six rows. A month that needs five would otherwise make the popup jump in height as
        // you page through the year, which turns "next month" into a moving target under the cursor.
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
            Text = day.Day.ToString(CultureInfo.CurrentCulture), FontSize = 12,
            FontWeight = isSelected || isToday ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        // A day from the month either side stays readable but recedes - blank cells would break the
        // sense that this is one continuous calendar rather than a grid of one month.
        text.SetResourceReference(TextBlock.ForegroundProperty,
            isSelected ? "Ink" : otherMonth ? "TextDim" : "Text");
        if (otherMonth && !isSelected) text.Opacity = 0.55;

        var surface = new Border
        {
            Width = 32, Height = 30, CornerRadius = new CornerRadius(9), Child = text,
            Margin = new Thickness(1),
            // Today is RINGED, the chosen day is FILLED. Two different facts, two different marks -
            // if both were fills you could not tell which day you had actually picked.
            BorderThickness = new Thickness(isToday && !isSelected ? 1 : 0),
        };
        if (isSelected) surface.SetResourceReference(Border.BackgroundProperty, "Pink");
        else surface.Background = Brushes.Transparent;
        if (isToday && !isSelected) surface.SetResourceReference(Border.BorderBrushProperty, "Pink");

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
            Text = "Today", FontSize = 12, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Pink");

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
