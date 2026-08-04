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

    /// <summary>Guard: only raise a card while the notch is idle. Overridable for tests.</summary>
    public Func<bool> CanPrompt { get; set; } =
        () => App.State.Current is PillState.Hidden or PillState.Collapsed;

    public void Start()
    {
        if (App.Headless) return;
        if (!_wired)
        {
            _wired = true;
            _timer.Tick += (_, _) => Tick();
            // A fresh fetch completing mid-window: react promptly (marshal — the event fires off-thread).
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

        // Auto-retire a card the user walked away from — a prompt must not squat through the meeting.
        if (App.State.Current == PillState.PrepCard && App.CurrentPrep is { } cp
            && cp.Event.StartUtc < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2))
            App.State.TryGo(PillState.Hidden);

        TryRaise();
    }

    void TryRaise()
    {
        if (!CanPrompt() || Suppressed()) return;
        var events = UpcomingCache.Instance.Snapshot;
        if (events is null) return;

        var now = DateTimeOffset.UtcNow;
        var lead = TimeSpan.FromMinutes(LeadMinutes);
        var cand = events
            .Where(e => !_shown.Contains(e.Id) && Worthy(e) && e.StartUtc > now && e.StartUtc - now <= lead)
            .OrderBy(e => e.StartUtc)
            .FirstOrDefault();
        if (cand is null) return;

        App.CurrentPrep = PrepBuilder.Build(cand, App.Store);
        if (App.State.TryGo(PillState.PrepCard))
        {
            _shown.Add(cand.Id);
            App.Log($"prep: surfaced card for '{cand.Summary}' ({(int)(cand.StartUtc - now).TotalMinutes} min out)");
        }
        // Prune ids no longer upcoming so the set can't grow forever.
        _shown.RemoveWhere(id => events.All(e => e.Id != id));
    }

    // A real meeting worth prepping: not all-day, and either a video link or at least one other guest.
    static bool Worthy(CalEvent e) => !e.IsAllDay && (e.HasMeetLink || e.Attendees.Any(a => !a.IsSelf));

    async void WarmCache()
    {
        try { await UpcomingCache.Instance.GetAsync(); } catch { /* offline — the next tick retries */ }
    }

    // Don't interrupt a fullscreen app / game / presentation (same quiet check the edge-summon uses).
    static bool Suppressed()
    {
        try
        {
            return SHQueryUserNotificationState(out var st) == 0
                && st is QunsBusy or QunsRunningD3dFullScreen or QunsPresentationMode;
        }
        catch { return false; }
    }

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    const int QunsBusy = 2, QunsRunningD3dFullScreen = 3, QunsPresentationMode = 4;
}
