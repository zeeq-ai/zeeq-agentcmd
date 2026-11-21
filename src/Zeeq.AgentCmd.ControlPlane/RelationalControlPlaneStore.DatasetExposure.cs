using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Exposure is separate from frozen dataset content so later human reveals cannot rewrite an earlier protocol.</summary>
/// <param name="ExposedCaseIds">Cases whose source families have been revealed or used for tuning.</param>
/// <param name="IndependentAcceptanceEligible">Whether this acceptance set has no recorded source-family exposure.</param>
public sealed record DatasetExposure(
    IReadOnlyList<string> ExposedCaseIds,
    bool IndependentAcceptanceEligible
);

public sealed partial class RelationalControlPlaneStore
{
    private async Task RecordDeclaredExposureAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        ManagementActor actor,
        ManagementResource dataset,
        CancellationToken ct
    )
    {
        if (dataset.Kind != "datasets" || Text(dataset.Body, "purpose") != "Acceptance")
        {
            return;
        }
        await LockDatasetFamiliesAsync(db, tx, scope, ct);
        foreach (var item in DatasetCases(dataset.Body))
        {
            if (
                Text(item, "sourceFamilyId") is { } family
                && Text(item, "id") is { } caseId
                && item.TryGetProperty("exposed", out var value)
                && value.ValueKind == JsonValueKind.True
            )
            {
                var inserted = await ExecuteAsync(
                    db,
                    tx,
                    "INSERT INTO cp_family_exposure(scope,family,dataset_id,case_id,actor,at,reason) VALUES(@scope,@family,@dataset,@case,@actor,@at,@reason) ON CONFLICT(scope,family,dataset_id,case_id) DO NOTHING",
                    ct,
                    ("scope", scope.Key),
                    ("family", family),
                    ("dataset", dataset.Id),
                    ("case", caseId),
                    ("actor", actor.Subject),
                    ("at", Timestamp(time.GetUtcNow())),
                    ("reason", "Declared exposure during dataset authoring")
                );
                if (inserted == 1)
                {
                    await EventAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        "dataset.exposure_declared",
                        "datasets",
                        dataset.Id,
                        dataset.Revision,
                        new { caseId, family },
                        ct
                    );
                }
            }
        }
    }

    private async Task LockDatasetFamiliesAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        CancellationToken ct
    )
    {
        if (Provider == "Postgres")
        {
            // Serialize membership and reveal decisions across hosts; an ordinary SELECT is not a split admission lock.
            var key = BinaryPrimitives.ReadInt64LittleEndian(
                SHA256.HashData(Encoding.UTF8.GetBytes("dataset-splits:" + scope.Key))
            );
            await ExecuteAsync(db, tx, "SELECT pg_advisory_xact_lock(@key)", ct, ("key", key));
        }
    }

    private async Task FreezeDatasetFamiliesAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        ManagementResource dataset,
        CancellationToken ct
    )
    {
        var purpose = Text(dataset.Body, "purpose");
        if (purpose is not ("Regression" or "Calibration" or "Acceptance"))
        {
            return;
        }
        await LockDatasetFamiliesAsync(db, tx, scope, ct);
        // Index older frozen records too. A deployment upgrade must not hide previously used sources.
        await using (
            var existing = Command(
                db,
                tx,
                "SELECT id,body FROM cp_documents WHERE scope=@scope AND kind='datasets' AND state='Frozen'",
                ("scope", scope.Key)
            )
        )
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            var rows = new List<(string Id, JsonElement Body)>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), Element(reader.GetString(1))));
            }
            await reader.DisposeAsync();
            foreach (var row in rows)
            {
                var otherPurpose = Text(row.Body, "purpose");
                if (otherPurpose is "Regression" or "Calibration" or "Acceptance")
                {
                    await IndexFamiliesAsync(db, tx, scope, row.Id, otherPurpose, row.Body, ct);
                }
            }
        }
        var cases = DatasetCases(dataset.Body);
        foreach (var item in cases)
        {
            if (Text(item, "reviewId") is { } reviewId)
            {
                var review = await ReadAsync(db, tx, scope, "reviews", reviewId, ct);
                if (review?.State != "AcceptedIntoDraft")
                {
                    throw new ManagementException("dataset_case_review_pending", 409);
                }
                if (
                    Text(review.Body, "kind") == "Incident"
                    && (
                        Text(review.Body, "datasetDraftId") != dataset.Id
                        || Text(review.Body, "caseId") != Text(item, "id")
                        || Text(review.Body, "caseEvidenceHash") != IncidentEvidenceHash(item)
                    )
                )
                {
                    // An accepted task is authority for the reviewed evidence, not for later edits to that case.
                    throw new ManagementException("dataset_case_review_evidence_changed", 409);
                }
            }
        }
        if (
            purpose == "Acceptance"
            && (
                cases.Length == 0
                || cases.Any(item => string.IsNullOrWhiteSpace(Text(item, "sourceFamilyId")))
            )
        )
        {
            throw new ManagementException("acceptance_family_required", 409);
        }
        foreach (var item in cases)
        {
            var family = Text(item, "sourceFamilyId");
            if (string.IsNullOrWhiteSpace(family))
            {
                continue;
            }
            await using var membership = Command(
                db,
                tx,
                "SELECT purpose FROM cp_dataset_families WHERE scope=@scope AND family=@family",
                ("scope", scope.Key),
                ("family", family)
            );
            var purposes = new HashSet<string>(StringComparer.Ordinal);
            await using (var reader = await membership.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    purposes.Add(reader.GetString(0));
                }
            }
            await using var exposure = Command(
                db,
                tx,
                "SELECT 1 FROM cp_family_exposure WHERE scope=@scope AND family=@family LIMIT 1",
                ("scope", scope.Key),
                ("family", family)
            );
            var recordedExposure = await exposure.ExecuteScalarAsync(ct) is not null;
            var exposed =
                recordedExposure
                || item.TryGetProperty("exposed", out var value)
                    && value.ValueKind == JsonValueKind.True;
            if (
                purpose == "Acceptance"
                && (exposed || purposes.Any(other => other != "Acceptance"))
            )
            {
                throw new ManagementException("acceptance_source_exposed", 409);
            }
            if (
                purpose != "Acceptance"
                && purposes.Contains("Acceptance")
                && !(purpose == "Regression" && recordedExposure)
            )
            {
                throw new ManagementException("dataset_split_conflict", 409);
            }
            if (
                purpose == "Calibration" && purposes.Contains("Regression")
                || purpose == "Regression" && purposes.Contains("Calibration")
            )
            {
                throw new ManagementException("dataset_split_conflict", 409);
            }
        }
        await IndexFamiliesAsync(db, tx, scope, dataset.Id, purpose, dataset.Body, ct);
    }

    private static async Task IndexFamiliesAsync(
        DbConnection db,
        DbTransaction tx,
        WorkspaceScope scope,
        string id,
        string purpose,
        JsonElement body,
        CancellationToken ct
    )
    {
        foreach (var item in DatasetCases(body))
        {
            var family = Text(item, "sourceFamilyId");
            if (!string.IsNullOrWhiteSpace(family))
            {
                await ExecuteAsync(
                    db,
                    tx,
                    "INSERT INTO cp_dataset_families(scope,family,purpose,dataset_id) VALUES(@scope,@family,@purpose,@id) ON CONFLICT(scope,family,dataset_id) DO NOTHING",
                    ct,
                    ("scope", scope.Key),
                    ("family", family),
                    ("purpose", purpose),
                    ("id", id)
                );
            }
        }
    }

    /// <summary>Records an explicit case reveal before returning held-out input/reference evidence to a person.</summary>
    /// <remarks>Trusted hosts must call this for equivalent external reveals and prompt-example use. Model evaluation alone does not imply author exposure.</remarks>
    public Task<JsonElement> RevealDatasetCaseAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string datasetId,
        string caseId,
        string reason,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
                {
                    throw new ManagementException("exposure_reason_required");
                }
                await LockDatasetFamiliesAsync(db, tx, scope, cancellationToken);
                var dataset = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    datasetId,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                if (
                    dataset.State is not ("Draft" or "Frozen")
                    || Text(dataset.Body, "purpose") != "Acceptance"
                )
                {
                    throw new ManagementException("frozen_acceptance_required", 409);
                }
                var item = DatasetCases(dataset.Body)
                    .SingleOrDefault(item => Text(item, "id") == caseId);
                if (
                    item.ValueKind == JsonValueKind.Undefined
                    || Text(item, "sourceFamilyId") is not { } family
                )
                {
                    throw new ManagementException("dataset_case_not_found", 404);
                }
                var inserted = await ExecuteAsync(
                    db,
                    tx,
                    "INSERT INTO cp_family_exposure(scope,family,dataset_id,case_id,actor,at,reason) VALUES(@scope,@family,@dataset,@case,@actor,@at,@reason) ON CONFLICT(scope,family,dataset_id,case_id) DO NOTHING",
                    cancellationToken,
                    ("scope", scope.Key),
                    ("family", family),
                    ("dataset", datasetId),
                    ("case", caseId),
                    ("actor", actor.Subject),
                    ("at", Timestamp(time.GetUtcNow())),
                    ("reason", reason)
                );
                if (inserted == 1)
                {
                    await EventAsync(
                        db,
                        tx,
                        scope,
                        actor,
                        "dataset.case_revealed",
                        "datasets",
                        dataset.Id,
                        dataset.Revision,
                        new
                        {
                            caseId,
                            family,
                            reason,
                            datasetHash = dataset.ContentHash,
                        },
                        cancellationToken
                    );
                }
                return item;
            },
            cancellationToken
        );

    /// <summary>Returns current family exposure independently of a dataset's immutable protocol hash.</summary>
    public async Task<DatasetExposure> GetDatasetExposureAsync(
        WorkspaceScope scope,
        string datasetId,
        CancellationToken cancellationToken = default
    )
    {
        var dataset = await ReadAsync(scope, "datasets", datasetId, cancellationToken)
            ?? throw new ManagementException("dataset_not_found", 404);
        return await ExposureForSnapshotAsync(scope, dataset, cancellationToken);
    }

    private async Task<DatasetExposure> ExposureForSnapshotAsync(
        WorkspaceScope scope,
        ManagementResource dataset,
        CancellationToken cancellationToken
    )
    {
        var families = new HashSet<string>(StringComparer.Ordinal);
        await using var db = await OpenAsync(cancellationToken);
        await using var command = Command(
            db,
            null,
            "SELECT DISTINCT family FROM cp_family_exposure WHERE scope=@scope",
            ("scope", scope.Key)
        );
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            families.Add(reader.GetString(0));
        }
        var exposed = DatasetCases(dataset.Body)
            .Where(item => families.Contains(Text(item, "sourceFamilyId") ?? ""))
            .Select(item => Text(item, "id")!)
            .ToArray();
        var declared = DatasetCases(dataset.Body)
            .Any(item =>
                item.TryGetProperty("exposed", out var value)
                && value.ValueKind == JsonValueKind.True
            );
        return new(
            exposed,
            Text(dataset.Body, "purpose") == "Acceptance"
                && dataset.State == "Frozen"
                && exposed.Length == 0
                && !declared
        );
    }

    /// <summary>Produces a browser read model with protected holdout content withheld; the stored dataset and hash do not change.</summary>
    public async Task<ManagementResource> ProjectDatasetAsync(
        WorkspaceScope scope,
        ManagementResource dataset,
        CancellationToken cancellationToken = default
    )
    {
        if (dataset.Kind != "datasets" || Text(dataset.Body, "purpose") != "Acceptance")
        {
            return dataset;
        }
        var exposure = await ExposureForSnapshotAsync(scope, dataset, cancellationToken);
        var body = JsonNode.Parse(dataset.Body.GetRawText())!.AsObject();
        foreach (var node in body["cases"]?.AsArray() ?? [])
        {
            var item = node!.AsObject();
            var exposed = exposure.ExposedCaseIds.Contains(item["id"]!.GetValue<string>());
            item["exposed"] = exposed;
            item["holdoutWithheld"] = !exposed;
            if (!exposed)
            {
                item["input"] = null;
                item["reference"] = null;
                item["assets"] = new JsonArray();
            }
        }
        body["independentAcceptanceEligible"] = exposure.IndependentAcceptanceEligible;
        return dataset with { Body = JsonSerializer.SerializeToElement(body) };
    }

    private static JsonElement[] DatasetCases(JsonElement body) =>
        body.TryGetProperty("cases", out var cases) && cases.ValueKind == JsonValueKind.Array
            ? cases.EnumerateArray().ToArray()
            : [];
}
