using System.Text.Json;

namespace Zeeq.AgentCmd;

/// <summary>Provides generated, reflection-free access to an input contract for runtime experiments.</summary>
/// <typeparam name="TParameters">The authoritative typed command input.</typeparam>
public interface IRuntimeBinding<TParameters>
{
    /// <summary>Gets a stable signature of the input, output and permitted runtime binding shape.</summary>
    string ContractSignature { get; }

    /// <summary>Captures all permitted scalar placeholders using invariant formatting.</summary>
    /// <param name="input">The input to capture.</param>
    /// <returns>A map keyed by case-sensitive CLR property name.</returns>
    IReadOnlyDictionary<string, string?> Freeze(TParameters input);

    /// <summary>Applies a complete batch of authorized overrides without mutating the input.</summary>
    /// <param name="input">The caller input.</param>
    /// <param name="overrides">Values keyed by CLR property name.</param>
    /// <param name="effective">The resulting typed input on success.</param>
    /// <param name="error">A bounded explanation on failure.</param>
    /// <returns>Whether every override was accepted.</returns>
    bool TryApplyOverrides(
        TParameters input,
        IReadOnlyDictionary<string, JsonElement> overrides,
        out TParameters effective,
        out string? error
    );

    /// <summary>Attempts to capture a provably immutable typed input for deferred execution.</summary>
    /// <param name="input">The input to capture.</param>
    /// <param name="snapshot">The immutable input on success.</param>
    /// <returns>Whether the input shape can be safely retained.</returns>
    bool TrySnapshot(TParameters input, out TParameters snapshot);
}
