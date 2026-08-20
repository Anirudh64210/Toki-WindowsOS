using Toki.Llm;

namespace Toki.Calendar;

/// <summary>
/// The single calendar facade, over two independent accounts (Google and Outlook, both connectable
/// at once, agenda merged by time; connecting or disconnecting one never touches the other).
/// Refresh tokens live DPAPI-encrypted in AppSettings; access tokens are never persisted. Every
/// write routes by <see cref="CalEvent.Provider"/> - an event id from one service means nothing
/// to the other.
/// </summary>
public sealed class CalendarService
{
    public static CalendarService Instance { get; } = new();

    /// <summary>One live connection: its access token, its refresh gate, and the settings it reads.
    /// Two instances, one per provider, so neither can block or invalidate the other.</summary>
    sealed record TokenState(string Token, DateTimeOffset ExpiresUtc);

    readonly SemaphoreSlim _googleGate = new(1, 1);
    readonly SemaphoreSlim _outlookGate = new(1, 1);
    TokenState? _googleToken;
    TokenState? _outlookToken;

    /// <summary>Raised when any connection state flips. May fire on a background thread.</summary>
    public event Action? ConnectionChanged;

    // ── connection state ──

    /// <summary>True when AT LEAST ONE account is connected - what every "is the calendar on?" check
    /// in the UI means.</summary>
    public bool IsConnected => GoogleConnected || OutlookConnected;

    public bool GoogleConnected => AppSettings.Current.GoogleConnected;
    public bool OutlookConnected => AppSettings.Current.OutlookConnected;

    public bool IsConnectedTo(CalendarProvider p) =>
        p == CalendarProvider.Outlook ? OutlookConnected : GoogleConnected;

    public string? AccountEmail => AppSettings.Current.GoogleAccountEmail ?? AppSettings.Current.OutlookAccountEmail;

    public string? AccountEmailFor(CalendarProvider p) =>
        p == CalendarProvider.Outlook ? AppSettings.Current.OutlookAccountEmail : AppSettings.Current.GoogleAccountEmail;

    /// <summary>True once sign-in is set up for a provider. When false, Connect should explain that
    /// rather than opening a broken browser tab.</summary>
    public bool IsConfiguredFor(CalendarProvider p) =>
        p == CalendarProvider.Outlook ? MicrosoftAuth.IsConfigured : GoogleAuth.IsConfigured;

    /// <summary>Kept for the existing Google-only call sites.</summary>
    public bool IsConfigured => GoogleAuth.IsConfigured;

    /// <summary>What to call the connection in the UI. With both on, it is neither one of them.</summary>
    public string ProviderName =>
        GoogleConnected && OutlookConnected ? "Google Calendar and Outlook"
        : OutlookConnected ? CalendarProvider.Outlook.Name()
        : CalendarProvider.Google.Name();

    // ── connect / disconnect ──

    public async Task ConnectAsync(CancellationToken ct = default) =>
        await ConnectAsync(CalendarProvider.Google, ct).ConfigureAwait(false);

    /// <summary>Interactive sign-in for one provider. Leaves the other connection alone.</summary>
    public async Task ConnectAsync(CalendarProvider provider, CancellationToken ct = default)
    {
        var s = AppSettings.Current;
        if (provider == CalendarProvider.Outlook)
        {
            var t = await MicrosoftAuth.ConnectAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(t.RefreshToken)) s.OutlookRefreshToken = t.RefreshToken;
            if (!string.IsNullOrEmpty(t.Email)) s.OutlookAccountEmail = t.Email;
            s.Save();
            _outlookToken = new TokenState(t.AccessToken, t.ExpiresUtc);
        }
        else
        {
            var t = await GoogleAuth.ConnectAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(t.RefreshToken)) s.GoogleRefreshToken = t.RefreshToken;
            if (!string.IsNullOrEmpty(t.Email)) s.GoogleAccountEmail = t.Email;
            s.Save();
            _googleToken = new TokenState(t.AccessToken, t.ExpiresUtc);
        }
        ConnectionChanged?.Invoke();
    }

    public async Task DisconnectAsync() => await DisconnectAsync(CalendarProvider.Google).ConfigureAwait(false);

    /// <summary>Wipe one grant locally and, where the provider supports it, revoke it at the source.
    /// Always ends disconnected from that provider, whatever the network did.</summary>
    public async Task DisconnectAsync(CalendarProvider provider)
    {
        var s = AppSettings.Current;
        if (provider == CalendarProvider.Outlook)
        {
            ClearDeadGrant(s, provider);
            // Microsoft has no revoke endpoint for a public client; consent is withdrawn from the
            // user's own Microsoft account page. The local grant is gone either way.
            return;
        }
        var rt = s.GoogleRefreshToken;
        ClearDeadGrant(s, provider);
        if (!string.IsNullOrEmpty(rt)) await GoogleAuth.RevokeAsync(rt!).ConfigureAwait(false);
    }

    /// <summary>Wipe one stored grant + its in-memory token and tell the UI - shared by disconnect, a
    /// rejected refresh, and an undecryptable DPAPI blob. Always ends honestly disconnected.</summary>
    void ClearDeadGrant(AppSettings s, CalendarProvider provider)
    {
        if (provider == CalendarProvider.Outlook)
        {
            s.OutlookRefreshToken = null;
            s.OutlookAccountEmail = null;
            _outlookToken = null;
        }
        else
        {
            s.GoogleRefreshToken = null;
            s.GoogleAccountEmail = null;
            _googleToken = null;
        }
        s.Save();
        ConnectionChanged?.Invoke();
    }

    // ── reading ──

    /// <summary>
    /// The merged agenda across every connected account, soonest first.
    ///
    /// One provider failing must NOT empty the agenda: if Outlook is unreachable, the Google events
    /// still show. The exception is only rethrown when NOTHING came back, because then the user really
    /// does need to know rather than stare at a blank week.
    /// </summary>
    public async Task<IReadOnlyList<CalEvent>> UpcomingAsync(int days = 7, CancellationToken ct = default)
    {
        var all = new List<CalEvent>();
        Exception? firstFailure = null;
        var attempted = 0;

        if (GoogleConnected)
        {
            attempted++;
            try
            {
                all.AddRange(await CalendarClient.ListUpcomingAsync(
                    await AccessTokenAsync(CalendarProvider.Google, ct).ConfigureAwait(false), days, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailure ??= ex;
                App.Log($"calendar: Google fetch failed ({ex.Message})");
            }
        }

        if (OutlookConnected)
        {
            attempted++;
            try
            {
                all.AddRange(await OutlookClient.ListUpcomingAsync(
                    await AccessTokenAsync(CalendarProvider.Outlook, ct).ConfigureAwait(false), days,
                    AppSettings.Current.OutlookAccountEmail, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailure ??= ex;
                App.Log($"calendar: Outlook fetch failed ({ex.Message})");
            }
        }

        if (all.Count == 0 && firstFailure is not null && attempted > 0) throw firstFailure;
        return all.OrderBy(e => e.StartUtc).ToList();
    }

    // ── writing (routed by the event's own provider) ──

    /// <summary>Create on <paramref name="provider"/>, defaulting to whichever account is connected -
    /// and to Google when both are, because that is where Toki's Meet-link support lives.</summary>
    public async Task<CalEvent> CreateAsync(NewEvent ev, CancellationToken ct = default) =>
        await CreateAsync(ev, DefaultWriteProvider, ct).ConfigureAwait(false);

    public async Task<CalEvent> CreateAsync(NewEvent ev, CalendarProvider provider, CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(provider, ct).ConfigureAwait(false);
        return provider == CalendarProvider.Outlook
            ? await OutlookClient.InsertAsync(token, ev, AppSettings.Current.OutlookAccountEmail, ct).ConfigureAwait(false)
            : await CalendarClient.InsertAsync(token, ev, ct).ConfigureAwait(false);
    }

    public async Task<CalEvent> UpdateAsync(string eventId, NewEvent ev, bool hasMeetAlready, CancellationToken ct = default) =>
        await UpdateAsync(eventId, ev, hasMeetAlready, DefaultWriteProvider, ct).ConfigureAwait(false);

    public async Task<CalEvent> UpdateAsync(string eventId, NewEvent ev, bool hasMeetAlready,
        CalendarProvider provider, CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(provider, ct).ConfigureAwait(false);
        return provider == CalendarProvider.Outlook
            ? await OutlookClient.PatchAsync(token, eventId, ev, hasMeetAlready, AppSettings.Current.OutlookAccountEmail, ct).ConfigureAwait(false)
            : await CalendarClient.PatchAsync(token, eventId, ev, hasMeetAlready, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string eventId, CancellationToken ct = default) =>
        await DeleteAsync(eventId, DefaultWriteProvider, ct).ConfigureAwait(false);

    public async Task DeleteAsync(string eventId, CalendarProvider provider, CancellationToken ct = default)
    {
        var token = await AccessTokenAsync(provider, ct).ConfigureAwait(false);
        if (provider == CalendarProvider.Outlook)
            await OutlookClient.DeleteAsync(token, eventId, ct).ConfigureAwait(false);
        else
            await CalendarClient.DeleteAsync(token, eventId, ct).ConfigureAwait(false);
    }

    /// <summary>Where a NEW event goes when the caller has no event to take the answer from. Google
    /// when it is connected, because that path also attaches a Meet link.</summary>
    public CalendarProvider DefaultWriteProvider =>
        GoogleConnected ? CalendarProvider.Google : CalendarProvider.Outlook;

    // ── tokens ──

    /// <summary>A valid access token for one provider, refreshing if needed. Throws
    /// <see cref="GoogleAuthException"/> (NeedsReconnect) when the grant is missing or rejected - in
    /// which case the dead grant is cleared and the UI notified, so a failure surfaces as "connect
    /// again" rather than a crash.</summary>
    async Task<string> AccessTokenAsync(CalendarProvider provider, CancellationToken ct)
    {
        var outlook = provider == CalendarProvider.Outlook;
        var cached = outlook ? _outlookToken : _googleToken;
        if (cached is { } fast && DateTimeOffset.UtcNow < fast.ExpiresUtc) return fast.Token;

        var gate = outlook ? _outlookGate : _googleGate;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var again = outlook ? _outlookToken : _googleToken;
            if (again is { } won && DateTimeOffset.UtcNow < won.ExpiresUtc) return won.Token;   // won the race

            var s = AppSettings.Current;
            var rt = outlook ? s.OutlookRefreshToken : s.GoogleRefreshToken;
            if (rt is null)
            {
                // A stored blob that will not decrypt (password force-reset, settings copied to
                // another account) must be cleared honestly, or IsConnected stays true forever
                // while every fetch fails.
                var protectedBlob = outlook ? s.OutlookRefreshTokenProtected : s.GoogleRefreshTokenProtected;
                if (!string.IsNullOrEmpty(protectedBlob))
                {
                    App.Log($"{provider} refresh token present but undecryptable - clearing the dead grant");
                    ClearDeadGrant(s, provider);
                    throw new GoogleAuthException($"Your {provider.ShortName()} connection couldn't be read - please connect again.", needsReconnect: true);
                }
                throw new GoogleAuthException($"Toki isn't connected to {provider.Name()}.", needsReconnect: true);
            }

            try
            {
                var t = outlook
                    ? await MicrosoftAuth.RefreshAsync(rt, ct).ConfigureAwait(false)
                    : await GoogleAuth.RefreshAsync(rt, ct).ConfigureAwait(false);

                if (outlook) _outlookToken = new TokenState(t.AccessToken, t.ExpiresUtc);
                else _googleToken = new TokenState(t.AccessToken, t.ExpiresUtc);

                // Microsoft ROTATES the refresh token on every refresh and invalidates the old one;
                // failing to store what comes back kills the Outlook connection at the next restart.
                if (!string.IsNullOrEmpty(t.RefreshToken) && t.RefreshToken != rt)
                {
                    if (outlook) s.OutlookRefreshToken = t.RefreshToken;
                    else s.GoogleRefreshToken = t.RefreshToken;
                    s.Save();
                }
                return t.AccessToken;
            }
            catch (GoogleAuthException ex) when (ex.NeedsReconnect)
            {
                ClearDeadGrant(s, provider);   // dead grant - clear it so the UI shows "connect again"
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
