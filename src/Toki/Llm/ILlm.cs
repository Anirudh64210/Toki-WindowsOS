namespace Toki.Llm;

/// <summary>A text-in / text-out language model (local Qwen, or a BYO API key).</summary>
public interface ILlm
{
    bool Ready { get; }

    /// <summary>Single-turn instruction → completion. Should already apply the chat template + sampler params.</summary>
    Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default);

    /// <summary>Same as <see cref="CompleteAsync"/>, but yields the answer token-by-token as it's
    /// generated — so the UI can show words appearing live (huge perceived-speed win vs. waiting for
    /// the whole thing). Markers like the chat-template end tag are stripped from the yielded pieces.</summary>
    IAsyncEnumerable<string> StreamAsync(string system, string user, int maxTokens, CancellationToken ct = default);
}
