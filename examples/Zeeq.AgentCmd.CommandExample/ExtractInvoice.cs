using Zeeq.AgentCmd;

namespace CommandDemo;

// Normal application records. No serializer context or required-field annotations.
/// <summary>Source document and locale for invoice extraction.</summary>
public sealed record InvoiceParameters(string Document, string Locale = "en");

/// <summary>Typed invoice data returned by the command.</summary>
public sealed record Invoice(decimal Total, string Currency, int Count = 3);

/// <summary>Declares the invoice extraction intent; the generator supplies its runtime boilerplate.</summary>
public sealed partial class ExtractInvoice : PromptCommand<InvoiceParameters, Invoice>
{
    const string Id = "invoice.extract";
    const string UserTemplate = "Extract an invoice from {Document} ({Locale}).";
}
