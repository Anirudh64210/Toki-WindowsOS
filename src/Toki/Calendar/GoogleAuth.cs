using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toki.Calendar;

/// <summary>Tokens returned by an auth or refresh round-trip. The refresh token is present
/// only on the FIRST authorization (Google doesn't resend it on refresh); the email is present
/// only when an id_token came back (i.e. the openid/email scope was granted).</summary>
public sealed record GoogleTokens(string AccessToken, DateTimeOffset ExpiresUtc, string? RefreshToken, string? Email);

/// <summary>A clean, human-readable auth failure. <see cref="NeedsReconnect"/> is true when the
/// stored grant is dead (revoked / expired / invalid) and the user must sign in again - the caller
/// clears the token and drops to the "Not connected" state rather than crashing mid-meeting.</summary>
public sealed class GoogleAuthException : Exception
{
    public bool NeedsReconnect { get; }
    public GoogleAuthException(string message, bool needsReconnect) : base(message) => NeedsReconnect = needsReconnect;
}

/// <summary>
/// Google OAuth 2.0 for an installed (desktop) app: the loopback-redirect flow with PKCE.
/// The user's own browser talks directly to Google; Google redirects to a one-shot HttpListener
/// on 127.0.0.1; we exchange the code for tokens. Nothing routes through a Toki server - the whole
/// flow is browser ↔ Google ↔ localhost, so hard constraint #1 ($0 upkeep, no backend) holds.
///
/// Networking style mirrors <see cref="Toki.Llm.ApiLlm"/>: one static HttpClient, System.Text.Json,
/// short human error strings. Tokens (access, refresh, id_token, the auth code) are NEVER logged.
/// </summary>
public static class GoogleAuth
{
    const string AuthEndpoint  = "https://accounts.google.com/o/oauth2/v2/auth";
    const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Requested once: read + create/edit events (the meeting-creator needs write), plus
    /// openid+email so we can show "Connected as …". Every Calendar-event scope is equally "sensitive"
    /// to Google, so asking for the read-only variant now would only force a second consent later.</summary>
    public const string Scopes = "openid email https://www.googleapis.com/auth/calendar.events";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ── OAuth client credentials ──
    // For an installed app Google's client_id + client_secret are, in Google's own words, "not treated
    // as a secret" - they ship in every desktop app (rclone, gcloud, Google's own libraries). Real
    // security comes from PKCE + the exact loopback redirect + the user consenting to THEIR OWN data;
    // a leaked secret buys an attacker nothing without a user clicking consent. Do NOT "fix" this into
    // a backend - that would break the $0-upkeep constraint. Resolved in order:
    //   1. env vars TOKI_GOOGLE_CLIENT_ID / TOKI_GOOGLE_CLIENT_SECRET (dev),
    //   2. %APPDATA%\Toki\google_client.json (the file Google's console hands you - drop it in),
    //   3. the embedded consts below (filled at ship time).
    const string EmbeddedClientId = "";
    const string EmbeddedClientSecret = "";

    static (string Id, string Secret)? _cred;
    static bool _credResolved;

    /// <summary>True once an OAuth client is available from any source - the Connect button/self-test
    /// check this and show a clear "not set up" message instead of a broken flow.</summary>
    public static bool IsConfigured => Credentials() is not null;

    static (string Id, string Secret)? Credentials()
    {
        if (_credResolved) return _cred;
        _credResolved = true;
        _cred = ResolveCredentials();
        return _cred;
    }

    static (string Id, string Secret)? ResolveCredentials()
    {
        var envId = Environment.GetEnvironmentVariable("TOKI_GOOGLE_CLIENT_ID");
        var envSecret = Environment.GetEnvironmentVariable("TOKI_GOOGLE_CLIENT_SECRET");
        if (!string.IsNullOrWhiteSpace(envId) && !string.IsNullOrWhiteSpace(envSecret))
            return (envId.Trim(), envSecret.Trim());

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "google_client.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                // Google's downloaded credential file wraps everything under "installed" (or "web").
                if (root.TryGetProperty("installed", out var ins)) root = ins;
                else if (root.TryGetProperty("web", out var web)) root = web;
                var fid = root.TryGetProperty("client_id", out var a) ? a.GetString() : null;
                var fsec = root.TryGetProperty("client_secret", out var b) ? b.GetString() : null;
                if (!string.IsNullOrWhiteSpace(fid) && !string.IsNullOrWhiteSpace(fsec))
                    return (fid!.Trim(), fsec!.Trim());
            }
        }
        catch { /* malformed file → fall through to embedded */ }

        return EmbeddedClientId.Length > 0 && EmbeddedClientSecret.Length > 0
            ? (EmbeddedClientId, EmbeddedClientSecret)
            : null;
    }

    // ── the interactive sign-in ──

    /// <summary>Run the full loopback + PKCE sign-in: open the browser, catch the redirect, exchange
    /// the code for tokens. Throws <see cref="GoogleAuthException"/> (never a raw exception) on any
    /// failure. Times out after 5 minutes if the user never finishes.</summary>
    public static async Task<GoogleTokens> ConnectAsync(CancellationToken ct = default)
    {
        var cred = Credentials()
            ?? throw new GoogleAuthException("Google isn't set up yet (missing OAuth client).", needsReconnect: false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        var verifier = NewCodeVerifier();
        var challenge = CodeChallenge(verifier);
        var state = NewState();

        var (listener, redirectUri) = StartLoopbackListener();
        try
        {
            var url = BuildAuthUrl(cred.Id, redirectUri, Scopes, challenge, state);
            OpenBrowser(url);

            var code = await AwaitRedirect(listener, state, timeout.Token).ConfigureAwait(false);
            return await ExchangeAsync(code, verifier, redirectUri, cred, timeout.Token).ConfigureAwait(false);
        }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>Trade the stored refresh token for a fresh access token. On <c>invalid_grant</c>
    /// (revoked / expired) throws a <see cref="GoogleAuthException"/> with NeedsReconnect = true.</summary>
    public static async Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var cred = Credentials()
            ?? throw new GoogleAuthException("Google isn't set up yet (missing OAuth client).", needsReconnect: false);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = cred.Id,
            ["client_secret"] = cred.Secret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };
        return await PostToken(form, ct).ConfigureAwait(false);
    }

    /// <summary>Best-effort revoke at Google's endpoint (used on Disconnect). Never throws.</summary>
    public static async Task RevokeAsync(string token, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }) };
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch { /* offline / already-revoked - the local token is cleared regardless */ }
    }

    // ── PKCE + URL building (pure, unit-tested) ──

    /// <summary>A PKCE code verifier: 32 CSPRNG bytes → base64url = 43 chars from the RFC 7636 set.</summary>
    public static string NewCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge: base64url(SHA-256(ASCII(verifier))), no padding.</summary>
    public static string CodeChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A CSRF <c>state</c> value: 32 CSPRNG bytes, base64url.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Compose the browser authorization URL (response_type=code, S256 PKCE).</summary>
    public static string BuildAuthUrl(string clientId, string redirectUri, string scope, string challenge, string state)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",     // harmless for installed apps; explicit intent to get a refresh token
            ["prompt"] = "consent",          // ensure a refresh token even on a re-auth
        };
        var qs = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthEndpoint}?{qs}";
    }

    /// <summary>Pull the <c>email</c> claim out of an id_token WITHOUT verifying the signature - it
    /// arrived over TLS straight from Google's token endpoint and is used for display only.</summary>
    public static string? DecodeIdTokenEmail(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            return doc.RootElement.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
        }
        catch { return null; }
    }

    // ── loopback listener ──

    static (HttpListener listener, string redirectUri) StartLoopbackListener()
    {
        // HttpListener can't bind port 0, so grab a free ephemeral port from a throwaway TcpListener.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            // ExclusiveAddressUse (RFC 8252 §8.10): stop another local process from also binding this
            // loopback port and racing for the OAuth redirect. Set before Start.
            try { probe.Server.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ExclusiveAddressUse, true); }
            catch { /* best-effort; the PKCE verifier + CSRF state remain the real defenses */ }
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://127.0.0.1:{port}");   // exact string reused in the token exchange
            }
            catch (HttpListenerException) { /* rare race - the port got taken between probe and Start */ }
        }
        throw new GoogleAuthException("Couldn't open a local port for the Google sign-in.", needsReconnect: false);
    }

    static async Task<string> AwaitRedirect(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { throw new GoogleAuthException("Google sign-in was cancelled or timed out.", needsReconnect: false); }
                catch (ObjectDisposedException) { throw new GoogleAuthException("Google sign-in was cancelled.", needsReconnect: false); }

                var q = ParseQuery(ctx.Request.Url?.Query);
                // STATE FIRST: only a request carrying the state WE generated is Google's redirect.
                // Anything else - favicon, a port-scanning browser tab firing ?error=x at loopback
                // ports, a stray local process - is served a neutral page and IGNORED, so a bogus
                // hit can no longer tear down the listener while the real redirect is in flight.
                if (!q.TryGetValue("state", out var gotState) || !FixedEquals(gotState, expectedState))
                {
                    await WritePage(ctx, Page("One moment…", "You can return to Toki.")).ConfigureAwait(false);
                    continue;
                }

                if (q.TryGetValue("error", out var err))
                {
                    await WritePage(ctx, Page("Sign-in cancelled", "Toki wasn't connected. You can close this tab.")).ConfigureAwait(false);
                    throw new GoogleAuthException(err == "access_denied"
                        ? "You declined the Google permission - Toki wasn't connected."
                        : $"Google returned an error ({err}).", needsReconnect: false);
                }

                if (!q.ContainsKey("code"))
                {
                    await WritePage(ctx, Page("One moment…", "You can return to Toki.")).ConfigureAwait(false);
                    continue;
                }

                await WritePage(ctx, Page("Toki is connected ✓", "You can close this tab and return to Toki.")).ConfigureAwait(false);
                return q["code"];
            }
        }
    }

    static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }   // required for URLs on .NET Core+
        catch (Exception ex) { throw new GoogleAuthException($"Couldn't open your browser ({ex.Message}).", needsReconnect: false); }
    }

    // ── token endpoint ──

    static async Task<GoogleTokens> ExchangeAsync(string code, string verifier, string redirectUri, (string Id, string Secret) cred, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = cred.Id,
            ["client_secret"] = cred.Secret,
            ["redirect_uri"] = redirectUri,      // must byte-match the auth request's redirect_uri
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = verifier,
        };
        return await PostToken(form, ct).ConfigureAwait(false);
    }

    static async Task<GoogleTokens> PostToken(Dictionary<string, string> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        string raw;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
            resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            using (resp) raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new GoogleAuthException("Google sign-in timed out.", needsReconnect: false); }
        // HttpClient's OWN timeout (caller's ct not cancelled) must also map to the documented
        // GoogleAuthException contract - a raw TaskCanceledException flew past every caller.
        catch (OperationCanceledException) { throw new GoogleAuthException("Google didn't respond - check your connection.", needsReconnect: false); }
        catch (HttpRequestException) { throw new GoogleAuthException("Couldn't reach Google (network error).", needsReconnect: false); }

        if (!resp.IsSuccessStatusCode)
        {
            var errCode = ErrorCode(raw);
            var needsReconnect = errCode is "invalid_grant";   // token revoked/expired → must re-auth
            throw new GoogleAuthException(FriendlyTokenError(errCode, resp.StatusCode), needsReconnect);
        }
        return ParseTokens(raw);
    }

    static GoogleTokens ParseTokens(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access))
                throw new GoogleAuthException("Google's reply was missing the access token.", needsReconnect: false);
            var expiresIn = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var s) ? s : 3600;
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var idToken = root.TryGetProperty("id_token", out var i) ? i.GetString() : null;
            // 60s early-refresh margin absorbs clock skew - never trust expires_in to the second.
            var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn) - 60);
            return new GoogleTokens(access!, expiresUtc, refresh, DecodeIdTokenEmail(idToken));
        }
        catch (JsonException) { throw new GoogleAuthException("Google sent an unexpected reply.", needsReconnect: false); }
    }

    static string? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch { return null; }
    }

    static string FriendlyTokenError(string? code, HttpStatusCode status) => code switch
    {
        "invalid_grant" => "Your Google sign-in has expired - please connect again.",
        "invalid_client" => "Toki's Google sign-in isn't set up correctly (invalid client).",
        "access_denied" => "You declined the Google permission.",
        _ => $"Google refused the sign-in (HTTP {(int)status}).",
    };

    // ── small helpers ──

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] Base64UrlDecode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(t.PadRight(t.Length + (4 - t.Length % 4) % 4, '='));
    }

    static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    static Dictionary<string, string> ParseQuery(string? query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return d;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return d;
    }

    static async Task WritePage(HttpListenerContext ctx, string html)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }
        catch { /* the browser may have moved on - nothing to do */ }
    }

    static string Page(string title, string sub) =>
        $"<!doctype html><html><head><meta charset=utf-8><title>Toki</title><style>" +
        "html,body{height:100%;margin:0}body{display:flex;align-items:center;justify-content:center;" +
        "font-family:'Segoe UI',system-ui,sans-serif;background:#0d0d0f;color:#f2f2f4}" +
        ".c{text-align:center;padding:40px}h1{font-size:22px;font-weight:600;margin:0 0 10px}" +
        "p{color:#a0a0a8;font-size:14px;margin:0}</style></head><body><div class=c>" +
        $"<h1>{title}</h1><p>{sub}</p></div></body></html>";
}
