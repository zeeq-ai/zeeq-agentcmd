using System.Text.Json;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Feature-specific structural checks before a draft or freeze transition.</summary>
internal static class ResourceRules
{
    internal static JsonElement Manifest(JsonElement body)
    {
        var content = body.TryGetProperty("manifest", out var nested) ? nested : body;
        if (
            body.TryGetProperty("manifestJson", out var raw)
            && raw.ValueKind == JsonValueKind.String
        )
        {
            content = CommandManifest.Parse(raw.GetString()!).Content;
        }
        var manifest = CommandManifest.Parse(content.GetRawText());
        if (
            body.TryGetProperty("manifestDigest", out var digest)
            && digest.ValueKind == JsonValueKind.String
            && digest.GetString() != manifest.Sha256
        )
        {
            throw new ManagementException("manifest_digest_mismatch");
        }
        _ = ContentIdentity.Normalize(manifest.Content);
        var ids = manifest
            .Content.GetProperty("commands")
            .EnumerateArray()
            .Select(command => Text(command, "id"))
            .ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new ManagementException("manifest_duplicate_command");
        }
        return manifest.Content;
    }

    internal static void ValidateEnvelope(SaveResource request)
    {
        if (
            string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 200
            || request.Body.ValueKind != JsonValueKind.Object
        )
        {
            throw new ManagementException("resource_input_invalid");
        }
        if (request.Body.GetRawText().Length > 4 * 1024 * 1024)
        {
            throw new ManagementException("content_too_large", 413);
        }
        _ = ContentIdentity.Normalize(request.Body);
    }

    internal static async Task ValidateAsync(
        IControlPlaneStore store,
        WorkspaceScope scope,
        string kind,
        JsonElement body,
        bool freezing,
        CancellationToken ct
    )
    {
        if (kind == "datasets")
        {
            if (Text(body, "purpose") is not ("Regression" or "Calibration" or "Acceptance"))
            {
                throw new ManagementException("dataset_purpose_required");
            }
            var cases = Array(body, "cases");
            if (cases.Length > 2000 || (freezing && cases.Length == 0))
            {
                throw new ManagementException("dataset_case_count_invalid");
            }
            Unique(cases, "id");
            foreach (var item in cases)
            {
                RequireText(item, "sourceFamilyId");
                if (
                    !item.TryGetProperty("input", out var input)
                    || input.ValueKind != JsonValueKind.Object
                )
                {
                    throw new ManagementException("case_typed_input_required");
                }
                if (
                    item.TryGetProperty("reference", out var reference)
                    && Text(reference, "completeness") is not ("Partial" or "Exhaustive" or "None")
                )
                {
                    throw new ManagementException("reference_completeness_required");
                }
                if (
                    Text(body, "purpose") == "Acceptance"
                    && item.TryGetProperty("exposed", out var exposed)
                    && exposed.ValueKind == JsonValueKind.True
                )
                {
                    throw new ManagementException("acceptance_case_already_exposed");
                }
            }
        }
        else if (kind == "benchmarks")
        {
            var dataset = await store.ReadAsync(
                scope,
                "datasets",
                RequireText(body, "datasetId"),
                ct
            ) ?? throw new ManagementException("dataset_not_found", 404);
            if (freezing && dataset.State != "Frozen")
            {
                throw new ManagementException("dataset_not_frozen", 409);
            }
            var criteria = Array(body, "criteria");
            Unique(criteria, "id");
            if (
                freezing
                && (
                    !criteria.Any(c => Text(c, "id") == Text(body, "primaryCriterionId"))
                    || criteria.Length == 0
                )
            )
            {
                throw new ManagementException("primary_criterion_required");
            }
            if (
                body.TryGetProperty("repeats", out var repeats)
                && (!repeats.TryGetInt32(out var count) || count < 1 || count > 100)
            )
            {
                throw new ManagementException("repeat_policy_invalid");
            }
            foreach (var criterion in criteria)
            {
                if (
                    Text(criterion, "scope")
                    is not ("Field" or "Subtree" or "Collection" or "Whole")
                )
                {
                    throw new ManagementException("criterion_scope_required");
                }
                var hasMap = criterion.TryGetProperty("outputBindings", out var bindings);
                if (
                    hasMap
                    && (
                        bindings.ValueKind != JsonValueKind.Object
                        || !bindings.EnumerateObject().Any()
                        || bindings
                            .EnumerateObject()
                            .Any(binding =>
                                string.IsNullOrWhiteSpace(binding.Name)
                                || !ValidPointer(binding.Value)
                            )
                    )
                )
                {
                    throw new ManagementException("criterion_bindings_invalid");
                }
                if (
                    !hasMap
                    && (
                        !criterion.TryGetProperty("outputBinding", out var output)
                        || !ValidPointer(output)
                    )
                )
                {
                    throw new ManagementException("criterion_pointer_invalid");
                }
                if (
                    !criterion.TryGetProperty("scorer", out var scorer)
                    || Text(scorer, "kind") is not ("Human" or "Exact" or "Numeric" or "Llm")
                )
                {
                    throw new ManagementException("criterion_scorer_required");
                }
                if (Text(criterion, "scope") == "Collection" && freezing)
                {
                    if (
                        !criterion.TryGetProperty("matching", out var matching)
                        || Text(matching, "order") is not ("Preserve" or "Ignore")
                        || Text(matching, "duplicates") is not ("Count" or "Collapse")
                        || Text(matching, "unit") is not ("Item" or "AtomicFact")
                    )
                    {
                        throw new ManagementException("collection_matching_required");
                    }
                }
            }
            foreach (var rule in OptionalArray(body, "guardrails"))
            {
                RequireText(rule, "id");
                RequireText(rule, "metric");
                if (
                    Text(rule, "operator") is not ("Gte" or "Lte" or "Eq")
                    || !rule.TryGetProperty("threshold", out var threshold)
                    || !threshold.TryGetDouble(out var number)
                    || !double.IsFinite(number)
                )
                {
                    throw new ManagementException("guardrail_rule_invalid");
                }
            }
        }
        else if (kind == "publications")
        {
            var build = await store.ReadAsync(scope, "builds", RequireText(body, "buildId"), ct)
                ?? throw new ManagementException("build_not_found", 404);
            var manifest = Manifest(build.Body);
            var commands = Array(body, "commands");
            Unique(commands, "commandId");
            var restoring =
                body.TryGetProperty("restoreConfiguredDefaults", out var restore)
                && restore.ValueKind == JsonValueKind.True;
            if (
                (freezing && commands.Length == 0 && !restoring)
                || (commands.Length != 0 && restoring)
            )
            {
                throw new ManagementException("publication_commands_required");
            }
            foreach (var command in commands)
            {
                var commandId = RequireText(command, "commandId");
                var contract = manifest
                    .GetProperty("commands")
                    .EnumerateArray()
                    .FirstOrDefault(item => Text(item, "id") == commandId);
                if (
                    contract.ValueKind == JsonValueKind.Undefined
                    || !command.TryGetProperty("commandRevision", out var revision)
                    || !contract.TryGetProperty("revision", out var expectedRevision)
                    || !revision.TryGetInt32(out var actualRevision)
                    || expectedRevision.GetInt32() != actualRevision
                    || Text(command, "contractSignature") != Text(contract, "contractSignature")
                )
                {
                    throw new ManagementException("publication_contract_mismatch");
                }
                RequireText(command, "experimentId");
                var variants = Array(command, "variants");
                Unique(variants, "id");
                if (
                    !variants.Any(variant =>
                        Text(variant, "id") == Text(command, "controlVariantId")
                    )
                )
                {
                    throw new ManagementException("control_variant_required");
                }
                foreach (var variant in variants)
                {
                    RequireText(variant, "userTemplate");
                    if (variant.TryGetProperty("parameters", out var parameters))
                    {
                        var allowed = OptionalArray(contract, "runtimeOverrides")
                            .Select(item => Text(item, "name"))
                            .ToHashSet(StringComparer.Ordinal);
                        if (
                            parameters.ValueKind != JsonValueKind.Object
                            || parameters
                                .EnumerateObject()
                                .Any(parameter => !allowed.Contains(parameter.Name))
                        )
                        {
                            throw new ManagementException("parameter_override_not_allowed");
                        }
                    }
                    if (
                        !variant.TryGetProperty("weight", out var weight)
                        || !weight.TryGetInt32(out var number)
                        || number < 0
                    )
                    {
                        throw new ManagementException("variant_weight_invalid");
                    }
                }
            }
        }
        else if (kind is "source-profiles" or "bindings")
        {
            foreach (var property in body.EnumerateObject())
            {
                if (
                    property.Name.Equals("password", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("secret", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("connectionString", StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new ManagementException("secret_reference_required");
                }
            }
        }
    }

    private static bool ValidPointer(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && (value.GetString() == "" || value.GetString()!.StartsWith('/'))
        && !value.GetString()!.Contains('*');

    private static JsonElement[] Array(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : throw new ManagementException(name + "_required");

    private static JsonElement[] OptionalArray(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : [];

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RequireText(JsonElement body, string name) =>
        !string.IsNullOrWhiteSpace(Text(body, name))
            ? Text(body, name)!
            : throw new ManagementException(name + "_required");

    private static void Unique(JsonElement[] values, string name)
    {
        var ids = values.Select(value => RequireText(value, name)).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new ManagementException("duplicate_" + name);
        }
    }
}
