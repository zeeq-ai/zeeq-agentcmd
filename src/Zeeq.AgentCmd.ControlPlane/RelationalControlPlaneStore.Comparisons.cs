using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Immutable references used to inspect a comparison under one frozen protocol.</summary>
/// <param name="AssessmentIds">Exact saved assessments, including non-scores.</param>
/// <param name="BaselineCandidateId">Explicit baseline among the assessments.</param>
public sealed record SaveComparisonRequest(
    IReadOnlyList<string> AssessmentIds,
    string BaselineCandidateId
);

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Saves protocol-compatible assessment identities without inventing a winner or filling missing scores.</summary>
    public Task<ManagementResource> SaveComparisonAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string name,
        SaveComparisonRequest request,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                if (
                    request.AssessmentIds is not { Count: > 0 and <= 1000 }
                    || request.AssessmentIds.Distinct(StringComparer.Ordinal).Count()
                        != request.AssessmentIds.Count
                    || string.IsNullOrWhiteSpace(request.BaselineCandidateId)
                )
                {
                    throw new ManagementException("comparison_request_invalid");
                }
                string? datasetHash = null,
                    benchmarkHash = null;
                var baselineFound = false;
                foreach (var id in request.AssessmentIds)
                {
                    var assessment = await ReadAsync(
                        db,
                        tx,
                        scope,
                        "assessments",
                        id,
                        cancellationToken
                    ) ?? throw new ManagementException("assessment_not_found", 404);
                    var dataset = Text(assessment.Body, "datasetHash");
                    var benchmark = Text(assessment.Body, "benchmarkHash");
                    if (
                        string.IsNullOrWhiteSpace(dataset)
                        || string.IsNullOrWhiteSpace(benchmark)
                        || datasetHash is not null && datasetHash != dataset
                        || benchmarkHash is not null && benchmarkHash != benchmark
                    )
                    {
                        throw new ManagementException("comparison_protocol_mismatch", 409);
                    }
                    datasetHash = dataset;
                    benchmarkHash = benchmark;
                    baselineFound |=
                        Text(assessment.Body, "candidateId") == request.BaselineCandidateId;
                }
                if (!baselineFound)
                {
                    throw new ManagementException("comparison_baseline_missing");
                }
                var body = ContentIdentity.Normalize(
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            request.AssessmentIds,
                            request.BaselineCandidateId,
                            datasetHash,
                            benchmarkHash,
                        },
                        Json
                    )
                );
                return await InsertAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    "comparisons",
                    Guid.NewGuid().ToString("N"),
                    name,
                    "Saved",
                    body,
                    cancellationToken
                );
            },
            cancellationToken
        );
}
