using System.Text.Json;

namespace Zeeq.AgentCmd;

/// <summary>
/// Closed result of reading a published whole back from the ledger. Evaluation grades the stored
/// JSON, not the in-memory value, so a caller cannot skip the case where the stored version is
/// unavailable or no longer decodes.
/// </summary>
/// <typeparam name="TWhole">Generated whole type.</typeparam>
public closed record StoredWhole<TWhole>
{
    /// <summary>The stored aggregate was read and decoded.</summary>
    /// <param name="Value">Whole decoded from the stored JSON.</param>
    /// <param name="Artifact">Exact stored output, including the reference and JSON to grade.</param>
    public sealed record Found(TWhole Value, OutputArtifact Artifact) : StoredWhole<TWhole>;

    /// <summary>The ledger did not return the stored aggregate, for example because the actor is not authorized.</summary>
    /// <param name="Result">Ledger rejection, with its stable code.</param>
    public sealed record Unavailable(LedgerResult<OutputArtifact> Result) : StoredWhole<TWhole>;

    /// <summary>No whole contract is registered for this type, or the stored JSON does not match it.</summary>
    /// <param name="Reason">Short explanation.</param>
    public sealed record Undecodable(string Reason) : StoredWhole<TWhole>;
}

/// <summary>Reads a published aggregate back through the ledger so evaluation uses the exact stored version.</summary>
public static class PublishedWholeExtensions
{
    /// <summary>
    /// Reads the aggregate's own output reference, not a component execution, and decodes it with the
    /// registered whole contract. The ledger enforces authorization for <paramref name="actor"/>.
    /// </summary>
    /// <param name="published">Published aggregate from the root command.</param>
    /// <param name="ledger">Ledger that holds the stored output.</param>
    /// <param name="registry">Registry holding the generated whole contract.</param>
    /// <param name="actor">Authorized actor reading the output.</param>
    /// <typeparam name="TWhole">Generated whole type.</typeparam>
    public static StoredWhole<TWhole> ReadStored<TWhole>(
        this CompositeOutcome<TWhole>.Published published,
        IHumanFeedbackLedger ledger,
        CompositionRegistry registry,
        Actor actor
    )
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(registry);

        var read = ledger.ReadOutput(published.Aggregate.Output, actor);
        if (read is not LedgerResult<OutputArtifact>.Accepted accepted)
        {
            return new StoredWhole<TWhole>.Unavailable(read);
        }

        var descriptor = registry.Find<TWhole>();
        if (descriptor is null)
        {
            return new StoredWhole<TWhole>.Undecodable("The whole contract is not registered.");
        }

        try
        {
            var whole = accepted.Value.Value.Deserialize(descriptor.Whole);

            return whole is null
                ? new StoredWhole<TWhole>.Undecodable("The stored whole decoded to null.")
                : new StoredWhole<TWhole>.Found(whole, accepted.Value);
        }
        catch (JsonException)
        {
            return new StoredWhole<TWhole>.Undecodable(
                "The stored JSON does not match the whole contract."
            );
        }
    }
}
