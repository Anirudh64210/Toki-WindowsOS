namespace Toki.Llm;

/// <summary>A text-in / text-out language model (local Qwen, or a BYO API key).</summary>
public interface ILlm
{
    bool Ready { get; }

    /// <summary>Single-turn instruction → completion. Should already apply the chat template + sampler params.</summary>
    Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken ct = default);
}
