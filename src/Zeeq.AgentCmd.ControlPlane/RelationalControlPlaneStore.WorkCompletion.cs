using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Records exact final execution evidence separately from provider-attempt receipts.</summary>
    public Task CompleteWorkAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string workId,
        string owner,
        long epoch,
        JsonElement receipt,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    receipt.ValueKind != JsonValueKind.Object
                    || Text(receipt, "workId") != workId
                    || Text(receipt, "purpose") != "Backtest"
                    || Text(receipt, "role") is not ("Primary" or "Shadow")
                    || Text(receipt, "outcome") is not ("Success" or "Failed" or "Cancelled")
                    || Text(receipt, "storageStatus") is not ("Stored" or "Unavailable")
                    || !Guid.TryParse(Text(receipt, "executionId"), out var executionId)
                    || executionId == Guid.Empty
                )
                {
                    throw new ManagementException("execution_receipt_invalid");
                }
                if (
                    Text(receipt, "storageStatus") == "Unavailable"
                    && (
                        receipt.TryGetProperty("value", out _)
                        || receipt.TryGetProperty("outputRef", out _)
                    )
                )
                {
                    throw new ManagementException("execution_receipt_invalid");
                }
                if (
                    Text(receipt, "storageStatus") == "Stored"
                    && (
                        !receipt.TryGetProperty("value", out _)
                        || !receipt.TryGetProperty("outputRef", out var outputRef)
                        || !ValidOutputReference(outputRef, executionId)
                    )
                )
                {
                    throw new ManagementException("stored_output_reference_required");
                }
                var jobId = Text(receipt, "jobId")
                    ?? throw new ManagementException("execution_receipt_invalid");
                // Serialize final work completion and cancellation on the owning job row.
                if (
                    await ExecuteAsync(
                        db,
                        tx,
                        "UPDATE cp_documents SET state=state WHERE scope=@scope AND kind='jobs' AND id=@job",
                        cancellationToken,
                        ("scope", scope.Key),
                        ("job", jobId)
                    ) != 1
                )
                {
                    throw new ManagementException("not_found", 404);
                }
                await using (
                    var read = Command(
                        db,
                        tx,
                        "SELECT job_id,build_id,candidate_id,case_id,repeat_index,request FROM cp_work WHERE scope=@scope AND id=@work AND owner=@owner AND epoch=@epoch",
                        ("scope", scope.Key),
                        ("work", workId),
                        ("owner", owner),
                        ("epoch", epoch)
                    )
                )
                await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                {
                    if (
                        !await reader.ReadAsync(cancellationToken)
                        || Text(receipt, "jobId") != reader.GetString(0)
                        || Text(receipt, "buildId") != reader.GetString(1)
                        || Text(receipt, "candidateId") != reader.GetString(2)
                        || Text(receipt, "caseId") != reader.GetString(3)
                        || !receipt.TryGetProperty("repeatIndex", out var repeat)
                        || !repeat.TryGetInt32(out var repeatIndex)
                        || repeatIndex != reader.GetInt32(4)
                    )
                    {
                        throw new ManagementException("execution_receipt_mismatch", 409);
                    }
                    var workRequest = Element(reader.GetString(5));
                    var candidate = workRequest.GetProperty("candidate");
                    var pin = workRequest
                        .GetProperty("pins")
                        .EnumerateArray()
                        .First(p =>
                            Text(p.GetProperty("candidate"), "id") == Text(candidate, "id")
                        );
                    if (
                        Text(receipt, "publicationHash") != Text(pin, "publicationHash")
                        || Text(receipt, "publicationId") != Text(candidate, "publicationId")
                        || Text(receipt, "commandId") != Text(candidate, "commandId")
                        || Text(receipt, "variantId") != Text(candidate, "variantId")
                    )
                    {
                        throw new ManagementException("execution_receipt_mismatch", 409);
                    }
                }
                var body = ContentIdentity.Normalize(receipt);
                var changed = await ExecuteAsync(
                    db,
                    tx,
                    "UPDATE cp_work SET status='Finished',result=@receipt WHERE scope=@scope AND id=@work AND owner=@owner AND epoch=@epoch AND status IN ('Claimed','Held') AND NOT EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id AND a.state='Started') AND (@success=0 OR EXISTS (SELECT 1 FROM cp_attempts a WHERE a.scope=cp_work.scope AND a.work_id=cp_work.id AND a.state='Finished'))",
                    cancellationToken,
                    ("success", Text(receipt, "outcome") == "Success" ? 1 : 0),
                    ("scope", scope.Key),
                    ("work", workId),
                    ("owner", owner),
                    ("epoch", epoch),
                    ("receipt", body)
                );
                if (changed == 0)
                {
                    await using var command = Command(
                        db,
                        tx,
                        "SELECT result FROM cp_work WHERE scope=@scope AND id=@work AND owner=@owner AND epoch=@epoch AND status='Finished'",
                        ("scope", scope.Key),
                        ("work", workId),
                        ("owner", owner),
                        ("epoch", epoch)
                    );
                    if ((await command.ExecuteScalarAsync(cancellationToken)) as string != body)
                    {
                        throw new ManagementException("work_completion_conflict", 409);
                    }
                    return true;
                }
                await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "executions",
                    Text(receipt, "executionId")!,
                    Text(receipt, "executionId")!,
                    "Recorded",
                    body,
                    cancellationToken
                );
                await EventAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "work.completed",
                    "work",
                    workId,
                    epoch,
                    new { receiptHash = ContentIdentity.Hash(body) },
                    cancellationToken
                );
                await using (
                    var pending = Command(
                        db,
                        tx,
                        "SELECT COUNT(*) FROM cp_work WHERE scope=@scope AND job_id=@job AND status<>'Finished'",
                        ("scope", scope.Key),
                        ("job", jobId)
                    )
                )
                {
                    if (
                        Convert.ToInt64(
                            await pending.ExecuteScalarAsync(cancellationToken),
                            System.Globalization.CultureInfo.InvariantCulture
                        ) == 0
                    )
                    {
                        var job = await ReadAsync(db, tx, scope, "jobs", jobId, cancellationToken)
                            ?? throw new ManagementException("not_found", 404);
                        if (job.State == "Queued")
                        {
                            await ReplaceAsync(
                                db,
                                tx,
                                scope,
                                actor,
                                job,
                                job.Name,
                                "Finished",
                                ContentIdentity.Normalize(job.Body),
                                "job.completed",
                                cancellationToken
                            );
                        }
                    }
                }
                return true;
            },
            cancellationToken
        );

    private static bool ValidOutputReference(JsonElement reference, Guid executionId)
    {
        if (
            reference.ValueKind != JsonValueKind.Object
            || !Guid.TryParse(Text(reference, "runId"), out var run)
            || run == Guid.Empty
            || !Guid.TryParse(Text(reference, "artifactId"), out var artifact)
            || artifact == Guid.Empty
            || !Guid.TryParse(Text(reference, "versionId"), out var versionId)
            || versionId == Guid.Empty
            || !reference.TryGetProperty("version", out var version)
            || !version.TryGetInt32(out var n)
            || n < 1
            || !reference.TryGetProperty("producer", out var producer)
            || producer.ValueKind != JsonValueKind.Object
            || !Guid.TryParse(Text(producer, "id"), out var producerId)
            || producerId == Guid.Empty
        )
        {
            return false;
        }
        var kind = Text(producer, "$producer");
        if (kind == "command" && producerId != executionId)
        {
            return false;
        }
        // A composition is its own producer; it must not be replaced by a component execution.
        if (kind is not ("command" or "composition"))
        {
            return false;
        }
        return !reference.TryGetProperty("executionId", out var linked)
            || linked.ValueKind == JsonValueKind.Null
            || (
                kind == "command"
                && linked.ValueKind == JsonValueKind.String
                && Guid.TryParse(linked.GetString(), out var linkedId)
                && linkedId == executionId
            );
    }
}
