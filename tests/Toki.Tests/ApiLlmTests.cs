using Toki.Llm;

namespace Toki.Tests;

public class ApiLlmTests
{
    // The five documented base-URL shapes all land on /v1/chat/completions exactly once.
    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api/v1", "https://openrouter.ai/api/v1/chat/completions")]
    [InlineData("  https://api.groq.com/openai/v1  ", "https://api.groq.com/openai/v1/chat/completions")]  // pasted whitespace
    [InlineData("https://localhost:8080", "https://localhost:8080/v1/chat/completions")]                    // local https proxy
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]                    // Ollama - loopback http is safe (never leaves the PC)
    [InlineData("http://127.0.0.1:1234/v1", "http://127.0.0.1:1234/v1/chat/completions")]                   // LM Studio
    public void EndpointJoinsV1ExactlyOnce(string baseUrl, string expected)
        => Assert.Equal(expected, ApiLlm.TryBuildEndpoint(baseUrl)!.ToString());

    // The key is a credential: over the network it MUST be https. Plain http is allowed only on
    // loopback (tested above); a remote http URL - where the key would travel in the clear - is rejected.
    [Theory]
    [InlineData("http://api.openai.com")]         // remote plaintext - never
    [InlineData("ftp://api.openai.com")]
    [InlineData("api.openai.com")]                // no scheme
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NonHttpsBaseUrlsAreRejected(string? baseUrl)
        => Assert.Null(ApiLlm.TryBuildEndpoint(baseUrl));

    [Theory]
    [InlineData("sk-x", "gpt-4o-mini", "https://api.openai.com", true)]
    [InlineData("", "gpt-4o-mini", "https://api.openai.com", false)]      // no key
    [InlineData("sk-x", "", "https://api.openai.com", false)]             // no model
    [InlineData("sk-x", "gpt-4o-mini", "http://api.openai.com", false)]   // http
    [InlineData("sk-x", "gpt-4o-mini", "", false)]                        // no URL
    public void ReadyRequiresKeyModelAndHttpsUrl(string key, string model, string url, bool ready)
        => Assert.Equal(ready, new ApiLlm(key, model, url).Ready);

    [Fact]
    public async Task HttpBaseUrlThrowsClearErrorWithoutTheKey()
    {
        var llm = new ApiLlm("sk-supersecret", "gpt-4o-mini", "http://api.openai.com");
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => llm.CompleteAsync("s", "u", 8));
        Assert.Contains("https", ex.Message);
        Assert.DoesNotContain("supersecret", ex.Message);
    }

    // The models endpoint mirrors the chat endpoint: same /v1 join rules, ending in /models.
    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/models")]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/models")]
    [InlineData("https://api.groq.com/openai/v1", "https://api.groq.com/openai/v1/models")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/models")]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/models")]
    public void ModelsEndpointJoinsV1ExactlyOnce(string baseUrl, string expected)
        => Assert.Equal(expected, ApiLlm.TryBuildModelsEndpoint(baseUrl)!.ToString());

    [Theory]
    [InlineData("http://api.openai.com")]   // remote plaintext - never
    [InlineData("api.openai.com")]           // no scheme
    [InlineData("")]
    [InlineData(null)]
    public void ModelsEndpointRejectsNonHttps(string? baseUrl)
        => Assert.Null(ApiLlm.TryBuildModelsEndpoint(baseUrl));

    [Fact]
    public async Task ListModelsReportsBadConfigWithoutThrowingOrNetwork()
    {
        // Misconfigured → fast human message, no exception, no request.
        var (ok, msg, models) = await ApiLlm.ListModelsAsync("sk-x", "http://api.openai.com");
        Assert.False(ok);
        Assert.Empty(models);
        Assert.Contains("https", msg);

        (ok, msg, models) = await ApiLlm.ListModelsAsync("", "https://api.openai.com");
        Assert.False(ok);
        Assert.Contains("key", msg);
    }

    [Fact]
    public async Task TestAsyncReportsBadConfigWithoutThrowingOrNetwork()
    {
        // Misconfigured clients fail fast with a human message - no exception, no request.
        var (ok, msg) = await new ApiLlm("sk-x", "gpt-4o-mini", "http://api.openai.com").TestAsync();
        Assert.False(ok);
        Assert.Contains("https", msg);

        (ok, msg) = await new ApiLlm("", "gpt-4o-mini", "https://api.openai.com").TestAsync();
        Assert.False(ok);
        Assert.Contains("key", msg);
    }
}
