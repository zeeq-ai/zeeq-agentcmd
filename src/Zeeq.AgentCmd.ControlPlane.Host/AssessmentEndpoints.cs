using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Routes exact assessment, review and comparison actions.</summary>
internal sealed class AssessmentEndpoints : IEndpoint
{
    public void MapEndpoints(IEndpointRouteBuilder api) => Map(api);

    internal static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet(
                "/comparisons/{id}/summary",
                (HttpContext context, string id, AssessmentHandler handler, CancellationToken ct) =>
                    handler.ComparisonSummaryAsync(context, id, ct)
            )
            .WithName("GetComparisonSummary")
            .Produces<ComparisonReport>();
        api.MapPost(
                "/assessments/llm",
                (
                    HttpContext context,
                    JudgeEvaluationRequest request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.JudgeAsync(context, request, ct)
            )
            .WithName("CreateJudgeEvaluation");
        api.MapPost(
                "/calibrations",
                (
                    HttpContext context,
                    CalibrationRequest request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.CalibrateAsync(context, request, ct)
            )
            .WithName("CalibrateJudge");
        api.MapPost(
                "/assessments/deterministic",
                (
                    HttpContext context,
                    DeterministicAssessmentRequest request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.DeterministicAsync(context, request, ct)
            )
            .WithName("AssessDeterministic");
        api.MapPost(
                "/assessments/human",
                (
                    HttpContext context,
                    HumanAssessmentRequest request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.HumanAsync(context, request, ct)
            )
            .WithName("AssessHuman");
        api.MapPost(
                "/reviews",
                (
                    HttpContext context,
                    SaveResource request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.ReviewAsync(context, request, ct)
            )
            .WithName("CreateReview");
        api.MapPost(
                "/reviews/{id}/claim",
                (HttpContext context, string id, AssessmentHandler handler, CancellationToken ct) =>
                    handler.ClaimAsync(context, id, ct)
            )
            .WithName("ClaimReview");
        api.MapPost(
                "/reviews/{id}/decide",
                (
                    HttpContext context,
                    string id,
                    ReviewDecision request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.DecideAsync(context, id, request, ct)
            )
            .WithName("DecideReview");
        api.MapPost(
                "/comparisons",
                (
                    HttpContext context,
                    SaveComparisonRequest request,
                    AssessmentHandler handler,
                    CancellationToken ct
                ) => handler.CompareAsync(context, request, ct)
            )
            .WithName("SaveComparison");
    }
}

/// <summary>Applies authenticated review actions to exact retained evidence.</summary>
internal sealed class AssessmentHandler(
    RelationalControlPlaneStore store,
    IControlPlaneAssetStore assets
) : IEndpointHandler
{
    internal async Task<IResult> ComparisonSummaryAsync(
        HttpContext context,
        string id,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Read");
        using var activity = ControlPlaneTelemetry.Start("comparisons.summary", authority);
        return Results.Ok(await store.ReadComparisonSummaryAsync(authority.Scope, id, ct));
    }

    internal async Task<IResult> DeterministicAsync(
        HttpContext context,
        DeterministicAssessmentRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("assessments.deterministic", authority);
        return ApiAccess.Result(
            await store.AssessDeterministicAsync(authority.Scope, authority.Actor, request, ct),
            context
        );
    }

    internal async Task<IResult> HumanAsync(
        HttpContext context,
        HumanAssessmentRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("assessments.human", authority);
        return ApiAccess.Result(
            await store.AssessHumanAsync(authority.Scope, authority.Actor, request, ct),
            context
        );
    }

    internal async Task<IResult> JudgeAsync(
        HttpContext context,
        JudgeEvaluationRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Launch");
        using var activity = ControlPlaneTelemetry.Start("assessments.llm.launch", authority);
        return ApiAccess.Result(
            await store.CreateJudgeEvaluationAsync(authority.Scope, authority.Actor, request, ct),
            context
        );
    }

    internal async Task<IResult> CalibrateAsync(
        HttpContext context,
        CalibrationRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("assessments.calibrate", authority);
        return ApiAccess.Result(
            await store.CalibrateJudgeAsync(
                authority.Scope,
                authority.Actor,
                "Judge calibration",
                request,
                ct
            ),
            context
        );
    }

    internal async Task<IResult> ReviewAsync(
        HttpContext context,
        SaveResource request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("reviews.create", authority);
        return ApiAccess.Result(
            await store.CreateReviewAsync(authority.Scope, authority.Actor, request, ct),
            context
        );
    }

    internal async Task<IResult> ClaimAsync(HttpContext context, string id, CancellationToken ct)
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("reviews.claim", authority);
        return ApiAccess.Result(
            await store.ClaimReviewAsync(
                authority.Scope,
                authority.Actor,
                id,
                ApiAccess.Revision(context),
                ct
            ),
            context
        );
    }

    internal async Task<IResult> DecideAsync(
        HttpContext context,
        string id,
        ReviewDecision request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("reviews.decide", authority);
        if (request.Decision == "Accept")
        {
            return ApiAccess.Result(
                await store.AcceptReferenceReviewAsync(
                    authority.Scope,
                    authority.Actor,
                    id,
                    ApiAccess.Revision(context),
                    request,
                    assets,
                    ct
                ),
                context
            );
        }
        return ApiAccess.Result(
            await store.DecideReviewAsync(
                authority.Scope,
                authority.Actor,
                id,
                ApiAccess.Revision(context),
                request,
                ct
            ),
            context
        );
    }

    internal async Task<IResult> CompareAsync(
        HttpContext context,
        SaveComparisonRequest request,
        CancellationToken ct
    )
    {
        var authority = ApiAccess.Authorize(context, "Evaluation.Review");
        using var activity = ControlPlaneTelemetry.Start("comparisons.create", authority);
        return ApiAccess.Result(
            await store.SaveComparisonAsync(
                authority.Scope,
                authority.Actor,
                "Comparison",
                request,
                ct
            ),
            context
        );
    }
}
