using CommandRoot;

namespace CommandDemo;

// Normal application records. No serializer context or required-field annotations.
public sealed record InvoiceParameters(string Document, string Locale = "en");
public sealed record Invoice(decimal Total, string Currency, int Count = 3);

public sealed partial class ExtractInvoice : PromptCommand<InvoiceParameters, Invoice>
{
    const string Id = "invoice.extract";
    const string UserTemplate = "Extract an invoice from {Document} ({Locale}).";
}
