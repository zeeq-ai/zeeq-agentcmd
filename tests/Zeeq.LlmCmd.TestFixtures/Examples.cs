using System.Text.Json;
using Zeeq.LlmCmd;

namespace CommandRoot;

public static class CommandComposition
{
    public static InvoiceService Activate(CommandRuntime runtime) =>
        new(new ExtractInvoice(runtime));
}

public sealed class InvoiceService(ExtractInvoice extract)
{
    public Task<Outcome<Invoice>> ExtractAsync(
        string document,
        Guid runId,
        CancellationToken ct = default
    ) =>
        extract
            .WithParameters(new(document))
            .WithProfile("extraction")
            .Configure(o => o.Temperature = 0.1f)
            .InRun(runId)
            .ExecuteAsync(ct);

    // IDs belong to the user action. Reuse them when the follow-up request is retried.
    public static (LedgerResult<GradeReceipt>, LedgerResult<CorrectionReceipt>) Review(
        Outcome<Invoice>.Success result,
        InMemoryFeedbackLedger ledger,
        Actor actor,
        Guid gradeEventId,
        Guid correctionEventId
    )
    {
        if (result.Delivery is not OutputDelivery.Stored stored)
        {
            return (
                new LedgerResult<GradeReceipt>.Rejected(
                    "not_collected",
                    "Output was not collected"
                ),
                new LedgerResult<CorrectionReceipt>.Rejected(
                    "not_collected",
                    "Output was not collected"
                )
            );
        }

        var output = stored.Output.Reference;
        var grade = ledger.RecordGrade(
            new GradeRequest(
                gradeEventId,
                output,
                "/Total",
                JsonSerializer.SerializeToElement(result.Value.Total),
                0.9
            ),
            actor
        );
        var correction = ledger.SubmitCorrection(
            new CorrectionRequest(
                correctionEventId,
                output,
                "/Total",
                JsonSerializer.SerializeToElement(result.Value.Total),
                JsonSerializer.SerializeToElement(42m)
            ),
            actor
        );
        return (grade, correction);
    }
}
