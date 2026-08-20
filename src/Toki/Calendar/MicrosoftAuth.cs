using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Toki.Calendar;

/// <summary>
/// Microsoft identity sign-in for Outlook calendars: the same loopback + PKCE flow as
/// <see cref="GoogleAuth"/>. One real difference: a "public client" has NO client secret (Microsoft
/// rejects one; PKCE is the whole proof). The "common" tenant is deliberate - it accepts both
/// work/school and personal accounts. Tokens are never logged.
/// </summary>
public static class MicrosoftAuth
{
    const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    static string AuthEndpoint => $"{Authority}/authorize";
    static string TokenEndpoint => $"{Authority}/token";

    /// <summary>
    /// <c>offline_access</c> is what makes a refresh token come back at all - without it the access
    /// token dies in an hour and the connection silently stops working. Calendars.ReadWrite because
    /// Toki creates and edits events; read-only now would force a second consent later.
    /// </summary>
    public const string Scopes = "offline_access openid email profile https://graph.microsoft.com/Calendars.ReadWrite";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // ── the app registration ──
    // Resolved in the same order as the Google one, so setting Toki up is one habit rather than two:
    //   1. env var TOKI_MS_CLIENT_ID (dev),
    //   2. %APPDATA%\Toki\microsoft_client.json  ->  { "client_id": "..." },
    //   3. the embedded const below (filled at ship time).
    const string EmbeddedClientId = "";

    static string? _clientId;
    static bool _resolved;

    /// <summary>True once a client id is available from any source. The Connect button checks this and
    /// explains that Outlook sign-in is not set up, rather than opening a broken browser tab.</summary>
    public static bool IsConfigured => ClientId() is not null;

    static string? ClientId()
    {
        if (_resolved) return _clientId;
        _resolved = true;
        _clientId = ResolveClientId();
        return _clientId;
    }

    static string? ResolveClientId()
    {
        var env = Environment.GetEnvironmentVariable("TOKI_MS_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Toki", "microsoft_client.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                foreach (var key in new[] { "client_id", "clientId", "appId" })
                    if (root.TryGetProperty(key, out var v) && v.GetString() is { Length: > 0 } id)
                        return id.Trim();
            }
        }
        catch { /* malformed file -> fall through to embedded */ }

        return EmbeddedClientId.Length > 0 ? EmbeddedClientId : null;
    }

    // ── the interactive sign-in ──

    /// <summary>Open the browser, catch the loopback redirect, exchange the code. Throws
    /// <see cref="GoogleAuthException"/> - the shared calendar auth error - on every failure path.</summary>
    public static async Task<GoogleTokens> ConnectAsync(CancellationToken ct = default)
    {
        var clientId = ClientId()
            ?? throw new GoogleAuthException("Outlook isn't set up yet (missing app registration).", needsReconnect: false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        var verifier = GoogleAuth.NewCodeVerifier();
        var challenge = GoogleAuth.CodeChallenge(verifier);
        var state = GoogleAuth.NewState();

        var (listener, redirectUri) = StartLoopbackListener();
        try
        {
            OpenBrowser(BuildAuthUrl(clientId, redirectUri, challenge, state));
            var code = await AwaitRedirect(listener, state, timeout.Token).ConfigureAwait(false);

            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,     // must byte-match the authorize request
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
                ["scope"] = Scopes,
            };
            return await PostToken(form, timeout.Token).ConfigureAwait(false);
        }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>Trade the stored refresh token for a fresh access token. Microsoft ROTATES refresh
    /// tokens - every refresh returns a new one and invalidates the old - so the caller must store what
    /// comes back or the connection dies at the next restart.</summary>
    public static async Task<GoogleTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var clientId = ClientId()
            ?? throw new GoogleAuthException("Outlook isn't set up yet (missing app registration).", needsReconnect: false);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scopes,
        };
        return await PostToken(form, ct).ConfigureAwait(false);
    }

    /// <summary>Microsoft has no revoke endpoint for a public client - a user withdraws access from
    /// their own account page. Disconnecting therefore wipes the local grant and says so honestly;
    /// this exists so the two providers present the same shape to <see cref="CalendarService"/>.</summary>
    public static Task RevokeAsync(string token, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>The browser authorization URL.</summary>
    public static string BuildAuthUrl(string clientId, string redirectUri, string challenge, string state)
    {
        var q = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["prompt"] = "select_account",   // so a second account can be added without signing out first
        };
        var qs = string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"{AuthEndpoint}?{qs}";
    }

    // ── loopback listener (same shape as GoogleAuth; see the notes there) ──

    static (HttpListener listener, string redirectUri) StartLoopbackListener()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            try { probe.Server.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ExclusiveAddressUse, true); }
            catch { }
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, $"http://localhost:{port}");
            }
            catch (HttpListenerException) { }
        }
        throw new GoogleAuthException("Couldn't open a local port for the Outlook sign-in.", needsReconnect: false);
    }

    static async Task<string> AwaitRedirect(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using (ct.Register(() => { try { listener.Stop(); } catch { } }))
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { throw new GoogleAuthException("Outlook sign-in was cancelled or timed out.", needsReconnect: false); }
                catch (ObjectDisposedException) { throw new GoogleAuthException("Outlook sign-in was cancelled.", needsReconnect: false); }

                var q = ParseQuery(ctx.Request.Url?.Query);
                // State first: anything not carrying the state we generated is not our redirect, and
                // must not be allowed to tear the listener down while the real one is in flight.
                if (!q.TryGetValue("state", out var got) || !FixedEquals(got, expectedState))
                {
                    await WritePage(ctx, Page("One moment…", "You can return to Toki.")).ConfigureAwait(false);
                    continue;
                }
                if (q.TryGetValue("error", out var err))
                {
                    await WritePage(ctx, Page("Sign-in cancelled", "Toki wasn't connected. You can close this tab.")).ConfigureAwait(false);
                    throw new GoogleAuthException(err == "access_denied"
                        ? "You declined the Outlook permission - Toki wasn't connected."
                        : $"Microsoft returned an error ({err}).", needsReconnect: false);
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
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { throw new GoogleAuthException($"Couldn't open your browser ({ex.Message}).", needsReconnect: false); }
    }

    // ── token endpoint ──

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new GoogleAuthException("Outlook sign-in timed out.", needsReconnect: false); }
        catch (OperationCanceledException) { throw new GoogleAuthException("Microsoft didn't respond - check your connection.", needsReconnect: false); }
        catch (HttpRequestException) { throw new GoogleAuthException("Couldn't reach Microsoft (network error).", needsReconnect: false); }

        if (!resp.IsSuccessStatusCode)
        {
            var code = ErrorCode(raw);
            // invalid_grant covers a revoked consent, a changed password and an expired token - all of
            // which mean the same thing to a person: sign in again.
            var needsReconnect = code is "invalid_grant" or "interaction_required";
            throw new GoogleAuthException(Friendly(code, resp.StatusCode), needsReconnect);
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
                throw new GoogleAuthException("Microsoft's reply was missing the access token.", needsReconnect: false);
            var expiresIn = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var s) ? s : 3600;
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var idToken = root.TryGetProperty("id_token", out var i) ? i.GetString() : null;
            var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn) - 60);
            return new GoogleTokens(access!, expiresUtc, refresh, Email(idToken));
        }
        catch (JsonException) { throw new GoogleAuthException("Microsoft sent an unexpected reply.", needsReconnect: false); }
    }

    /// <summary>The address to show as "Connected as ...". Microsoft puts it in <c>email</c> for a
    /// personal account and in <c>preferred_username</c> for a work one, so both are read. Signature
    /// unverified on purpose: it arrived over TLS from the token endpoint and is display only.</summary>
    static string? Email(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken)) return null;
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            foreach (var claim in new[] { "email", "preferred_username", "upn" })
                if (doc.RootElement.TryGetProperty(claim, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }
        catch { return null; }
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

    static string Friendly(string? code, HttpStatusCode status) => code switch
    {
        "invalid_grant" or "interaction_required" => "Your Outlook sign-in has expired - please connect again.",
        "invalid_client" or "unauthorized_client" => "Toki's Outlook sign-in isn't set up correctly.",
        "access_denied" => "You declined the Outlook permission.",
        _ => $"Microsoft refused the sign-in (HTTP {(int)status}).",
    };

    // ── small helpers ──

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
        catch { }
    }

    static string Page(string title, string sub) =>
        $"<!doctype html><html><head><meta charset=utf-8><title>Toki</title><style>" +
        "html,body{height:100%;margin:0}body{display:flex;align-items:center;justify-content:center;" +
        "font-family:'Segoe UI',system-ui,sans-serif;background:#0d0d0f;color:#f2f2f4}" +
        ".c{text-align:center;padding:40px}h1{font-size:22px;font-weight:600;margin:0 0 10px}" +
        "p{color:#a0a0a8;font-size:14px;margin:0}</style></head><body><div class=c>" +
        $"<h1>{title}</h1><p>{sub}</p></div></body></html>";
}
