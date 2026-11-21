using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Host-prepared treatment tied to the exact immutable snapshot inspected by typed preflight.</summary>
/// <param name="Revision">Runtime source revision for the frozen snapshot.</param>
/// <param name="ExperimentId">Selected experiment.</param>
/// <param name="VariantId">Selected variant.</param>
/// <param name="PublicationHash">Frozen management publication content hash.</param>
/// <param name="BuildHash">Registered build content hash.</param>
/// <param name="RunId">Existing authorized ledger run for the evaluation invocation.</param>
public sealed record PreparedWorkerTreatment(
    ExperimentRevision Revision,
    string ExperimentId,
    string VariantId,
    string PublicationHash,
    string BuildHash,
    Guid RunId
);

/// <summary>Application-owned typed preparation bound to its isolated evaluation runtime and source.</summary>
/// <remarks>Implementations must resolve the immutable source revision and existing authorized ledger run without provider calls. The runtime performs typed candidate preparation separately.</remarks>
public interface ITypedTreatmentPreparation<TInput>
{
    /// <summary>Prepares and validates this candidate against the source used by the worker runtime.</summary>
    Task<PreparedWorkerTreatment> PrepareAsync(
        WorkspaceScope scope,
        TInput input,
        JsonElement pinnedRequest,
        CancellationToken cancellationToken
    );
}

/// <summary>Runs an explicitly registered typed command under the claim journal and strict candidate policy.</summary>
/// <remarks>The host owns the runtime, typed serializer and preparation port; no console-supplied type or assembly is loaded.</remarks>
/// <param name="command">Application-registered command bound to the evaluation runtime.</param>
/// <param name="inputType">Host-approved typed JSON metadata.</param>
/// <param name="buildId">Application build served by this adapter.</param>
/// <param name="buildHash">Host-attested registered content digest of the running build.</param>
/// <param name="preparation">Side-effect-free application-owned exact snapshot preparation.</param>
/// <param name="assets">Authorized immutable asset store for replay cases.</param>
public sealed class TypedWorkerAdapter<TInput, TOutput>(
    PromptCommand<TInput, TOutput> command,
    JsonTypeInfo<TInput> inputType,
    string buildId,
    string buildHash,
    ITypedTreatmentPreparation<TInput> preparation,
    IControlPlaneAssetStore? assets = null
) : IWorkerCapability
{
    /// <inheritdoc />
    public bool SupportsBuild(string requestedBuildId) =>
        string.Equals(buildId, requestedBuildId, StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<WorkerPreflightResult> CheckAsync(
        WorkspaceScope scope,
        WorkClaim claim,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _ = await PrepareAsync(scope, claim, cancellationToken);
            return new(true, ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(
                false,
                ContentIdentity.Hash(ContentIdentity.Normalize(claim.Request)),
                "typed_preflight_failed"
            );
        }
    }

    /// <summary>Runs the same pinned typed invocation after the host has durably admitted preflight.</summary>
    public async Task<Outcome<TOutput>> ExecuteAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        WorkClaim claim,
        RelationalControlPlaneStore store,
        CancellationToken cancellationToken = default
    )
    {
        if (actor.Subject != claim.Owner)
        {
            throw new ManagementException("worker_context_invalid");
        }
        var (input, candidate, runId) = await PrepareAsync(scope, claim, cancellationToken);
        return await command
            .WithParameters(input)
            .InRun(runId)
            .WithJournal(new WorkExecutionJournal(store, scope, actor, claim))
            .RequireExperimentCandidate(candidate)
            .ExecuteAsync(cancellationToken);
    }

    /// <summary>Runs one strict command and records its exact delivery or failure without inventing output storage.</summary>
    public async Task<Outcome<TOutput>> ExecuteAndCompleteAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        WorkClaim claim,
        RelationalControlPlaneStore store,
        CancellationToken cancellationToken = default
    )
    {
        if (actor.Subject != claim.Owner)
        {
            throw new ManagementException("worker_context_invalid");
        }
        var (input, expected, runId) = await PrepareAsync(scope, claim, cancellationToken);
        // This measures the full invocation, not a provider attempt or an aggregated case metric.
        var started = Stopwatch.GetTimestamp();
        var outcome = await command
            .WithParameters(input)
            .InRun(runId)
            .WithJournal(new WorkExecutionJournal(store, scope, actor, claim))
            .RequireExperimentCandidate(expected)
            .ExecuteAsync(cancellationToken);
        var elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var request = claim.Request;
        var candidate = request.GetProperty("candidate");
        var selectedPin = request
            .GetProperty("pins")
            .EnumerateArray()
            .Single(p => Text(p.GetProperty("candidate"), "id") == Text(candidate, "id"));
        var item = request.GetProperty("case");
        var common = new Dictionary<string, object?>
        {
            ["jobId"] = Text(request, "jobId"),
            ["workId"] = claim.WorkId,
            ["candidateId"] = Text(candidate, "id"),
            ["caseId"] = Text(item, "id"),
            ["repeatIndex"] = request.GetProperty("repeat").GetInt32(),
            ["buildId"] = Text(candidate, "buildId"),
            ["publicationId"] = Text(candidate, "publicationId"),
            ["publicationHash"] = Text(selectedPin, "publicationHash"),
            ["commandId"] = Text(candidate, "commandId"),
            ["variantId"] = Text(candidate, "variantId"),
            ["purpose"] = "Backtest",
            ["role"] = "Primary",
            ["executionId"] = outcome.ExecutionId,
            ["executionElapsedSeconds"] = elapsedSeconds,
            ["elapsedSource"] = "CapturedInvocationElapsed",
        };
        if (outcome is Outcome<TOutput>.Success success)
        {
            common["outcome"] = "Success";
            var actual = success.Snapshot.Experiment;
            common["comparisonEligible"] =
                expected is ExpectedExperimentCandidate.Treatment treatment
                && actual is not null
                && actual.Revision == treatment.Revision.Value
                && actual.ExperimentId == treatment.ExperimentId
                && actual.VariantId == treatment.VariantId
                && actual.Role == ExperimentRole.Primary;
            common["experimentEvidence"] = actual is null
                ? null
                : new
                {
                    actual.Source,
                    actual.Revision,
                    actual.ExperimentId,
                    actual.VariantId,
                    role = actual.Role.ToString(),
                    actual.Reason,
                };
            if (success.Delivery is OutputDelivery.Stored stored)
            {
                common["storageStatus"] = "Stored";
                common["value"] = stored.Output.Value;
                common["outputRef"] = stored.Output.Reference;
            }
            else
            {
                common["storageStatus"] = "Unavailable";
            }
        }
        else if (outcome is Outcome<TOutput>.Failed failure)
        {
            common["outcome"] =
                failure.Failure is ExecutionFailure.Cancelled ? "Cancelled" : "Failed";
            common["storageStatus"] = "Unavailable";
            common["failureKind"] = failure.Failure.Kind;
            common["comparisonEligible"] = false;
        }
        await store.CompleteWorkAsync(
            scope,
            actor,
            claim.WorkId,
            claim.Owner,
            claim.Epoch,
            JsonSerializer.SerializeToElement(
                common,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ),
            CancellationToken.None
        );
        return outcome;
    }

    private async Task<(
        TInput Input,
        ExpectedExperimentCandidate Candidate,
        Guid RunId
    )> PrepareAsync(WorkspaceScope scope, WorkClaim claim, CancellationToken cancellationToken)
    {
        var request = claim.Request;
        if (
            request.ValueKind != JsonValueKind.Object
            || Text(request, "purpose") != "Backtest"
            || !request.TryGetProperty("candidate", out var candidate)
            || candidate.ValueKind != JsonValueKind.Object
            || Text(candidate, "buildId") != buildId
            || !request.TryGetProperty("case", out var item)
            || item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("input", out var inputJson)
            || inputJson.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("pins", out var pins)
            || pins.ValueKind != JsonValueKind.Array
        )
        {
            throw new ManagementException("worker_request_invalid");
        }
        await RelationalControlPlaneStore.VerifyCaseAssetsAsync(
            scope,
            item,
            assets,
            cancellationToken
        );
        var contract = command.ExperimentContract
            ?? throw new ManagementException("worker_command_unbound");
        if (contract.CommandId != Text(candidate, "commandId"))
        {
            throw new ManagementException("worker_command_mismatch");
        }
        var pin = pins.EnumerateArray()
            .SingleOrDefault(value =>
                value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("candidate", out var selected)
                && Text(selected, "id") == Text(candidate, "id")
            );
        if (pin.ValueKind != JsonValueKind.Object)
        {
            throw new ManagementException("worker_pin_missing");
        }
        var input = inputJson.Deserialize(inputType)
            ?? throw new ManagementException("worker_input_invalid");
        var serialized = JsonSerializer.SerializeToElement(input, inputType);
        if (ContentIdentity.Normalize(serialized) != ContentIdentity.Normalize(inputJson))
        {
            throw new ManagementException("worker_input_roundtrip_mismatch");
        }
        var prepared = await preparation.PrepareAsync(scope, input, request, cancellationToken);
        if (
            prepared is null
            || prepared.RunId == Guid.Empty
            || string.IsNullOrWhiteSpace(prepared.ExperimentId)
            || prepared.PublicationHash != Text(pin, "publicationHash")
            || string.IsNullOrWhiteSpace(buildHash)
            || prepared.BuildHash != buildHash
            || prepared.BuildHash != Text(pin, "buildHash")
            || prepared.VariantId != Text(candidate, "variantId")
        )
        {
            throw new ManagementException("worker_candidate_preflight_mismatch");
        }
        var expected = new ExpectedExperimentCandidate.Treatment(
            prepared.Revision,
            prepared.ExperimentId,
            prepared.VariantId
        );
        var preflight = await command
            .WithParameters(input)
            .InRun(prepared.RunId)
            .RequireExperimentCandidate(expected)
            .PreflightAsync(cancellationToken);
        if (
            !preflight.Ready
            || preflight.CommandId != contract.CommandId
            || preflight.CommandRevision != contract.CommandRevision
            || preflight.ContractSignature != contract.ContractSignature
            || string.IsNullOrWhiteSpace(preflight.User)
        )
        {
            throw new ManagementException("worker_candidate_preflight_mismatch");
        }
        return (input, expected, prepared.RunId);
    }

    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var member)
        && member.ValueKind == JsonValueKind.String
            ? member.GetString()
            : null;
}
