using Zeeq.AgentCmd;

namespace ExperimentDemo;

/// <summary>Invoice text stays caller-owned; the experiment may choose a locale.</summary>
public sealed record InvoiceInput(
    string Document,
    [property: RuntimeOverride] string Locale = "en"
);

/// <summary>A small structured response from the synthetic client.</summary>
public sealed record InvoiceResult(string Value);

/// <summary>Declares the compiled default used when the experiment source is unavailable.</summary>
public sealed partial class ExtractInvoice : PromptCommand<InvoiceInput, InvoiceResult>
{
    const string Id = "invoice.experiment";
    const string UserTemplate = "Extract {Document} in {Locale}.";
}
