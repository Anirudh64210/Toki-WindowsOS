namespace Toki.Calendar;

/// <summary>
/// A tiny stale-while-revalidate cache over <see cref="CalendarService.UpcomingAsync"/>, shared by the
/// Home "COMING UP" glance and the Calendar page so they never double-fetch. Reads <see cref="Snapshot"/>
/// for an instant paint; <see cref="GetAsync"/> returns fresh data (coalescing concurrent callers into
/// one request). Raises <see cref="Changed"/> after any refresh/create/delete/update — <b>possibly on a
/// background thread, so subscribers must marshal to the UI dispatcher</b>. Returns seeded samples under
/// <see cref="App.Headless"/> so the render harness never hits the network.
/// </summary>
public sealed class UpcomingCache
{
    public static UpcomingCache Instance { get; } = new();

    UpcomingCache()
    {
        // A connect or disconnect makes any cached events stale (different account, or none).
        if (!App.Headless) CalendarService.Instance.ConnectionChanged += Invalidate;
    }

    const int HorizonDays = 31;                       // one fetch covers Home (7d) + the Calendar's 4 weeks
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    readonly object _gate = new();
    IReadOnlyList<CalEvent>? _cache;
    DateTimeOffset _fetchedUtc;
    Task<IReadOnlyList<CalEvent>>? _inflight;

    /// <summary>Raised after the cache changes (refresh / create / delete / update). May fire off the
    /// UI thread — marshal in the handler.</summary>
    public event Action? Changed;

    /// <summary>The last-known events, or null if never fetched — for an instant, no-await first paint.</summary>
    public IReadOnlyList<CalEvent>? Snapshot => _cache;

    /// <summary>Fresh events (soonest first). Serves the cache when it's within TTL, otherwise fetches
    /// once and shares that request across all callers. Never throws for the render harness.</summary>
    public Task<IReadOnlyList<CalEvent>> GetAsync(bool force = false, CancellationToken ct = default)
    {
        if (App.Headless) return Task.FromResult(Toki.Dev.CalendarSamples.Events());

        lock (_gate)
        {
            var fresh = _cache is not null && DateTimeOffset.UtcNow - _fetchedUtc < Ttl;
            if (fresh && !force) return Task.FromResult(_cache!);
            // The SHARED fetch never runs on one caller's token — a cancel-on-navigate from the
            // first caller would fail every coalesced waiter. Each caller instead abandons its
            // own WAIT via WaitAsync while the fetch runs to completion and still fills the cache.
            _inflight ??= FetchAsync();
            return _inflight.WaitAsync(ct);
        }
    }

    async Task<IReadOnlyList<CalEvent>> FetchAsync()
    {
        try
        {
            var events = await CalendarService.Instance.UpcomingAsync(HorizonDays).ConfigureAwait(false);
            lock (_gate) { _cache = events; _fetchedUtc = DateTimeOffset.UtcNow; }
            Changed?.Invoke();
            return events;
        }
        finally { lock (_gate) { _inflight = null; } }
    }

    /// <summary>Optimistically fold a just-created/updated event into the cache so the UI reflects it
    /// before the next fetch. Keeps the list sorted by start.</summary>
    public void Upsert(CalEvent e)
    {
        lock (_gate)
        {
            var list = (_cache ?? Array.Empty<CalEvent>()).Where(x => x.Id != e.Id).Append(e)
                .OrderBy(x => x.StartUtc).ToList();
            _cache = list;
        }
        Changed?.Invoke();
    }

    /// <summary>Optimistically drop an event from the cache (a just-deleted one).</summary>
    public void Remove(string id)
    {
        lock (_gate) { if (_cache is not null) _cache = _cache.Where(x => x.Id != id).ToList(); }
        Changed?.Invoke();
    }

    /// <summary>Forget everything (on connect/disconnect) so the next read re-fetches from scratch.</summary>
    public void Invalidate()
    {
        lock (_gate) { _cache = null; _fetchedUtc = default; }
        Changed?.Invoke();
    }
}
