using System.Data.Common;
using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

public sealed partial class RelationalControlPlaneStore
{
    private static JsonElement BuildManifest(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return default;
        }
        if (
            body.TryGetProperty("manifestJson", out var raw)
            && raw.ValueKind == JsonValueKind.String
        )
        {
            using var document = JsonDocument.Parse(raw.GetString()!);
            return document.RootElement.Clone();
        }
        return body.TryGetProperty("manifest", out var nested) ? nested : body;
    }

    private static string? OutputSignature(ManagementResource build, string commandId)
    {
        var manifest = BuildManifest(build.Body);
        if (
            manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }
        var selected = commands
            .EnumerateArray()
            .Where(command => Text(command, "id") == commandId)
            .ToArray();
        if (
            selected.Length != 1
            || !selected[0].TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Object
            || Text(output, "status") != "declared"
        )
        {
            return null;
        }
        return Text(output, "signature");
    }

    private static async Task<string?> ResolveOutputBindingAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        JsonElement criterion,
        ManagementResource execution,
        CancellationToken cancellationToken
    )
    {
        if (!criterion.TryGetProperty("outputBindings", out var bindings))
        {
            return Text(criterion, "outputBinding");
        }
        if (bindings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var jobId = Text(execution.Body, "jobId");
        var candidateId = Text(execution.Body, "candidateId");
        var commandId = Text(execution.Body, "commandId");
        var buildId = Text(execution.Body, "buildId");
        if (jobId is null || candidateId is null || commandId is null || buildId is null)
        {
            return null;
        }
        var job = await ReadAsync(db, tx, scope, "jobs", jobId, cancellationToken);
        var build = await ReadAsync(db, tx, scope, "builds", buildId, cancellationToken);
        if (
            job is null
            || build is null
            || !job.Body.TryGetProperty("pins", out var pins)
            || pins.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }
        var matches = pins.EnumerateArray()
            .Where(pin =>
                pin.ValueKind == JsonValueKind.Object
                && pin.TryGetProperty("candidate", out var candidate)
                && Text(candidate, "id") == candidateId
            )
            .ToArray();
        if (
            matches.Length != 1
            || Text(matches[0], "buildHash") != build.ContentHash
            || Text(matches[0].GetProperty("candidate"), "buildId") != buildId
            || Text(matches[0].GetProperty("candidate"), "commandId") != commandId
            || Text(matches[0], "publicationHash") != Text(execution.Body, "publicationHash")
        )
        {
            return null;
        }
        var signature = OutputSignature(build, commandId);
        return
            signature is not null
            && bindings.TryGetProperty(signature, out var selected)
            && selected.ValueKind == JsonValueKind.String
            ? selected.GetString()
            : null;
    }

    private static void RequireCommonShapeOrMappings(
        JsonElement benchmark,
        IReadOnlyCollection<string> shapeIdentities
    )
    {
        if (
            !benchmark.TryGetProperty("criteria", out var criteria)
            || criteria.ValueKind != JsonValueKind.Array
        )
        {
            throw new ManagementException("job_protocol_invalid", 409);
        }
        foreach (var criterion in criteria.EnumerateArray())
        {
            if (criterion.ValueKind != JsonValueKind.Object)
            {
                throw new ManagementException("job_protocol_invalid", 409);
            }
            if (criterion.TryGetProperty("outputBindings", out var bindings))
            {
                if (
                    bindings.ValueKind != JsonValueKind.Object
                    || !bindings.EnumerateObject().Any()
                    || bindings
                        .EnumerateObject()
                        .Any(property =>
                            string.IsNullOrWhiteSpace(property.Name)
                            || property.Value.ValueKind != JsonValueKind.String
                            || property.Value.GetString() is not { } pointer
                            || pointer.Length > 0 && !pointer.StartsWith('/')
                        )
                )
                {
                    throw new ManagementException("criterion_output_bindings_invalid", 409);
                }
            }
            else if (shapeIdentities.Distinct(StringComparer.Ordinal).Skip(1).Any())
            {
                throw new ManagementException("criterion_output_bindings_required", 409);
            }
        }
    }
}
