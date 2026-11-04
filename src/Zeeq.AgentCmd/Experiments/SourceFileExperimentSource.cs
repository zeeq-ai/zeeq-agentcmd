using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Zeeq.AgentCmd;

/// <summary>
/// Reads experiment snapshots from one JSON document that holds every partition. It is the reference external source:
/// edit the file and the change reaches callers after the cache lifetime, with no recompile.
/// </summary>
/// <remarks>
/// <para>
/// The file is read on every call to <see cref="GetAsync"/>. Wrap the source in <see cref="CachedExperimentSnapshotSource"/>
/// so requests do not read the file each time. The cache lifetime bounds how long an edit takes to appear.
/// </para>
/// <para>
/// A file serves only its current publication. A pinned lookup for any other revision is rejected by validation rather
/// than answered from the file, so a pin never picks up a later edit. Keep history in a host store when pinned
/// revisions must stay available.
/// </para>
/// <para>
/// Parse and shape errors are an invalid release. A missing or locked file during an update is unavailable and is
/// never cached. The file text never reaches the result, so an error cannot leak document content.
/// </para>
/// </remarks>
public sealed class FileExperimentSnapshotSource : IExperimentSnapshotSource
{
    /// <summary>
    /// Largest document accepted. The whole file is read and checked before any value is built, so the check
    /// cannot be bypassed by a document that is only partly read.
    /// </summary>
    const int MaxDocumentBytes = 4 * 1024 * 1024;

    readonly string _path;

    /// <summary>Creates a source that reads the given experiment document.</summary>
    /// <param name="path">Path to the document. A relative path resolves against the process working directory.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.</exception>
    public FileExperimentSnapshotSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A document path is required.", nameof(path));
        }

        _path = path;
    }

    /// <inheritdoc/>
    public async ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
        ExperimentLookup lookup,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lookup);

        ExperimentFileDocument? document;
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            if (stream.Length > MaxDocumentBytes)
            {
                return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
            }

            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

            // System.Text.Json keeps the last of two equal names. A repeated key would silently change the publication,
            // so the bytes are checked for duplicate names at every depth before any value is built.
            if (!ExperimentFileDuplicateGuard.IsStrictJson(bytes))
            {
                return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
            }

            document = JsonSerializer.Deserialize(
                bytes,
                ExperimentFileJsonContext.Default.ExperimentFileDocument
            );
        }
        catch (JsonException)
        {
            return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
        }
        catch (IOException)
        {
            return new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable);
        }
        catch (UnauthorizedAccessException)
        {
            return new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable);
        }

        return ExperimentFileMapper.Map(document, lookup);
    }
}

/// <summary>
/// Maps a parsed experiment document to a source answer. Every shape problem becomes an invalid release, so no partial
/// publication is ever returned.
/// </summary>
internal static class ExperimentFileMapper
{
    const int SupportedFormat = 1;

    /// <summary>Maps the document for one partition. A partition the file does not name is an explicit no-override.</summary>
    /// <param name="document">Parsed document, or null for an empty file.</param>
    /// <param name="lookup">Lookup whose partition selects the publication.</param>
    /// <returns>A found snapshot, no override, or an invalid release.</returns>
    public static SourceResult<ExperimentSnapshot> Map(
        ExperimentFileDocument? document,
        ExperimentLookup lookup
    )
    {
        if (document is null || document.Format != SupportedFormat || document.Partitions is null)
        {
            return Rejected();
        }

        if (!document.Partitions.TryGetValue(lookup.Partition, out var partition))
        {
            return new SourceResult<ExperimentSnapshot>.NoOverride();
        }

        if (partition is null || partition.Revision is null || partition.Commands is null)
        {
            return Rejected();
        }

        if (!ExperimentRevision.TryCreate(partition.Revision, out var revision) || revision is null)
        {
            return Rejected();
        }

        var commands = new Dictionary<string, ExperimentCatalogView>(StringComparer.Ordinal);
        foreach (var (commandId, command) in partition.Commands)
        {
            if (!TryMapCommand(commandId, command, revision, out var view))
            {
                return Rejected();
            }

            commands.Add(commandId, view);
        }

        return new SourceResult<ExperimentSnapshot>.Found(
            new ExperimentSnapshot(revision, commands)
        );
    }

    static SourceResult<ExperimentSnapshot> Rejected() =>
        new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);

    static bool TryMapCommand(
        string commandId,
        ExperimentFileCommand? command,
        ExperimentRevision revision,
        [NotNullWhen(true)] out ExperimentCatalogView? view
    )
    {
        view = null;
        if (command is null || command.Variants is null)
        {
            return false;
        }

        // Missing identity fields become empty strings or -1, which validation rejects as a whole snapshot.
        var variants = new List<ExperimentVariant>(command.Variants.Count);
        foreach (var (variantId, variant) in command.Variants)
        {
            if (!TryMapVariant(variantId, variant, out var mapped))
            {
                return false;
            }

            variants.Add(mapped);
        }

        view = ExperimentCatalogView.Freeze(
            commandId,
            command.CommandRevision ?? -1,
            command.ContractSignature ?? string.Empty,
            revision,
            command.ExperimentId ?? string.Empty,
            command.ControlVariantId ?? string.Empty,
            variants
        );
        return true;
    }

    static bool TryMapVariant(
        string variantId,
        ExperimentFileVariant? variant,
        [NotNullWhen(true)] out ExperimentVariant? mapped
    )
    {
        mapped = null;
        if (
            variant is null
            || !TryMapTemplate(variant.Prompt?.SystemTemplate, out var system)
            || !TryMapTemplate(variant.Prompt?.UserTemplate, out var user)
            || !TryMapRoute(variant.Route, out var route)
        )
        {
            return false;
        }

        // A missing weight is -1, so validation rejects it. It is never treated as zero, which would shift traffic silently.
        var parameters =
            variant.Parameters ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        mapped = new ExperimentVariant(
            variantId,
            variant.Weight ?? -1,
            new PromptTemplateOverride(system, user),
            parameters,
            route
        );
        return true;
    }

    static bool TryMapTemplate(
        ExperimentFileTemplate? template,
        [NotNullWhen(true)] out TemplateChange? change
    )
    {
        change = null;
        if (template is null)
        {
            return false;
        }

        switch (template.Mode)
        {
            case "inherit":
                change = new TemplateChange.Inherited();
                return true;
            case "removed":
                change = new TemplateChange.Removed();
                return true;
            case "replaced" when template.Text is { } text:
                change = new TemplateChange.Replaced(text);
                return true;
            default:
                return false;
        }
    }

    static bool TryMapRoute(ExperimentFileRoute? route, out ExecutionProfile? profile)
    {
        profile = null;
        if (route is null)
        {
            // An omitted route inherits the configured base route.
            return true;
        }

        if (
            !ProfileKey.TryCreate(route.Key, out var key)
            || key is null
            || string.IsNullOrWhiteSpace(route.Provider)
            || string.IsNullOrWhiteSpace(route.Model)
        )
        {
            return false;
        }

        var defaults = new ProfileDefaults(
            route.Temperature,
            route.MaxOutputTokens,
            TopP: route.TopP,
            TopK: route.TopK
        );
        profile = new ExecutionProfile(key, route.Provider, route.Model, Defaults: defaults);
        return true;
    }
}
