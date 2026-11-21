using System.Data.Common;
using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Coordinates durable intent with one target CAS; unknown sends only permit read-only reconciliation.</summary>
/// <param name="store">Control-plane store, independent of the runtime target provider.</param>
/// <param name="targets">Explicit server bindings.</param>
public sealed class PublicationCoordinator(
    RelationalControlPlaneStore store,
    PublicationTargetRegistry targets
)
{
    /// <summary>Observes the actual target and persists exact decision/content/precondition pins before delivery.</summary>
    public async Task<ManagementResource> ReviewAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string targetId,
        PublicationReviewRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var target = targets.Resolve(scope, targetId);
        RequireWriter(target);
        PublicationPointer? current;
        try
        {
            current = await ObserveAsync(target, cancellationToken);
        }
        catch (Exception error) when (IsTargetFailure(error))
        {
            throw new ManagementException("publication_target_unreadable", 503);
        }
        return await store.SavePublicationOperationAsync(
            scope,
            actor,
            target.Profile,
            request,
            current,
            cancellationToken
        );
    }

    /// <summary>Claims a reviewed operation once, stores complete immutable content, then performs its exact CAS.</summary>
    public async Task<ManagementResource> DeliverAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string operationId,
        long expectedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var resource = await ReadAsync(scope, operationId, cancellationToken);
        var operation = RelationalControlPlaneStore.Operation(resource);
        var target = targets.Resolve(scope, operation.Profile.Id);
        RequireWriter(target);
        resource = await store.ClaimPublicationOperationAsync(
            scope,
            actor,
            operationId,
            expectedRevision,
            target.Profile,
            cancellationToken
        );
        PublicationPointer? observed = null;
        var state = "Unknown";
        try
        {
            await target.PutAsync(operation.Content, cancellationToken);
            if (
                await target.ReadRevisionAsync(operation.Content.Revision, cancellationToken)
                != operation.Content
            )
            {
                throw new ManagementException("publication_target_content_mismatch", 409);
            }
            var result = await target.ActivateAsync(
                operation.Expected,
                operation.Content,
                operationId,
                cancellationToken
            );
            observed = result.Current;
            state =
                result.Applied && Matches(operation, operationId, observed)
                    ? "Activated"
                    : "TargetDiverged";
        }
        catch (Exception error) when (IsTargetFailure(error))
        {
            // A timeout/cancel after the claim cannot prove no target commit occurred.
            // Persist Unknown even if the request disconnected; never reissue this operation.
        }
        return await store.FinishPublicationOperationAsync(
            scope,
            actor,
            resource,
            state,
            observed,
            CancellationToken.None
        );
    }

    /// <summary>Reads actual target state after lost acknowledgement; it never changes or retries the expected pointer.</summary>
    public async Task<ManagementResource> ReconcileAsync(
        WorkspaceScope scope,
        ManagementActor actor,
        string operationId,
        long expectedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var resource = await ReadAsync(scope, operationId, cancellationToken);
        RelationalControlPlaneStore.RequireRevision(resource, expectedRevision);
        if (resource.State == "Reviewed")
        {
            throw new ManagementException("publication_not_delivered", 409);
        }
        var operation = RelationalControlPlaneStore.Operation(resource);
        var target = targets.Resolve(scope, operation.Profile.Id);
        if (target.Profile != operation.Profile)
        {
            throw new ManagementException("publication_binding_changed", 409);
        }
        PublicationPointer? observed = null;
        var state = "Unknown";
        try
        {
            observed = await ObserveAsync(target, cancellationToken);
            state =
                Matches(operation, operationId, observed) ? "Activated"
                : observed != operation.Expected ? "TargetDiverged"
                : "Unknown";
        }
        catch (Exception error) when (IsTargetFailure(error))
        {
            // Failed reads and unchanged preconditions do not establish whether a send happened.
        }
        return await store.FinishPublicationOperationAsync(
            scope,
            actor,
            resource,
            state,
            observed,
            CancellationToken.None
        );
    }

    private async Task<ManagementResource> ReadAsync(
        WorkspaceScope scope,
        string id,
        CancellationToken ct
    ) =>
        await store.ReadAsync(scope, "publication-operations", id, ct)
        ?? throw new ManagementException("publication_operation_not_found", 404);

    private static bool Matches(
        PublicationOperation operation,
        string operationId,
        PublicationPointer? pointer
    ) =>
        pointer is not null
        && pointer.Revision == operation.Content.Revision
        && pointer.ContentHash == operation.Content.ContentHash
        && pointer.OperationId == operationId
        && pointer.Generation == (operation.Expected?.Generation ?? 0) + 1;

    private static async Task<PublicationPointer?> ObserveAsync(
        IPublicationTarget target,
        CancellationToken ct
    )
    {
        var pointer = await target.ReadCurrentAsync(ct);
        if (pointer is not null)
        {
            var content = await target.ReadRevisionAsync(pointer.Revision, ct);
            if (
                pointer.Generation < 1
                || content is null
                || content.Revision != pointer.Revision
                || content.ContentHash != pointer.ContentHash
            )
            {
                throw new ManagementException("publication_target_content_mismatch", 409);
            }
            PublicationCodec.Verify(content);
        }
        return pointer;
    }

    private static void RequireWriter(IPublicationTarget target)
    {
        if (!target.Capabilities.ImmutableHistory || !target.Capabilities.CompareExchange)
        {
            throw new ManagementException("publication_target_cas_unsupported", 409);
        }
    }

    private static bool IsTargetFailure(Exception error) =>
        error
            is DbException
                or IOException
                or TimeoutException
                or OperationCanceledException
                or ManagementException
                or JsonException;
}
