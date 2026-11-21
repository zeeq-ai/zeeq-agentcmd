using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeeq.AgentCmd.ControlPlane;

public sealed partial class RelationalControlPlaneStore
{
    private static readonly HashSet<string> BrowserSafeFields = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "id",
        "kind",
        "purpose",
        "status",
        "state",
        "jobId",
        "parentJobId",
        "sourceJobId",
        "evaluatorJobId",
        "executionId",
        "assessmentId",
        "datasetId",
        "benchmarkId",
        "caseId",
        "candidateId",
        "commandId",
        "variantId",
        "buildId",
        "publicationId",
        "criterionId",
        "criterionVersion",
        "repeatIndex",
        "revision",
        "epoch",
        "assignee",
        "owner",
        "outcome",
        "storageStatus",
        "comparisonEligible",
        "datasetHash",
        "sourceDatasetHash",
        "benchmarkHash",
        "buildHash",
        "publicationHash",
        "criterionHash",
        "judgeConfigurationId",
        "judgeConfigurationHash",
        "calibrationId",
        "executionApproved",
        "offset",
        "limit",
        "repeat",
        "repeats",
        "workId",
        "holdoutWithheld",
        "independentAcceptanceEligible",
    };

    private sealed record BrowserHoldout(bool HasAcceptance, bool Withheld, bool Resolved);

    private sealed class BrowserTraversal
    {
        internal Dictionary<string, BrowserHoldout> Cache { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Active { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Withholds unrevealed acceptance evidence while retaining resource identity and safe progress.</summary>
    public async Task<ManagementResource> ProjectBrowserResourceAsync(
        WorkspaceScope scope,
        ManagementResource resource,
        CancellationToken cancellationToken = default
    )
    {
        if (resource.Kind == "datasets")
        {
            return await ProjectDatasetAsync(scope, resource, cancellationToken);
        }
        var result = await BrowserHoldoutAsync(
            scope,
            resource,
            null,
            new BrowserTraversal(),
            0,
            cancellationToken
        );
        if (!result.Withheld)
        {
            return resource;
        }
        var projected = SafeBrowserBody(resource.Body);
        projected["holdoutWithheld"] = true;
        return resource with { Body = JsonSerializer.SerializeToElement(projected) };
    }

    /// <summary>Preserves aggregate comparison metrics while hiding unrevealed acceptance case details.</summary>
    public async Task<ComparisonReport> ProjectBrowserComparisonAsync(
        WorkspaceScope scope,
        ComparisonReport report,
        CancellationToken cancellationToken = default
    )
    {
        var saved = await ReadAsync(scope, "comparisons", report.ComparisonId, cancellationToken);
        if (
            saved is null
            || !saved.Body.TryGetProperty("assessmentIds", out var ids)
            || ids.ValueKind != JsonValueKind.Array
        )
        {
            return WithholdComparison(report, null);
        }
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        var sawAcceptance = false;
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String || id.GetString() is not { } assessmentId)
            {
                return WithholdComparison(report, null);
            }
            var assessment = await ReadAsync(scope, "assessments", assessmentId, cancellationToken);
            if (assessment is null)
            {
                return WithholdComparison(report, null);
            }
            var visibility = await BrowserHoldoutAsync(
                scope,
                assessment,
                null,
                new BrowserTraversal(),
                0,
                cancellationToken
            );
            sawAcceptance |= visibility.HasAcceptance;
            if (visibility.Withheld)
            {
                var caseId = Text(assessment.Body, "caseId");
                if (caseId is null)
                {
                    return WithholdComparison(report, null);
                }
                hidden.Add(caseId);
            }
        }
        if (!sawAcceptance && hidden.Count == 0)
        {
            return report;
        }
        // Unassessed cases are not represented by assessment IDs. Check the complete frozen dataset too.
        var first = ids.EnumerateArray().FirstOrDefault();
        var sample =
            first.ValueKind == JsonValueKind.String
                ? await ReadAsync(scope, "assessments", first.GetString()!, cancellationToken)
                : null;
        var jobId = sample is null ? null : Text(sample.Body, "jobId");
        var job = jobId is null ? null : await ReadAsync(scope, "jobs", jobId, cancellationToken);
        var datasetId = job is null ? null : Text(job.Body, "datasetId");
        var dataset = datasetId is null
            ? null
            : await ReadAsync(scope, "datasets", datasetId, cancellationToken);
        if (dataset is null)
        {
            return WithholdComparison(report, null);
        }
        if (Text(dataset.Body, "purpose") == "Acceptance")
        {
            var exposure = await ExposureForSnapshotAsync(scope, dataset, cancellationToken);
            var visible = exposure.ExposedCaseIds.ToHashSet(StringComparer.Ordinal);
            foreach (var item in DatasetCases(dataset.Body))
            {
                var caseId = Text(item, "id");
                if (caseId is null)
                {
                    return WithholdComparison(report, null);
                }
                if (!visible.Contains(caseId))
                {
                    hidden.Add(caseId);
                }
            }
        }
        return hidden.Count == 0 ? report : WithholdComparison(report, hidden);
    }

    private static ComparisonReport WithholdComparison(
        ComparisonReport report,
        HashSet<string>? hidden
    )
    {
        bool Hide(string caseId) => hidden is null || hidden.Contains(caseId);
        return report with
        {
            HoldoutWithheld = true,
            Criteria = report
                .Criteria.Select(criterion =>
                    criterion with
                    {
                        CommonCaseIds = criterion
                            .CommonCaseIds.Where(caseId => !Hide(caseId))
                            .ToArray(),
                        Candidates = criterion
                            .Candidates.Select(candidate =>
                                candidate with
                                {
                                    Cases = candidate
                                        .Cases.Select(item =>
                                            Hide(item.CaseId)
                                                ? item with
                                                {
                                                    Status = "Withheld",
                                                    Score = null,
                                                    AssessmentIds = [],
                                                    Outcomes = [],
                                                }
                                                : item
                                        )
                                        .ToArray(),
                                }
                            )
                            .ToArray(),
                    }
                )
                .ToArray(),
        };
    }

    private async Task<BrowserHoldout> BrowserHoldoutAsync(
        WorkspaceScope scope,
        ManagementResource resource,
        string? inheritedCaseId,
        BrowserTraversal traversal,
        int depth,
        CancellationToken cancellationToken
    )
    {
        if (depth > 12)
        {
            return new(false, true, false);
        }
        var body = resource.Body;
        if (body.ValueKind != JsonValueKind.Object)
        {
            return new(false, true, false);
        }
        var caseId = Text(body, "caseId") ?? inheritedCaseId;
        if (caseId is null && Text(body, "executionId") is { } executionId)
        {
            var execution = await ReadAsync(scope, "executions", executionId, cancellationToken);
            caseId = execution is null ? null : Text(execution.Body, "caseId");
        }
        if (caseId is null && Text(body, "assessmentId") is { } directAssessmentId)
        {
            var assessment = await ReadAsync(
                scope,
                "assessments",
                directAssessmentId,
                cancellationToken
            );
            caseId = assessment is null ? null : Text(assessment.Body, "caseId");
        }
        var key = JsonSerializer.Serialize(new[] { resource.Kind, resource.Id, caseId });
        if (traversal.Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        if (!traversal.Active.Add(key))
        {
            return new(false, true, false);
        }
        var contains = false;
        var withheld = false;
        var resolved = Text(body, "purpose") == "Production";
        if (Text(body, "sourceFamilyId") is { Length: > 0 } family)
        {
            // Intake observations can carry a family without a dataset link; protect any matching frozen holdout.
            var protectedFamily = false;
            await using (var db = await OpenAsync(cancellationToken))
            {
                await using (
                    var command = Command(
                        db,
                        null,
                        "SELECT body FROM cp_documents WHERE scope=@scope AND kind='datasets' AND state='Frozen'",
                        ("scope", scope.Key)
                    )
                )
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var candidate = Element(reader.GetString(0));
                        if (
                            Text(candidate, "purpose") == "Acceptance"
                            && DatasetCases(candidate)
                                .Any(item => Text(item, "sourceFamilyId") == family)
                        )
                        {
                            protectedFamily = true;
                            break;
                        }
                    }
                }
                if (protectedFamily)
                {
                    await using var exposure = Command(
                        db,
                        null,
                        "SELECT 1 FROM cp_family_exposure WHERE scope=@scope AND family=@family LIMIT 1",
                        ("scope", scope.Key),
                        ("family", family)
                    );
                    var revealed = await exposure.ExecuteScalarAsync(cancellationToken) is not null;
                    contains = true;
                    resolved = true;
                    withheld |= !revealed;
                }
            }
        }
        if (Text(body, "datasetId") is { } datasetId)
        {
            var dataset = await ReadAsync(scope, "datasets", datasetId, cancellationToken);
            if (dataset is null)
            {
                withheld = true;
            }
            else if (Text(dataset.Body, "purpose") == "Acceptance")
            {
                contains = true;
                resolved = true;
                var exposure = await ExposureForSnapshotAsync(scope, dataset, cancellationToken);
                var cases = DatasetCases(dataset.Body);
                if (caseId is null)
                {
                    withheld |=
                        cases.Length == 0
                        || cases.Any(item =>
                            Text(item, "id") is not { } id || !exposure.ExposedCaseIds.Contains(id)
                        );
                }
                else
                {
                    withheld |=
                        !cases.Any(item => Text(item, "id") == caseId)
                        || !exposure.ExposedCaseIds.Contains(caseId);
                }
            }
            else if (Text(dataset.Body, "purpose") is "Calibration" or "Regression")
            {
                resolved = true;
            }
        }
        foreach (
            var (property, kind) in new[]
            {
                ("jobId", "jobs"),
                ("parentJobId", "jobs"),
                ("sourceJobId", "jobs"),
                ("evaluatorJobId", "jobs"),
                ("executionId", "executions"),
                ("assessmentId", "assessments"),
            }
        )
        {
            var id = Text(body, property);
            if (id is null || kind == resource.Kind && id == resource.Id)
            {
                continue;
            }
            var child = await ReadAsync(scope, kind, id, cancellationToken);
            if (child is null)
            {
                withheld = true;
                continue;
            }
            var linked = await BrowserHoldoutAsync(
                scope,
                child,
                caseId,
                traversal,
                depth + 1,
                cancellationToken
            );
            contains |= linked.HasAcceptance;
            withheld |= linked.Withheld;
            resolved |= linked.Resolved;
        }
        if (
            body.TryGetProperty("assessmentIds", out var assessmentIds)
            && assessmentIds.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var value in assessmentIds.EnumerateArray())
            {
                if (
                    value.ValueKind != JsonValueKind.String
                    || value.GetString() is not { } assessmentId
                )
                {
                    withheld = true;
                    continue;
                }
                var child = await ReadAsync(scope, "assessments", assessmentId, cancellationToken);
                if (child is null)
                {
                    withheld = true;
                    continue;
                }
                var linked = await BrowserHoldoutAsync(
                    scope,
                    child,
                    null,
                    traversal,
                    depth + 1,
                    cancellationToken
                );
                contains |= linked.HasAcceptance;
                withheld |= linked.Withheld;
                resolved |= linked.Resolved;
            }
        }
        if (
            !resolved
            && (
                resource.Kind
                is "jobs"
                    or "executions"
                    or "assessments"
                    or "reviews"
                    or "comparisons"
            )
        )
        {
            withheld = true;
        }
        var result = new BrowserHoldout(contains, withheld, resolved);
        traversal.Active.Remove(key);
        traversal.Cache[key] = result;
        return result;
    }

    private static JsonObject SafeBrowserBody(JsonElement body)
    {
        var result = new JsonObject();
        if (body.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (var property in body.EnumerateObject())
        {
            if (
                BrowserSafeFields.Contains(property.Name)
                && property.Value.ValueKind
                    is JsonValueKind.String
                        or JsonValueKind.Number
                        or JsonValueKind.True
                        or JsonValueKind.False
                        or JsonValueKind.Null
            )
            {
                result[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            else if (
                property.NameEquals("counts")
                && property.Value.ValueKind == JsonValueKind.Object
            )
            {
                var counts = new JsonObject();
                foreach (var count in property.Value.EnumerateObject())
                {
                    if (count.Value.ValueKind == JsonValueKind.Number)
                    {
                        counts[count.Name] = JsonNode.Parse(count.Value.GetRawText());
                    }
                }
                result[property.Name] = counts;
            }
            else if (property.NameEquals("items"))
            {
                result[property.Name] = new JsonArray();
            }
            else if (property.NameEquals("pins") && property.Value.ValueKind == JsonValueKind.Array)
            {
                var pins = new JsonArray();
                foreach (var pin in property.Value.EnumerateArray())
                {
                    if (pin.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var safePin = SafeBrowserBody(pin);
                    if (
                        pin.TryGetProperty("candidate", out var candidate)
                        && candidate.ValueKind == JsonValueKind.Object
                    )
                    {
                        safePin["candidate"] = SafeBrowserBody(candidate);
                    }
                    pins.Add(safePin);
                }
                result[property.Name] = pins;
            }
        }
        return result;
    }
}
