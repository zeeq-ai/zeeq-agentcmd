using System.Text.Json;

namespace Zeeq.AgentCmd;

/// <summary>
/// Validated identifier of an experiment revision. Revisions are opaque to AgentCmd; the source
/// assigns them, and an immutable revision never changes meaning once published.
/// </summary>
/// <remarks>
/// The grammar is shared with other experiment identifiers so cache keys and diagnostics stay bounded.
/// </remarks>
public sealed record ExperimentRevision
{
    /// <summary>Validated revision text.</summary>
    public string Value
    {
        get;
    }

    private ExperimentRevision(string value) => Value = value;

    /// <summary>Validates a revision identifier without throwing.</summary>
    /// <param name="value">One to 128 ASCII letters, digits, periods, underscores, hyphens or colons.</param>
    /// <param name="revision">Validated revision on success; otherwise null.</param>
    /// <returns>True when the value satisfies the identifier rules.</returns>
    public static bool TryCreate(string? value, out ExperimentRevision? revision)
    {
        revision = null;
        if (!ExperimentIdentifier.IsValid(value))
        {
            return false;
        }

        revision = new(value!);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>Shared identifier grammar for experiment partitions, commands, revisions, and variants.</summary>
/// <remarks>
/// Partitions appear in cache keys. Excluding separators keeps composite keys unambiguous without escaping.
/// </remarks>
public static class ExperimentIdentifier
{
    /// <summary>Maximum identifier length accepted by the experiment contracts.</summary>
    public const int MaxLength = 128;

    /// <summary>Checks an identifier against the shared grammar.</summary>
    /// <param name="value">Candidate identifier.</param>
    /// <returns>True when the value is non-empty, bounded, and uses only allowed characters.</returns>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
}

/// <summary>Authorized request for one experiment snapshot of a partition.</summary>
/// <remarks>
/// <see cref="Partition"/> is the authorized tenant or partition chosen by the host, not caller input.
/// The snapshot covers every command in the partition, so one lookup yields one atomic publication.
/// Correlation identifiers never belong here because they do not grant access.
/// </remarks>
public sealed record ExperimentLookup
{
    /// <summary>Authorized partition, such as a tenant identifier.</summary>
    public string Partition
    {
        get;
    }

    /// <summary>Immutable revision to read, or null to read the latest published snapshot.</summary>
    public ExperimentRevision? RequestedRevision
    {
        get;
    }

    /// <summary>Longest partition accepted. Partitions are hashed before they reach any cache key.</summary>
    public const int MaxPartitionLength = 512;

    private ExperimentLookup(string partition, ExperimentRevision? requestedRevision)
    {
        Partition = partition;
        RequestedRevision = requestedRevision;
    }

    /// <summary>Validates a lookup without throwing.</summary>
    /// <param name="partition">Authorized partition chosen by the host.</param>
    /// <param name="requestedRevision">Optional immutable revision to read.</param>
    /// <param name="lookup">Validated lookup on success; otherwise null.</param>
    /// <returns>True when the partition is nonblank and bounded.</returns>
    /// <remarks>
    /// Tenant identifiers are opaque to AgentCmd, so the partition grammar accepts any nonblank text.
    /// Imposing a new identifier grammar here would silently reject tenants that the host already
    /// authorizes. The cache hashes the partition, so punctuation cannot break key boundaries.
    /// </remarks>
    public static bool TryCreate(string? partition, ExperimentRevision? requestedRevision, out ExperimentLookup? lookup)
    {
        lookup = null;
        if (string.IsNullOrWhiteSpace(partition) || partition.Length > MaxPartitionLength)
        {
            return false;
        }

        lookup = new(partition, requestedRevision);
        return true;
    }
}

/// <summary>Sanitized reason a source could not provide a usable snapshot.</summary>
/// <remarks>
/// Reasons are closed so logs and evidence never carry provider text, secrets, or source payloads.
/// </remarks>
public enum SourceFailure
{
    /// <summary>The source did not answer before its configured deadline.</summary>
    Timeout,

    /// <summary>The source could not be reached or failed.</summary>
    Unavailable,

    /// <summary>The source returned only part of a multi-command snapshot.</summary>
    IncompleteView,

    /// <summary>The source returned data that fails the snapshot contract.</summary>
    InvalidRelease,

    /// <summary>The source service refused its own access, such as an expired credential.</summary>
    /// <remarks>
    /// This is a source-service failure and selects the configured default. It never grants or
    /// replaces actor or run authorization, which the host checks before a lookup.
    /// </remarks>
    AuthorizationDenied,
}

/// <summary>Closed result of one source lookup.</summary>
/// <typeparam name="TRelease">Immutable value returned by the source.</typeparam>
/// <remarks>
/// <see cref="NoOverride"/> is an explicit answer and selects the configured default.
/// It differs from <see cref="Unavailable"/>, where the source could not answer at all.
/// </remarks>
public closed record SourceResult<TRelease>
{
    /// <summary>The source returned an immutable value for the lookup.</summary>
    /// <param name="Release">Value, validated again by the resolver before use.</param>
    public sealed record Found(TRelease Release) : SourceResult<TRelease>;

    /// <summary>The source explicitly has no override for this partition.</summary>
    public sealed record NoOverride : SourceResult<TRelease>;

    /// <summary>The source could not answer for a sanitized reason.</summary>
    /// <param name="Failure">Closed failure category.</param>
    public sealed record Unavailable(SourceFailure Failure) : SourceResult<TRelease>;

    /// <summary>The source answered with a value that failed its own validation.</summary>
    /// <param name="Failure">Closed failure category.</param>
    public sealed record Invalid(SourceFailure Failure) : SourceResult<TRelease>;
}

/// <summary>
/// Host-implemented port that supplies one atomic, versioned snapshot of experiment definitions.
/// </summary>
/// <remarks>
/// A snapshot joins prompt templates, parameters, routes, and weights for every command in a partition.
/// Hosts that store prompts and routes separately must join them behind this port, and only when both
/// name the same immutable revision. Two independent reads can tear and are not a valid source.
/// Implementations must be singleton-safe. Prompt-management systems and their SDKs stay outside core.
/// </remarks>
public interface IExperimentSnapshotSource
{
    /// <summary>Reads one atomic snapshot for an authorized partition.</summary>
    /// <param name="lookup">Validated authorized lookup.</param>
    /// <param name="cancellationToken">Caller cancellation. Cancellation must not become a default model call.</param>
    /// <returns>A closed source result.</returns>
    ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
        ExperimentLookup lookup,
        CancellationToken cancellationToken
    );
}

/// <summary>How one prompt template slot changes relative to the compiled command.</summary>
/// <remarks>
/// The three cases are distinct because an omitted slot, an explicit removal, and replacement text
/// mean different things. A source that maps JSON null to <see cref="Removed"/> must do so on purpose.
/// </remarks>
public closed record TemplateChange
{
    /// <summary>The slot keeps the compiled template.</summary>
    public sealed record Inherited : TemplateChange;

    /// <summary>The slot is explicitly removed. Valid only for the system template.</summary>
    public sealed record Removed : TemplateChange;

    /// <summary>The slot is replaced by nonempty text. A user template must contain non-whitespace text.</summary>
    /// <param name="Text">Replacement template text.</param>
    public sealed record Replaced(string Text) : TemplateChange;
}

/// <summary>Prompt template changes for one variant.</summary>
/// <param name="System">Change to the system template.</param>
/// <param name="User">
/// Change to the user template. Removal and whitespace-only replacement are rejected because a command needs a user message.
/// </param>
public sealed record PromptTemplateOverride(TemplateChange System, TemplateChange User);
