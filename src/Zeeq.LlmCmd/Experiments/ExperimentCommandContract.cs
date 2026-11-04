namespace Zeeq.LlmCmd;

/// <summary>
/// Identifies the compiled command contract that an external experiment may target.
/// A control plane can retain this value with a published experiment to detect a stale contract.
/// </summary>
/// <param name="CommandId">Stable command identity.</param>
/// <param name="CommandRevision">Compiled command revision.</param>
/// <param name="ContractSignature">Generated fingerprint of the compatible input and output shapes.</param>
public sealed record ExperimentCommandContract(
    string CommandId,
    int CommandRevision,
    string ContractSignature
);
