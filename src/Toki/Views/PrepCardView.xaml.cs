using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Toki.Calendar;
using Toki.State;
using Toki.Storage;

namespace Toki.Views;

/// <summary>
/// Pre-meeting prep card: title + when, a one-line recap of last time, still-open action items, and the
/// agenda — plus Join-and-record / Later. Content is assembled deterministically by <see cref="PrepBuilder"/>
/// and handed in via <see cref="App.CurrentPrep"/>; this view only renders it (no network, no model).
/// </summary>
public partial class PrepCardView : UserControl
{
    // Same re-entrancy guard as HomeView — NEVER grow the notch during construction.
    bool _ready;
    void Grow() { if (_ready && !App.Headless) App.GrowNotch(); }

    public PrepCardView()
    {
        InitializeComponent();
        Refresh();
        Loaded += (_, _) => { Refresh(); Grow(); };
        _ready = true;
    }

    /// <summary>Repaint from <see cref="App.CurrentPrep"/>. Called by PillWindow.OnStateChanged BEFORE the
    /// height is measured, so the reveal is sized to real content (the clipped-notch lesson).</summary>
    public void Refresh()
    {
        var prep = App.CurrentPrep;
        if (prep is null) return;

        Title.Text = prep.Event.Summary;
        PaintWhen(prep.Event);
        PaintPeople(prep.Event);
        JoinButton.Content = prep.Event.HasMeetLink ? "Join and record" : "Record";

        // LAST TIME
        if (prep.LastTime is { } m)
        {
            RecapHeader.Text = $"LAST TIME ({PrepBuilder.AgoLabel(m.StartedAt)})";
            RecapText.Text = PrepBuilder.Recap(m);
            RecapHost.Visibility = Visibility.Visible;
            FirstTimeHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            RecapHost.Visibility = Visibility.Collapsed;
            FirstTimeHost.Visibility = Visibility.Visible;
        }

        // STILL OPEN
        OpenRows.Children.Clear();
        if (prep.StillOpen.Count > 0)
        {
            foreach (var a in prep.StillOpen) OpenRows.Children.Add(OpenRow(a));
            OpenHost.Visibility = Visibility.Visible;
        }
        else OpenHost.Visibility = Visibility.Collapsed;

        // AGENDA
        AgendaRows.Children.Clear();
        if (prep.Agenda.Count > 0)
        {
            foreach (var line in prep.Agenda) AgendaRows.Children.Add(AgendaRow(line));
            AgendaHost.Visibility = Visibility.Visible;
        }
        else AgendaHost.Visibility = Visibility.Collapsed;

        Grow();
    }

    // "9:41 – 10:11 AM  ·  in 9 min" — the time range reads soft, the countdown reads amber (the
    // reference card's urgency accent). Runs use SetResourceReference so a live theme swap recolours.
    void PaintWhen(CalEvent e)
    {
        WhenText.Inlines.Clear();
        var range = new Run(CalendarRows.TimeRange(e));
        range.SetResourceReference(TextElement.ForegroundProperty, "TextSoft");
        WhenText.Inlines.Add(range);
        // Amber only when it's genuinely urgent (≤15 min / started) — a card previewed further out
        // keeps a quiet countdown, not a false alarm.
        var urgent = CalendarRows.Imminent(e) || e.Until <= TimeSpan.Zero;
        var rel = new Run("  ·  " + (CalendarRows.RelativeTime(e) is { Length: > 0 } r ? r : "now"))
        { FontWeight = FontWeights.SemiBold };
        rel.SetResourceReference(TextElement.ForegroundProperty, urgent ? "Amber" : "TextDim");
        WhenText.Inlines.Add(rel);
    }

    // "5 invited · 3 accepted" + overlapping initials — only from the REAL invite list; a meeting with
    // no guests shows nothing (never fabricated).
    void PaintPeople(CalEvent e)
    {
        AvatarHost.Children.Clear();
        var all = e.Attendees;
        var others = all.Where(a => !a.IsSelf).ToList();
        if (others.Count == 0) { PeopleHost.Visibility = Visibility.Collapsed; return; }

        AvatarHost.Children.Add(CalendarRows.AvatarStack(others, 20, 3));
        var accepted = all.Count(a => string.Equals(a.ResponseStatus, "accepted", StringComparison.OrdinalIgnoreCase));
        PeopleText.Text = accepted > 0 ? $"{all.Count} invited · {accepted} accepted" : $"{all.Count} invited";
        PeopleHost.Visibility = Visibility.Visible;
    }

    FrameworkElement OpenRow(ActionItem a)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var check = new CheckBox
        {
            Style = (Style)FindResource("TokiCheck"), IsChecked = a.Done, Margin = new Thickness(0, 1, 0, 0),
        };
        // Ticking an item off from the card is real — write it through to the store (never in the harness).
        check.Checked += (_, _) => { if (!App.Headless) App.Store.SetActionDone(a.Id, true); };
        check.Unchecked += (_, _) => { if (!App.Headless) App.Store.SetActionDone(a.Id, false); };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var text = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(9, 0, 0, 0), Foreground = (Brush)FindResource("TextSoft"),
        };
        if (!string.IsNullOrWhiteSpace(a.Owner))
        {
            text.Inlines.Add(new Run(a.Owner) { Foreground = (Brush)FindResource("Amber"), FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(" — " + a.Text));
        }
        else text.Inlines.Add(new Run(a.Text));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    FrameworkElement AgendaRow(string line)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        p.Children.Add(new TextBlock
        {
            Text = "•", Foreground = (Brush)FindResource("TextDim"), FontSize = 11,
            Margin = new Thickness(2, 0, 8, 0), FontFamily = (FontFamily)FindResource("UiFont"),
        });
        p.Children.Add(new TextBlock
        {
            Text = line, Foreground = (Brush)FindResource("TextDim"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)FindResource("UiFont"), MaxWidth = 276,   // fits inside the inset box at the 356 card width
        });
        return p;
    }

    void OnJoin(object sender, RoutedEventArgs e) => App.JoinAndRecord();
    void OnLater(object sender, RoutedEventArgs e) => App.DismissPrepCard();
    void OnCalendar(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Calendar);
}
