using Toki.Llm;

namespace Toki.Calendar;

/// <summary>
/// The single facade every UI surface (Home button, future notch preview / prep cards / meeting
/// creator) talks to. Owns the in-memory access token + its expiry and refreshes it lazily, so
/// callers never touch OAuth. The long-lived refresh token lives (DPAPI-encrypted) in AppSettings;
/// the access token is deliberately NOT persisted - one refresh on first use after a restart.
/// </summary>
public sealed class CalendarService
{
    public static CalendarService Instance { get; } = new();

    readonly SemaphoreSlim _tokenGate = new(1, 1);   // one refresh at a time

    /// <summary>Token + expiry as ONE immutable value: reference assignment is atomic, so the
    /// lock-free fast path in <see cref="AccessTokenAsync"/> can never pair a fresh token with a
    /// stale expiry (which caused a spurious refresh/"sign in again" right after connecting).</summary>
    sealed record TokenState(string Token, DateTimeOffset ExpiresUtc);
    TokenState? _token;

    /// <summary>Raised when the connection state flips (connected, disconnected, or a dead grant was
    /// cleared) so the UI can re-render. May fire on a background thread - marshal to the UI thread.</summary>
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
        _token = new TokenState(tokens.AccessToken, tokens.ExpiresUtc);
        ConnectionChanged?.Invoke();
    }

    /// <summary>Wipe the local grant and best-effort revoke it at Google. Always ends disconnected.</summary>
    public async Task DisconnectAsync()
    {
        var s = AppSettings.Current;
        var rt = s.GoogleRefreshToken;
        ClearDeadGrant(s);
        if (!string.IsNullOrEmpty(rt)) await GoogleAuth.RevokeAsync(rt!).ConfigureAwait(false);
    }

    /// <summary>Wipe the stored grant + in-memory token and tell the UI - shared by disconnect,
    /// a rejected refresh, and an undecryptable DPAPI blob. Always ends honestly disconnected.</summary>
    void ClearDeadGrant(AppSettings s)
    {
        s.GoogleRefreshToken = null;
        s.GoogleAccountEmail = null;
        s.Save();
        _token = null;
        ConnectionChanged?.Invoke();
    }

    public async Task<IReadOnlyList<CalEvent>> UpcomingAsync(int days = 7, CancellationToken ct = default)
        => await CalendarClient.ListUpcomingAsync(await AccessTokenAsync(ct).ConfigureAwait(false), days, ct).ConfigureAwait(false);

    public async Task<CalEvent> CreateAsync(NewEvent ev, CancellationToken ct = default)
        => await CalendarClient.InsertAsync(await AccessTokenAsync(ct).ConfigureAwait(false), ev, ct).ConfigureAwait(false);

    public async Task<CalEvent> UpdateAsync(string eventId, NewEvent ev, bool hasMeetAlready, CancellationToken ct = default)
        => await CalendarClient.PatchAsync(await AccessTokenAsync(ct).ConfigureAwait(false), eventId, ev, hasMeetAlready, ct).ConfigureAwait(false);

    /// <summary>The connected calendar provider's display name - the one place the UI names it, so an
    /// Outlook backend later is a string swap, not a UI rewrite.</summary>
    public string ProviderName => "Google Calendar";

    public async Task DeleteAsync(string eventId, CancellationToken ct = default)
        => await CalendarClient.DeleteAsync(await AccessTokenAsync(ct).ConfigureAwait(false), eventId, ct).ConfigureAwait(false);

    /// <summary>A valid access token, refreshing if needed. Throws <see cref="GoogleAuthException"/>
    /// (NeedsReconnect) if there's no grant or the refresh is rejected - in the latter case the dead
    /// token is cleared and the UI is notified, so a failure surfaces as "reconnect", never a crash.</summary>
    async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_token is { } fast && DateTimeOffset.UtcNow < fast.ExpiresUtc) return fast.Token;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } won && DateTimeOffset.UtcNow < won.ExpiresUtc) return won.Token;   // won the race

            var s = AppSettings.Current;
            var rt = s.GoogleRefreshToken;
            if (rt is null)
            {
                // A stored blob that won't DECRYPT (Windows password force-reset, settings file
                // moved to another account) previously kept IsConnected true forever while every
                // fetch failed - a contradictory state that never healed. Clear it honestly.
                if (!string.IsNullOrEmpty(s.GoogleRefreshTokenProtected))
                {
                    App.Log("google refresh token present but undecryptable - clearing the dead grant");
                    ClearDeadGrant(s);
                    throw new GoogleAuthException("Your Google connection couldn't be read - please connect again.", needsReconnect: true);
                }
                throw new GoogleAuthException("Toki isn't connected to Google Calendar.", needsReconnect: true);
            }
            try
            {
                var t = await GoogleAuth.RefreshAsync(rt, ct).ConfigureAwait(false);
                _token = new TokenState(t.AccessToken, t.ExpiresUtc);
                if (!string.IsNullOrEmpty(t.RefreshToken) && t.RefreshToken != rt)   // rotation (rare) → store the new one
                {
                    s.GoogleRefreshToken = t.RefreshToken;
                    s.Save();
                }
                return t.AccessToken;
            }
            catch (GoogleAuthException ex) when (ex.NeedsReconnect)
            {
                ClearDeadGrant(s);   // dead grant - clear it so the UI shows "connect again"
                throw;
            }
        }
        finally { _tokenGate.Release(); }
    }
}
