using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace Toki.Llm;

/// <summary>
/// Optional "bring your own API key" model — any OpenAI-compatible endpoint. The
/// user pays their own provider; Toki never touches that cost (hard constraint #1).
/// </summary>
public sealed class OpenAiLlm(string apiKey, string model, string baseUrl) : ILlm
{
    public bool Ready => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);

        var request = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            temperature = 0.7,
            max_tokens = maxTokens,
            stream = false,
        };

        using var resp = await http.PostAsJsonAsync("v1/chat/completions", request, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
        return doc!.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
