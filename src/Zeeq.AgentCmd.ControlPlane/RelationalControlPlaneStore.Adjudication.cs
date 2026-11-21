using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zeeq.AgentCmd.ControlPlane;

public sealed partial class RelationalControlPlaneStore
{
    /// <summary>Appends a reviewer-approved source-cited proposal to a compatible dataset draft.</summary>
    /// <remarks>The cited bytes prove text presence only; the reviewer is responsible for relevance and entailment.</remarks>
    public Task<ManagementResource> AcceptReferenceReviewAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string id,
        long revision,
        ReviewDecision decision,
        IControlPlaneAssetStore assets,
        CancellationToken cancellationToken = default
    ) =>
        TransactionAsync(
            async (db, tx) =>
            {
                var review = await ReadAsync(db, tx, scope, "reviews", id, cancellationToken)
                    ?? throw new ManagementException("not_found", 404);
                RequireRevision(review, revision);
                if (
                    review.State != "Claimed"
                    || Text(review.Body, "assignee") != actor.Subject
                    || Text(review.Body, "kind") != "ReferenceGap"
                    || decision.Decision != "Accept"
                    || string.IsNullOrWhiteSpace(decision.Note)
                    || string.IsNullOrWhiteSpace(decision.DatasetDraftId)
                )
                {
                    throw new ManagementException("review_accept_invalid", 409);
                }
                var datasetId = Text(review.Body, "datasetId");
                var caseId = Text(review.Body, "caseId");
                if (
                    datasetId is null
                    || caseId is null
                    || !review.Body.TryGetProperty("proposedValue", out var proposed)
                    || !review.Body.TryGetProperty("sourceLocator", out var locator)
                    || locator.ValueKind != JsonValueKind.Object
                )
                {
                    throw new ManagementException("review_evidence_required", 409);
                }
                var source = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    datasetId,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                var draft = await ReadAsync(
                    db,
                    tx,
                    scope,
                    "datasets",
                    decision.DatasetDraftId,
                    cancellationToken
                ) ?? throw new ManagementException("dataset_not_found", 404);
                if (source.State != "Frozen" || draft.State != "Draft")
                {
                    throw new ManagementException("dataset_draft_required", 409);
                }
                var original = FindById(source.Body, "cases", caseId);
                var target = FindById(draft.Body, "cases", caseId);
                if (
                    Text(original, "sourceFamilyId") is not { } family
                    || family != Text(target, "sourceFamilyId")
                    || !original.TryGetProperty("input", out var originalInput)
                    || !target.TryGetProperty("input", out var targetInput)
                    || !JsonElement.DeepEquals(originalInput, targetInput)
                    || !original.TryGetProperty("assets", out var originalAssets)
                    || !target.TryGetProperty("assets", out var targetAssets)
                    || !JsonElement.DeepEquals(originalAssets, targetAssets)
                    || originalAssets.ValueKind != JsonValueKind.Array
                )
                {
                    throw new ManagementException("review_source_mismatch", 409);
                }
                var assetHash = Text(locator, "assetHash");
                var quote = Text(locator, "quote");
                if (
                    assetHash is null
                    || string.IsNullOrEmpty(quote)
                    || !locator.TryGetProperty("start", out var startValue)
                    || !startValue.TryGetInt32(out var start)
                    || !locator.TryGetProperty("end", out var endValue)
                    || !endValue.TryGetInt32(out var end)
                    || start < 0
                    || end <= start
                    || !originalAssets
                        .EnumerateArray()
                        .Any(asset =>
                            asset.ValueKind == JsonValueKind.String
                                ? asset.GetString() == assetHash
                                : Text(asset, "hash") == assetHash
                        )
                )
                {
                    throw new ManagementException("review_source_locator_invalid", 409);
                }
                await using (
                    var stream = await assets.OpenAsync(scope, assetHash, cancellationToken)
                )
                {
                    var expected = Encoding.UTF8.GetBytes(quote);
                    if (end - start != expected.Length || end > stream.Length)
                    {
                        throw new ManagementException("review_source_locator_invalid", 409);
                    }
                    stream.Seek(start, SeekOrigin.Begin);
                    var actual = new byte[expected.Length];
                    await stream.ReadExactlyAsync(actual, cancellationToken);
                    if (!actual.AsSpan().SequenceEqual(expected))
                    {
                        throw new ManagementException("review_source_locator_invalid", 409);
                    }
                }
                if (
                    !target.TryGetProperty("reference", out var reference)
                    || !reference.TryGetProperty("value", out var value)
                    || value.ValueKind != JsonValueKind.Array
                    || value
                        .EnumerateArray()
                        .Any(existing => JsonElement.DeepEquals(existing, proposed))
                )
                {
                    throw new ManagementException("review_reference_not_appendable", 409);
                }
                var draftBody = JsonNode.Parse(draft.Body.GetRawText())!.AsObject();
                var targetNode = draftBody["cases"]!
                    .AsArray()
                    .Single(node => node?["id"]?.GetValue<string>() == caseId)!;
                targetNode["reference"]!["value"]!
                    .AsArray()
                    .Add(JsonNode.Parse(proposed.GetRawText()));
                await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    draft,
                    draft.Name,
                    "Draft",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(draftBody)),
                    "dataset.reference_adjudicated",
                    cancellationToken
                );
                var reviewBody = JsonNode.Parse(review.Body.GetRawText())!.AsObject();
                reviewBody["status"] = "AcceptedIntoDraft";
                reviewBody["datasetDraftId"] = draft.Id;
                reviewBody["note"] = decision.Note;
                return await ReplaceAsync(
                    db,
                    tx,
                    scope,
                    actor,
                    review,
                    review.Name,
                    "AcceptedIntoDraft",
                    ContentIdentity.Normalize(JsonSerializer.SerializeToElement(reviewBody)),
                    "review.accepted",
                    cancellationToken
                );
            },
            cancellationToken
        );
}
