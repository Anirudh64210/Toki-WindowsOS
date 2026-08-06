using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Toki.Calendar;
using Toki.State;
using Toki.Storage;

namespace Toki.Views;

/// <summary>
/// Pre-meeting prep card: title + when, a one-line recap of last time, still-open action items, and the
/// agenda - plus Join-and-record / Later. Content is assembled deterministically by <see cref="PrepBuilder"/>
/// and handed in via <see cref="App.CurrentPrep"/>; this view only renders it (no network, no model).
///
/// It is also where a meeting's STICKY NOTE is written. Pressing Edit turns the card itself into the
/// note - the recap, open items and agenda arrive as real text you carry on writing in. The note is a
/// row in the database from that moment, saved on every pause in typing, and which note this meeting
/// uses is always the user's explicit choice (see <see cref="ShowNotePicker"/>): Toki never matches
/// notes to meetings by title, because titles change and two meetings are often called the same thing.
/// </summary>
public partial class PrepCardView : UserControl
{
    // Same re-entrancy guard as HomeView - NEVER grow the notch during construction.
    bool _ready;
    void Grow() { if (_ready && !App.Headless) App.GrowNotch(); }

    NoteEditor? _scratch;
    long? _stickyId;                 // the note this card is currently editing
    readonly System.Windows.Threading.DispatcherTimer _scratchSave =
        new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };

    public PrepCardView()
    {
        InitializeComponent();
        _scratchSave.Tick += (_, _) => { _scratchSave.Stop(); FlushScratch(); };
        Refresh();
        Loaded += (_, _) => { Refresh(); Grow(); };
        // Leaving the card (back to recording, Join, dismissal) must not cost the user a sentence.
        Unloaded += (_, _) => { _scratchSave.Stop(); FlushScratch(); };
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

        PaintScratch(prep.Event.Id);
        PaintChrome();
        Grow();
    }

    // ── the sticky note ──

    /// <summary>Open whichever note this meeting is linked to. Once you've written on a card it STAYS
    /// written - reopening shows your version, not the generated one, because your words outrank
    /// Toki's. A meeting with no note yet shows the generated card, and Edit starts one.</summary>
    void PaintScratch(string eventId)
    {
        if (App.Headless) { ShowReadOnly(); return; }

        _stickyId = App.Store.StickyForEvent(eventId);
        var body = _stickyId is { } id ? App.Store.GetSticky(id)?.Body ?? "" : "";
        if (_stickyId is not null && body.Length > 0) EnterEdit(body);
        else ShowReadOnly();
    }

    void ShowReadOnly()
    {
        ContextBox.Visibility = Visibility.Visible;
        ScratchHost.Visibility = Visibility.Collapsed;
        EditButton.Visibility = Visibility.Visible;
    }

    /// <summary>Turn the card into the note. The generated content comes along as real text - open
    /// items as tick boxes, agenda as bullets - so you carry on writing rather than starting over.</summary>
    void EnterEdit(string markdown)
    {
        if (_scratch is null)
        {
            _scratch = new NoteEditor { MinHeight = 110, MaxHeight = 260, Compact = true };
            _scratch.Changed += (_, _) => { _scratchSave.Stop(); _scratchSave.Start(); };
            ScratchSlot.Content = _scratch;
        }
        _scratch.Markdown = markdown;
        ContextBox.Visibility = Visibility.Collapsed;
        ScratchHost.Visibility = Visibility.Visible;
        EditButton.Visibility = Visibility.Collapsed;   // you're already in it
    }

    void FlushScratch()
    {
        if (_scratch is null || ScratchHost.Visibility != Visibility.Visible) return;
        if (App.Headless || _stickyId is not { } id) return;
        try { App.Store.UpdateSticky(id, _scratch.Markdown); }
        catch (Exception ex) { App.Log($"sticky save failed: {ex.Message}"); }
    }

    /// <summary>Start writing. If this meeting has no note yet, one is created NOW - a row on disk from
    /// the first keystroke, so a crash mid-sentence can't cost anything - seeded with the card's own
    /// content and linked to this event.</summary>
    void OnEdit(object sender, RoutedEventArgs e)
    {
        if (App.CurrentPrep is not { } prep) return;

        if (_stickyId is null && !App.Headless)
        {
            try
            {
                _stickyId = App.Store.CreateSticky(prep.Event.Summary, PrepBuilder.ToMarkdown(prep));
                App.Store.LinkStickyToEvent(prep.Event.Id, _stickyId.Value);
            }
            catch (Exception ex) { App.Log($"sticky create failed: {ex.Message}"); }
        }

        var body = _stickyId is { } id && !App.Headless
            ? App.Store.GetSticky(id)?.Body ?? "" : PrepBuilder.ToMarkdown(prep);
        EnterEdit(body.Length > 0 ? body : PrepBuilder.ToMarkdown(prep));
        Grow();
        _scratch?.FocusEditor();
    }

    /// <summary>
    /// Choose which note this meeting uses - an earlier one, or a fresh one.
    ///
    /// This exists instead of guessing. Toki has no reliable way to know that today's "Weekly sync" is
    /// the same conversation as last week's, and getting it wrong either buries your notes or shows you
    /// someone else's; so the choice is simply yours, and it's remembered by id.
    /// </summary>
    void OnPickNote(object sender, RoutedEventArgs e)
    {
        if (App.CurrentPrep is not { } prep || App.Headless) return;
        FlushScratch();

        var items = new List<(string Label, Action Pick)>
        {
            ("Start a new note", () => UseNote(null, prep)),
        };
        foreach (var s in App.Store.RecentStickies(12))
        {
            var label = (string.IsNullOrWhiteSpace(s.Title) ? "Untitled" : s.Title)
                        + "  ·  " + PrepBuilder.AgoLabel(s.UpdatedAt.ToLocalTime());
            var id = s.Id;
            items.Add((label, () => UseNote(id, prep)));
        }

        NotePicker.Show(NoteButton, items, _stickyId is { } cur
            ? App.Store.GetSticky(cur)?.Title : null);
    }

    void UseNote(long? stickyId, PrepContent prep)
    {
        if (stickyId is null)
        {
            // A brand-new note starts from the generated card, same as the first Edit does.
            stickyId = App.Store.CreateSticky(prep.Event.Summary, PrepBuilder.ToMarkdown(prep));
        }
        App.Store.LinkStickyToEvent(prep.Event.Id, stickyId.Value);
        _stickyId = stickyId;
        EnterEdit(App.Store.GetSticky(stickyId.Value)?.Body ?? "");
        Grow();
        _scratch?.FocusEditor();
    }

    // ── recording-aware chrome (N4) ──

    /// <summary>Mid-recording the card is a side-trip: drop the Join/Later pair - you're already in the
    /// meeting, so "Join and record" would be nonsense. Back is ALWAYS shown, because while a card is
    /// armed hovering the peek opens this card, and without a way out you could never reach Home.</summary>
    void PaintChrome()
    {
        var recording = App.IsRecording;
        BackButton.ToolTip = recording ? "Back to recording" : "Back";
        JoinButton.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        LaterButton.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
    }

    void OnBack(object sender, RoutedEventArgs e)
    {
        FlushScratch();
        App.State.TryGo(App.IsRecording ? PillState.RecordingOpen : PillState.Home);
    }

    /// <summary>Dev/render only: show edit mode with planted text, without touching the database.</summary>
    public void DebugOpenScratch(string markdown) { EnterEdit(markdown); Grow(); }

    // "9:41 – 10:11 AM  ·  in 9 min" - the time range reads soft, the countdown reads amber (the
    // reference card's urgency accent). Runs use SetResourceReference so a live theme swap recolours.
    void PaintWhen(CalEvent e)
    {
        WhenText.Inlines.Clear();
        var range = new Run(CalendarRows.TimeRange(e));
        range.SetResourceReference(TextElement.ForegroundProperty, "TextSoft");
        WhenText.Inlines.Add(range);
        // Amber only when it's genuinely urgent (≤15 min / started) - a card previewed further out
        // keeps a quiet countdown, not a false alarm.
        var urgent = CalendarRows.Imminent(e) || e.Until <= TimeSpan.Zero;
        var rel = new Run("  ·  " + (CalendarRows.RelativeTime(e) is { Length: > 0 } r ? r : "now"))
        { FontWeight = FontWeights.SemiBold };
        rel.SetResourceReference(TextElement.ForegroundProperty, urgent ? "Amber" : "TextDim");
        WhenText.Inlines.Add(rel);
    }

    // "5 invited · 3 accepted" + overlapping initials - only from the REAL invite list; a meeting with
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
        // Ticking an item off from the card is real - write it through to the store (never in the harness).
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
            text.Inlines.Add(new Run(" - " + a.Text));
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

    void OnJoin(object sender, RoutedEventArgs e) { FlushScratch(); App.JoinAndRecord(); }
    void OnLater(object sender, RoutedEventArgs e) { FlushScratch(); App.DismissPrepCard(); }
    void OnCalendar(object sender, RoutedEventArgs e) => App.State.TryGo(PillState.Calendar);
}
