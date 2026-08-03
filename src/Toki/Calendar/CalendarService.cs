using Toki.Llm;

namespace Toki.Calendar;

/// <summary>
/// The single facade every UI surface (Home button, future notch preview / prep cards / meeting
/// creator) talks to. Owns the in-memory access token + its expiry and refreshes it lazily, so
/// callers never touch OAuth. The long-lived refresh token lives (DPAPI-encrypted) in AppSettings;
/// the access token is deliberately NOT persisted — one refresh on first use after a restart.
/// </summary>
public sealed class CalendarService
{
    public static CalendarService Instance { get; } = new();

    readonly SemaphoreSlim _tokenGate = new(1, 1);   // one refresh at a time
    string? _accessToken;
    DateTimeOffset _expiresUtc;

    /// <summary>Raised when the connection state flips (connected, disconnected, or a dead grant was
    /// cleared) so the UI can re-render. May fire on a background thread — marshal to the UI thread.</summary>
    public event Action? ConnectionChanged;

    public bool IsConnected => AppSettings.Current.GoogleConnected;
    public string? AccountEmail => AppSettings.Current.GoogleAccountEmail;

    /// <summary>True once an OAuth client is available (env/file/embedded). When false, the Connect
    /// action should explain Toki's Google sign-in isn't set up rather than open a broken flow.</summary>
    public bool IsConfigured => GoogleAuth.IsConfigured;

    /// <summary>Interactive sign-in. Stores the refresh token (encrypted) + account email on success.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var tokens = await GoogleAuth.ConnectAsync(ct).ConfigureAwait(false);
        var s = AppSettings.Current;
        if (!string.IsNullOrEmpty(tokens.RefreshToken)) s.GoogleRefreshToken = tokens.RefreshToken;
        if (!string.IsNullOrEmpty(tokens.Email)) s.GoogleAccountEmail = tokens.Email;
        s.Save();
        _accessToken = tokens.AccessToken;
        _expiresUtc = tokens.ExpiresUtc;
        ConnectionChanged?.Invoke();
    }

    /// <summary>Wipe the local grant and best-effort revoke it at Google. Always ends disconnected.</summary>
    public async Task DisconnectAsync()
    {
        var s = AppSettings.Current;
        var rt = s.GoogleRefreshToken;
        s.GoogleRefreshToken = null;
        s.GoogleAccountEmail = null;
        s.Save();
        _accessToken = null;
        _expiresUtc = default;
        ConnectionChanged?.Invoke();
        if (!string.IsNullOrEmpty(rt)) await GoogleAuth.RevokeAsync(rt!).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalEvent>> UpcomingAsync(int days = 7, CancellationToken ct = default)
        => await CalendarClient.ListUpcomingAsync(await AccessTokenAsync(ct).ConfigureAwait(false), days, ct).ConfigureAwait(false);

    public async Task<CalEvent> CreateAsync(NewEvent ev, CancellationToken ct = default)
        => await CalendarClient.InsertAsync(await AccessTokenAsync(ct).ConfigureAwait(false), ev, ct).ConfigureAwait(false);

    public async Task DeleteAsync(string eventId, CancellationToken ct = default)
        => await CalendarClient.DeleteAsync(await AccessTokenAsync(ct).ConfigureAwait(false), eventId, ct).ConfigureAwait(false);

    /// <summary>A valid access token, refreshing if needed. Throws <see cref="GoogleAuthException"/>
    /// (NeedsReconnect) if there's no grant or the refresh is rejected — in the latter case the dead
    /// token is cleared and the UI is notified, so a failure surfaces as "reconnect", never a crash.</summary>
    async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresUtc) return _accessToken;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresUtc) return _accessToken;   // won the race

            var s = AppSettings.Current;
            var rt = s.GoogleRefreshToken
                ?? throw new GoogleAuthException("Toki isn't connected to Google Calendar.", needsReconnect: true);
            try
            {
                var t = await GoogleAuth.RefreshAsync(rt, ct).ConfigureAwait(false);
                _accessToken = t.AccessToken;
                _expiresUtc = t.ExpiresUtc;
                if (!string.IsNullOrEmpty(t.RefreshToken) && t.RefreshToken != rt)   // rotation (rare) → store the new one
                {
                    s.GoogleRefreshToken = t.RefreshToken;
                    s.Save();
                }
                return _accessToken;
            }
            catch (GoogleAuthException ex) when (ex.NeedsReconnect)
            {
                s.GoogleRefreshToken = null;   // dead grant — clear it so the UI shows "connect again"
                s.GoogleAccountEmail = null;
                s.Save();
                _accessToken = null;
                _expiresUtc = default;
                ConnectionChanged?.Invoke();
                throw;
            }
        }
        finally { _tokenGate.Release(); }
    }
}
