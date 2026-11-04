using Zeeq.LlmCmd;

namespace CommandRoot;

public sealed partial class InterfaceValidationCommand
    : PromptCommand<InvoiceParameters, Invoice>,
        IInputValidator<InvoiceParameters>,
        IResultValidator<Invoice>
{
    const string Id = "invoice.interface-validation";
    const string Profile = "extraction";
    const string UserTemplate = "Extract an invoice from {Document}";

    public static string? ValidateInput(InvoiceParameters input) =>
        input.Document == "throws" ? throw new InvalidOperationException("private hook detail")
        : input.Document.StartsWith("blocked", StringComparison.Ordinal) ? "Input is blocked"
        : null;

    static string? IResultValidator<Invoice>.ValidateResult(Invoice result) =>
        result.Currency == "THROW" ? throw new InvalidOperationException("private hook detail")
        : result.Currency == "BAD" ? "Currency is blocked"
        : null;
}

public sealed record ExperimentValidatedInput(
    string Document,
    [property: RuntimeOverride] string Locale
);

public sealed record ExperimentValidatedResult(string Value);

public sealed partial class ExperimentValidatedCommand
    : PromptCommand<ExperimentValidatedInput, ExperimentValidatedResult>,
        IInputValidator<ExperimentValidatedInput>
{
    const string Id = "invoice.interface-experiment";
    const string UserTemplate = "CONTROL {Document} {Locale}";

    static string? IInputValidator<ExperimentValidatedInput>.ValidateInput(
        ExperimentValidatedInput input
    ) =>
        input.Locale switch
        {
            "fr" => "Locale is blocked",
            "boom" => throw new InvalidOperationException("private hook detail"),
            _ => null,
        };
}
