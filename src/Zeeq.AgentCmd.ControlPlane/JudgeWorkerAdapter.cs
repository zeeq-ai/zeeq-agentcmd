using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Runs a host-authorized judge route against one pinned evaluator work item.</summary>
/// <remarks>The host selects and owns the MEAI pipeline. One work item admits at most one provider send.</remarks>
/// <param name="client">Authorized host-managed client.</param>
/// <param name="policy">Exact registered judge configuration.</param>
/// <param name="configurationId">Frozen judge configuration identity.</param>
/// <param name="configurationHash">Exact registered configuration content hash.</param>
public sealed class JudgeWorkerAdapter(
    IChatClient client,
    ScopedJudgePolicy policy,
    string configurationId,
    string configurationHash
) : IWorkerCapability
{
    /// <inheritdoc />
    public bool SupportsBuild(string buildId) =>
        string.Equals(buildId, "judge:" + configurationId, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<WorkerPreflightResult> CheckAsync(
        WorkspaceScope scope,
        WorkClaim claim,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var work = claim.Request;
        var matches =
            work.ValueKind == JsonValueKind.Object
            && Text(work, "purpose") == "Evaluator"
            && Text(work, "judgeConfigurationId") == configurationId
            && Text(work, "judgeConfigurationHash") == configurationHash
            && work.TryGetProperty("executionApproved", out var approved)
            && approved.ValueKind == JsonValueKind.True
            && work.TryGetProperty("policy", out var pinned)
            && pinned.ValueKind == JsonValueKind.Object
            && Text(pinned, "routeId") == policy.RouteId
            && Text(pinned, "modelId") == policy.ModelId
            && Text(pinned, "promptVersion") == policy.PromptVersion
            && Text(pinned, "resultSchemaVersion") == policy.ResultSchemaVersion
            && Text(pinned, "rubric") == policy.Rubric
            && pinned.TryGetProperty("maxOutputTokens", out var tokens)
            && tokens.TryGetInt32(out var count)
            && count == policy.MaxOutputTokens
            && count is > 0 and <= 16384
            && work.TryGetProperty("target", out _)
            && Text(work, "executionId") is { Length: > 0 };
        return Task.FromResult(
            new WorkerPreflightResult(
                matches,
                ContentIdentity.Hash(ContentIdentity.Normalize(work)),
                matches ? null : "judge_preflight_failed"
            )
        );
    }

    /// <summary>Journals one judge call and records validated findings against the exact original output.</summary>
    public async Task<ManagementResource> ExecuteAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        WorkClaim claim,
        RelationalControlPlaneStore store,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (
            actor.Subject != claim.Owner
            || !(await CheckAsync(scope, claim, cancellationToken)).Passed
        )
        {
            throw new ManagementException("judge_preflight_failed", 409);
        }
        var judge = new ScopedJudge(client, policy);
        var result = await judge.EvaluateAsync(
            claim,
            true,
            context,
            new WorkExecutionJournal(store, scope, actor, claim),
            cancellationToken
        );
        return await store.RecordJudgeAssessmentAsync(
            scope,
            actor,
            claim.WorkId,
            result,
            CancellationToken.None
        );
    }

    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var member)
        && member.ValueKind == JsonValueKind.String
            ? member.GetString()
            : null;
}
