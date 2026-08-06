using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Toki.State;

namespace Toki.Calendar;

/// <summary>
/// Watches the calendar and surfaces a <see cref="PillState.PrepCard"/> ~5 minutes before a real meeting.
/// Deliberately restrained: one card per meeting-instance ever, only in the lead window, only for real
/// meetings, only while the notch is idle (Hidden/Collapsed), never during a fullscreen app, never twice.
/// Reads the network only via <see cref="UpcomingCache"/> (which the same-account Home already warms), so
/// it adds at most one fetch per minute. Never runs headless.
/// </summary>
public sealed class PrepWatcher
{
    public static PrepWatcher Instance { get; } = new();

    // Minutes before a meeting to surface the card. Env override for testing (mirrors TOKI_NO_EDGE).
    static readonly int LeadMinutes =
        int.TryParse(Environment.GetEnvironmentVariable("TOKI_PREP_LEAD"), out var v) && v > 0 ? v : 5;

    readonly DispatcherTimer _timer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromSeconds(30) };
    readonly HashSet<string> _shown = new();   // event-instance ids already surfaced or snoozed
    bool _wired;

    /// <summary>Is a prep card currently available to open? The notch asks this to decide whether
    /// hovering the peek should show the card, and the recording page asks it to decide whether to
    /// offer its "Prep card" button.</summary>
    public static bool Armed => App.CurrentPrep is not null;

    public void Start()
    {
        if (App.Headless) return;
        if (!_wired)
        {
            _wired = true;
            _timer.Tick += (_, _) => Tick();
            // A fresh fetch completing mid-window: react promptly (marshal - the event fires off-thread).
            UpcomingCache.Instance.Changed += () =>
                Application.Current?.Dispatcher.BeginInvoke(new Action(TryRaise));
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() { if (_timer.IsEnabled) _timer.Stop(); }

    /// <summary>"Later" (and internal): never raise this event again this session.</summary>
    public void Snooze(string eventId) => _shown.Add(eventId);

    void Tick()
    {
        if (!CalendarService.Instance.IsConnected) return;
        WarmCache();                     // keep UpcomingCache fresh (coalesced by its 60s TTL)

        // Disarm a card whose meeting is behind us - otherwise hovering the peek would keep opening a
        // stale prep card instead of Home. Never while recording: the card (and its scratchpad) has to
        // stay reachable from the recording page for the whole meeting, which is the point of N4.
        // Note this does NOT touch PrepScratch - what the user typed outlives the card, and N5 decides
        // whether to keep it.
        if (!App.IsRecording && App.State.Current != PillState.PrepCard
            && App.CurrentPrep is { } cp && cp.Event.EndUtc < DateTimeOffset.UtcNow)
            App.CurrentPrep = null;

        TryRaise();
    }

    void TryRaise()
    {
        var events = UpcomingCache.Instance.Snapshot;
        if (events is null) return;

        var now = DateTimeOffset.UtcNow;
        var lead = TimeSpan.FromMinutes(LeadMinutes);
        var cand = events
            .Where(e => !_shown.Contains(e.Id) && Worthy(e) && e.StartUtc > now && e.StartUtc - now <= lead)
            .OrderBy(e => e.StartUtc)
            .FirstOrDefault();
        if (cand is null) return;

        // ARM: the card becomes available, so hovering the peek opens it and the recording page can
        // offer its "Prep card" button.
        App.CurrentPrep = PrepBuilder.Build(cand, App.Store);
        _shown.Add(cand.Id);
        var mins = (int)(cand.StartUtc - now).TotalMinutes;

        // …and SURFACE it once, quietly (owner, 2026-08-06: "meeting is about to start, user gets auto
        // prompted to take a look at his notes"). This was removed a day earlier when the card was made
        // hover-only, and removing it took the "your meeting is starting" nudge with it - notes you have
        // to remember to go and look at are notes you don't look at.
        //
        // Restraint is still the design, so it opens ONLY when opening it costs nothing: the notch must
        // be idle (never interrupt a recording, an answer, or a page the user navigated to), and never
        // over a fullscreen app. If the moment isn't right the card simply stays armed, one hover away.
        if (Surfaceable())
        {
            App.State.TryGo(PillState.PrepCard);
            App.Log($"prep: surfaced card for '{cand.Summary}' ({mins} min out)");
        }
        else App.Log($"prep: armed card for '{cand.Summary}' ({mins} min out) - notch busy, hover to open");

        // Prune ids no longer upcoming so the set can't grow forever.
        _shown.RemoveWhere(id => events.All(e => e.Id != id));
    }

    /// <summary>Is now a moment when taking the notch would be welcome rather than rude?</summary>
    static bool Surfaceable() =>
        !App.IsRecording
        && App.State.Current is PillState.Hidden or PillState.Collapsed
        && !PillWindow.QuietFullscreen();

    // A real meeting worth prepping: not all-day, and either a video link or at least one other guest.
    static bool Worthy(CalEvent e) => !e.IsAllDay && (e.HasMeetLink || e.Attendees.Any(a => !a.IsSelf));

    async void WarmCache()
    {
        try { await UpcomingCache.Instance.GetAsync(); } catch { /* offline - the next tick retries */ }
    }

}
