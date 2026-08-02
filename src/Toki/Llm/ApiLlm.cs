using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.Llm;

/// <summary>
/// Optional "bring your own API key" model — provider-agnostic. It speaks the common
/// chat-completions wire format that virtually every provider and router implements, so
/// the user can point it at ANY model they like: OpenAI, Anthropic/Claude &amp; Google/Gemini
/// (directly via their compatible endpoints, or through a router like OpenRouter with one
/// key), Groq, Together, a local model, and so on. The user pays their own provider; Toki
/// never touches that cost (hard constraint #1).
///
/// Security posture: HTTPS over the network (the key never travels in the clear; plain http
/// only for loopback), the key goes on each request — never on the shared client, never in a
/// log or exception message — and every failure surfaces as a short human-readable message.
/// </summary>
public sealed class ApiLlm : ILlm
{
    // ONE client for the process (per-call clients exhaust sockets). No default auth
    // header — the key can change between calls, so it rides on each request instead.
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    readonly string _apiKey;
    readonly string _model;
    readonly Uri? _endpoint;   // …/v1/chat/completions, https only, or null if the base URL is unusable

    public ApiLlm(string? apiKey, string? model, string? baseUrl)
    {
        _apiKey = (apiKey ?? "").Trim();
        _model = (model ?? "").Trim();
        _endpoint = TryBuildEndpoint(baseUrl);
    }

    public bool Ready => _apiKey.Length > 0 && _model.Length > 0 && _endpoint is not null;

    /// <summary>
    /// Turn whatever the user typed into the chat-completions endpoint, or null if it
    /// isn't usable. Handles trailing slashes and an already-present <c>/v1</c> (including
    /// OpenRouter's <c>/api/v1</c>) without ever doubling it: the final path always ends in
    /// <c>/v1/chat/completions</c> exactly once.
    /// Scheme rule: <b>https required over the network</b> (an API key is a credential and must
    /// never travel in the clear) — but plain <c>http</c> is allowed for a <b>loopback</b> address
    /// (localhost / 127.0.0.1 / [::1]), where the traffic never leaves this machine, so a local
    /// proxy like Ollama or LM Studio works.
    /// </summary>
    public static Uri? TryBuildEndpoint(string? baseUrl) => TryBuildV1(baseUrl, "chat/completions");

    /// <summary>The <c>…/v1/models</c> URL for the same base — used to fetch a provider's
    /// live model list. Same scheme rules as <see cref="TryBuildEndpoint"/>.</summary>
    public static Uri? TryBuildModelsEndpoint(string? baseUrl) => TryBuildV1(baseUrl, "models");

    static Uri? TryBuildV1(string? baseUrl, string suffix)
    {
        var raw = (baseUrl ?? "").Trim();
        if (raw.Length == 0) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Length == 0) return null;
        var httpsOk = uri.Scheme == Uri.UriSchemeHttps;
        var loopbackHttpOk = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!httpsOk && !loopbackHttpOk) return null;             // https anywhere, http only on loopback
        var path = uri.AbsolutePath.TrimEnd('/');                 // "" | "/v1" | "/api/v1" | …
        if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";
        return new Uri($"{uri.Scheme}://{uri.Authority}{path}/{suffix}");
    }

    /// <summary>
    /// Fetch the provider's available model IDs from <c>…/v1/models</c> so the picker can
    /// show a live, always-current list. Never throws — returns (false, human message, empty)
    /// on any failure. Doubles as a key check: a successful list means the key + address work.
    /// Anthropic's list endpoint wants <c>x-api-key</c> + <c>anthropic-version</c>; every other
    /// OpenAI-compatible provider takes a Bearer token.
    /// </summary>
    public static async Task<(bool ok, string message, IReadOnlyList<string> models)> ListModelsAsync(
        string? apiKey, string? baseUrl, CancellationToken ct = default)
    {
        var key = (apiKey ?? "").Trim();
        var endpoint = TryBuildModelsEndpoint(baseUrl);
        if (endpoint is null) return (false, "The address must be https:// (or http:// for a local proxy).", Array.Empty<string>());
        if (key.Length == 0) return (false, "Enter your API key first.", Array.Empty<string>());
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (string.Equals(endpoint.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.TryAddWithoutValidation("x-api-key", key);   // Anthropic's native models endpoint
                req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
            else
            {
                req.Headers.Authorization = new("Bearer", key);          // per-request, never on the shared client
            }
            using var resp = await Http.SendAsync(req, timeout.Token).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, StatusReason(resp.StatusCode), Array.Empty<string>());
            var models = ParseModelIds(raw);
            return models.Count > 0
                ? (true, "", models)
                : (false, "The provider returned no models.", Array.Empty<string>());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (false, "Cancelled.", Array.Empty<string>()); }
        catch (OperationCanceledException) { return (false, "Couldn't reach the provider (timed out).", Array.Empty<string>()); }
        catch (HttpRequestException) { return (false, "Couldn't reach the provider (network error).", Array.Empty<string>()); }
    }

    /// <summary>Pull the model IDs out of an OpenAI-style <c>{"data":[{"id":…}]}</c> body
    /// (or a bare array), de-duplicated and sorted. Never throws on odd shapes.</summary>
    static List<string> ParseModelIds(string raw)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                arr = d;
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arr = doc.RootElement;
            else return ids;
            foreach (var m in arr.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s)
                    ids.Add(s);
        }
        catch (JsonException) { /* HTML/garbage body → empty */ }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Short, safe status message per class — no body, no key.</summary>
    static string StatusReason(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => "Your API key was rejected (check the key).",
        404 => "No model list at that address (check the provider).",
        429 => "Rate limited or out of credit.",
        >= 500 => "The provider had a server error.",
        _ => $"The provider refused the request (HTTP {(int)status}).",
    };

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
    {
        if (_endpoint is null) throw new ArgumentException("The API base URL must be https:// (http:// only for a local proxy).");
        if (_apiKey.Length == 0) throw new InvalidOperationException("No API key is set.");
        if (_model.Length == 0) throw new InvalidOperationException("No API model is set.");

        var body = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = 0.7,
            max_tokens = maxTokens,
            stream = false,
        };

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
            req.Headers.Authorization = new("Bearer", _apiKey);   // per-request, never on the shared client
            resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new HttpRequestException("Couldn't reach the provider (timed out)."); }
        catch (HttpRequestException) { throw new HttpRequestException("Couldn't reach the provider (network error)."); }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException(ErrorFor(resp.StatusCode, raw));
            return ExtractContent(raw);
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_endpoint is null) throw new ArgumentException("The API base URL must be https:// (http:// only for a local proxy).");
        if (_apiKey.Length == 0) throw new InvalidOperationException("No API key is set.");
        if (_model.Length == 0) throw new InvalidOperationException("No API model is set.");

        var body = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = 0.7,
            max_tokens = maxTokens,
            stream = true,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new("Bearer", _apiKey);
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new HttpRequestException("Couldn't reach the provider (timed out)."); }
        catch (HttpRequestException) { throw new HttpRequestException("Couldn't reach the provider (network error)."); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(ErrorFor(resp.StatusCode, err));
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new System.IO.StreamReader(stream);
            // OpenAI-style Server-Sent Events: lines of "data: {json}", terminated by "data: [DONE]".
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload == "[DONE]") break;
                string? piece = null;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var cnt) && cnt.ValueKind == JsonValueKind.String)
                        piece = cnt.GetString();
                }
                catch { /* keep-alive / non-JSON line — skip */ }
                if (!string.IsNullOrEmpty(piece)) yield return piece;
            }
        }
    }

    /// <summary>
    /// Tiny 1-token round trip for the Settings "Test connection" button. Never throws
    /// and never hangs — a short timeout and a (success, human message) result instead.
    /// </summary>
    public async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        if (_endpoint is null) return (false, "The base URL must be https:// (or http:// for a local proxy).");
        if (_apiKey.Length == 0) return (false, "Enter an API key first.");
        if (_model.Length == 0) return (false, "Enter a model name first.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await CompleteAsync("You are a connection test.", "Reply with OK.", 1, timeout.Token).ConfigureAwait(false);
            return (true, "Connected — your key works.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (false, "Test cancelled."); }
        catch (OperationCanceledException) { return (false, "Couldn't reach the provider (timed out)."); }
        catch (Exception ex) { return (false, ex.Message); }      // messages built here are key-free by design
    }

    // ── response handling (defensive: providers return error shapes, HTML, nothing…) ──

    /// <summary>choices[0].message.content, or a clear error — never a NullReference/KeyNotFound.</summary>
    string ExtractContent(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
                if (content.ValueKind == JsonValueKind.Null) return "";   // some providers null out a 0/1-token reply
            }
        }
        catch (JsonException) { /* fall through to the clear error below */ }
        throw new HttpRequestException("The provider sent an unexpected response (no message content)."
            + ProviderDetail(raw));
    }

    /// <summary>A short human message per status class. Safe to show/log — never contains the key.</summary>
    string ErrorFor(HttpStatusCode status, string body)
    {
        var reason = (int)status switch
        {
            401 or 403 => "API key was rejected (check the key).",
            404 => "The endpoint was not found (check the base URL).",
            429 => "Rate limited or out of credit.",
            >= 500 => "The provider had a server error.",
            _ => $"The provider refused the request (HTTP {(int)status}).",
        };
        return reason + ProviderDetail(body);
    }

    /// <summary>The provider's own error.message when the body carries one — trimmed,
    /// capped, and scrubbed so a quoted-back key can never leak into a log.</summary>
    string ProviderDetail(string body)
    {
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object
                    && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    detail = m.GetString();
                else if (err.ValueKind == JsonValueKind.String)
                    detail = err.GetString();
            }
        }
        catch (JsonException) { /* HTML or garbage body → no detail */ }

        if (string.IsNullOrWhiteSpace(detail)) return "";
        detail = detail.Trim();
        if (_apiKey.Length > 0) detail = detail.Replace(_apiKey, "•••");   // belt and braces
        if (detail.Length > 200) detail = detail[..200] + "…";
        return $" ({detail})";
    }
}
