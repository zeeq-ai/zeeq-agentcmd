using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Maps the complete management publication without executing application code or guessing route semantics.</summary>
internal static class PublicationCodec
{
    internal static PublicationContent Content(ManagementResource publication)
    {
        var content = new PublicationContent(
            "p:"
                + publication.Id
                + ":"
                + publication.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            publication.ContentHash,
            ContentIdentity.Normalize(publication.Body)
        );
        _ = Snapshot(content);
        return content;
    }

    internal static void Verify(PublicationContent content)
    {
        if (
            !ExperimentRevision.TryCreate(content.Revision, out _)
            || ContentIdentity.Hash(content.Json) != content.ContentHash
        )
        {
            throw new ManagementException("publication_content_invalid", 409);
        }
        using var document = JsonDocument.Parse(content.Json);
        if (ContentIdentity.Normalize(document.RootElement) != content.Json)
        {
            throw new ManagementException("publication_content_not_normalized", 409);
        }
    }

    internal static ExperimentSnapshot Snapshot(PublicationContent content)
    {
        Verify(content);
        ExperimentRevision.TryCreate(content.Revision, out var revision);
        using var document = JsonDocument.Parse(content.Json);
        var root = document.RootElement;
        var commands = new Dictionary<string, ExperimentCatalogView>(StringComparer.Ordinal);
        foreach (var command in Array(root, "commands"))
        {
            var id = Identifier(command, "commandId");
            var variants = new List<ExperimentVariant>();
            foreach (var variant in Array(command, "variants"))
            {
                string[] variantFields =
                [
                    "id",
                    "name",
                    "weight",
                    "userTemplate",
                    "systemTemplate",
                    "systemMode",
                    "profile",
                    "parameters",
                    "route",
                ];
                if (
                    variant.ValueKind != JsonValueKind.Object
                    || variant
                        .EnumerateObject()
                        .Any(p => !variantFields.Contains(p.Name, StringComparer.Ordinal))
                )
                {
                    throw new ManagementException("publication_variant_field_unsupported");
                }
                var weight = Integer(variant, "weight");
                if (weight < 0)
                {
                    throw Invalid();
                }
                var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (variant.TryGetProperty("parameters", out var values))
                {
                    if (values.ValueKind != JsonValueKind.Object)
                    {
                        throw Invalid();
                    }
                    foreach (var property in values.EnumerateObject())
                    {
                        if (
                            property.Name.Length is < 1 or > 128
                            || char.IsAsciiDigit(property.Name[0])
                            || property.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')
                            || !parameters.TryAdd(property.Name, property.Value.Clone())
                        )
                        {
                            throw Invalid();
                        }
                    }
                }
                var user = Text(variant, "userTemplate");
                if (string.IsNullOrWhiteSpace(user) || user.Length > 100000)
                {
                    throw Invalid();
                }
                var system = SystemTemplate(variant);
                if (
                    variant.TryGetProperty("profile", out var legacyProfile)
                    && legacyProfile.ValueKind != JsonValueKind.Null
                )
                {
                    throw new ManagementException(
                        "publication_profile_key_requires_typed_resolution"
                    );
                }
                variants.Add(
                    new(
                        Identifier(variant, "id"),
                        weight,
                        new(system, new TemplateChange.Replaced(user)),
                        parameters.ToFrozenDictionary(StringComparer.Ordinal),
                        Route(variant)
                    )
                );
            }
            // Zero weights are not a stop operation. An empty complete snapshot explicitly restores defaults.
            if (
                variants.Count == 0
                || variants.All(v => v.Weight == 0)
                || variants.Select(v => v.VariantId).Distinct(StringComparer.Ordinal).Count()
                    != variants.Count
            )
            {
                throw new ManagementException("publication_allocation_invalid");
            }
            var control = Identifier(command, "controlVariantId");
            var commandRevision = Integer(command, "commandRevision");
            var signature = Text(command, "contractSignature");
            if (
                commandRevision < 1
                || signature.Length > 256
                || variants.All(v => v.VariantId != control)
                || !commands.TryAdd(
                    id,
                    ExperimentCatalogView.Freeze(
                        id,
                        commandRevision,
                        signature,
                        revision!,
                        Identifier(command, "experimentId"),
                        control,
                        variants
                    )
                )
            )
            {
                throw Invalid();
            }
        }
        var restoreDefaults =
            root.TryGetProperty("restoreConfiguredDefaults", out var restore)
            && restore.ValueKind == JsonValueKind.True;
        if (
            (commands.Count == 0) != restoreDefaults
            || restore.ValueKind
                is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False)
        )
        {
            throw new ManagementException("publication_explicit_restore_required");
        }
        return new(revision!, commands.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static TemplateChange SystemTemplate(JsonElement variant)
    {
        var hasText = variant.TryGetProperty("systemTemplate", out var value);
        var mode =
            variant.TryGetProperty("systemMode", out var selected)
            && selected.ValueKind != JsonValueKind.Null
                ? Text(variant, "systemMode")
                : null;
        if (mode is "inherit" or "remove")
        {
            if (hasText && value.ValueKind != JsonValueKind.Null)
            {
                throw new ManagementException("publication_system_mode_conflict");
            }
            return mode == "inherit"
                ? new TemplateChange.Inherited()
                : new TemplateChange.Removed();
        }
        if (mode is not (null or "replace"))
        {
            throw new ManagementException("publication_system_mode_invalid");
        }
        if (mode is null && !hasText)
        {
            return new TemplateChange.Inherited();
        }
        if (mode is null && value.ValueKind == JsonValueKind.Null)
        {
            return new TemplateChange.Removed();
        }
        return
            value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 and <= 100000 } text
            ? new TemplateChange.Replaced(text)
            : throw Invalid();
    }

    private static ExecutionProfile? Route(JsonElement variant)
    {
        if (
            !variant.TryGetProperty("route", out var route)
            || route.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }
        Allowed(route, ["key", "provider", "model", "temperature", "defaults"]);
        if (
            !route.TryGetProperty("key", out var keyValue)
            || keyValue.ValueKind != JsonValueKind.Object
            || !ProfileKey.TryCreate(Text(keyValue, "id"), out var key)
        )
        {
            throw Invalid();
        }
        Allowed(keyValue, ["id"]);
        ProfileDefaults? defaults = null;
        if (
            route.TryGetProperty("defaults", out var settings)
            && settings.ValueKind != JsonValueKind.Null
        )
        {
            Allowed(
                settings,
                [
                    "temperature",
                    "maxOutputTokens",
                    "topP",
                    "topK",
                    "reasoningEffort",
                    "reasoningOutput",
                ]
            );
            var topP = OptionalFloat(settings, "topP");
            var max = OptionalInt(settings, "maxOutputTokens");
            var topK = OptionalInt(settings, "topK");
            if (topP is < 0 or > 1 || max is <= 0 || topK is <= 0)
            {
                throw Invalid();
            }
            defaults = new(
                Temperature: OptionalFloat(settings, "temperature"),
                MaxOutputTokens: max,
                ReasoningEffort: OptionalEnum<ReasoningEffort>(settings, "reasoningEffort"),
                TopP: topP,
                TopK: topK,
                ReasoningOutput: OptionalEnum<ReasoningOutput>(settings, "reasoningOutput")
            );
        }
        return new(
            key!,
            Text(route, "provider"),
            Text(route, "model"),
            Temperature: OptionalFloat(route, "temperature"),
            Defaults: defaults
        );
    }

    private static void Allowed(JsonElement value, string[] names)
    {
        if (
            value.ValueKind != JsonValueKind.Object
            || value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))
        )
        {
            throw new ManagementException("publication_route_unsupported");
        }
    }

    private static TEnum? OptionalEnum<TEnum>(JsonElement value, string name)
        where TEnum : struct, Enum
    {
        if (
            !value.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }
        return
            property.ValueKind == JsonValueKind.String
            && Enum.TryParse<TEnum>(property.GetString(), out var result)
            && Enum.IsDefined(result)
            && Enum.GetName(result) == property.GetString()
            ? result
            : throw Invalid();
    }

    private static float? OptionalFloat(JsonElement value, string name)
    {
        if (
            !value.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null
        )
        {
            return null;
        }
        return
            property.ValueKind == JsonValueKind.Number
            && property.TryGetSingle(out var number)
            && float.IsFinite(number)
            ? number
            : throw Invalid();
    }

    private static int? OptionalInt(JsonElement value, string name) =>
        !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null
            ? null
            : Integer(value, name);

    private static int Integer(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var number)
            ? number
            : throw Invalid();

    private static string Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw Invalid();

    private static string Identifier(JsonElement value, string name) =>
        Text(value, name) is var text && ExperimentIdentifier.IsValid(text)
            ? text
            : throw Invalid();

    private static JsonElement[] Array(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().ToArray()
            : throw Invalid();

    private static ManagementException Invalid() => new("publication_content_invalid");
}

/// <summary>Runtime source over the same immutable target history; current-pointer changes never retarget old pins.</summary>
/// <param name="target">Explicit host-owned target binding; no management-store access is inferred.</param>
public sealed class PublicationSnapshotSource(IPublicationTarget target) : IExperimentSnapshotSource
{
    /// <inheritdoc />
    public async ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
        ExperimentLookup lookup,
        CancellationToken cancellationToken
    )
    {
        if (lookup.Partition != target.Profile.Scope.Partition)
        {
            return new SourceResult<ExperimentSnapshot>.Unavailable(
                SourceFailure.AuthorizationDenied
            );
        }
        try
        {
            var pointer = lookup.RequestedRevision is null
                ? await target.ReadCurrentAsync(cancellationToken)
                : null;
            var revision = lookup.RequestedRevision?.Value ?? pointer?.Revision;
            if (revision is null)
            {
                return new SourceResult<ExperimentSnapshot>.NoOverride();
            }
            var content = await target.ReadRevisionAsync(revision, cancellationToken);
            if (
                content is null
                || content.Revision != revision
                || pointer is not null && pointer.ContentHash != content.ContentHash
            )
            {
                return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
            }
            return new SourceResult<ExperimentSnapshot>.Found(PublicationCodec.Snapshot(content));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is ManagementException or JsonException)
        {
            return new SourceResult<ExperimentSnapshot>.Invalid(SourceFailure.InvalidRelease);
        }
        catch (Exception error)
            when (error
                    is System.Data.Common.DbException
                        or IOException
                        or TimeoutException
                        or OperationCanceledException
            )
        {
            return new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable);
        }
    }
}
