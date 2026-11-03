using System.Text.Json.Serialization;
namespace CommandRoot;
public sealed record InvoiceParameters(string Document, string? Locale = "en");
public sealed record Invoice(decimal Total, string Currency);
public sealed partial class ExtractInvoice : PromptCommand<InvoiceParameters,Invoice> {
    const string Id = "invoice.extract";
    const string Profile = "extraction"; // Optional command override; omitted commands use the host default.
    const string SystemTemplate = "You extract invoices. Reply with JSON only.";
    const string UserTemplate = "Extract an invoice from {Document} ({Locale})";
    static string? Validate(InvoiceParameters p) => p is null || string.IsNullOrWhiteSpace(p.Document) ? "Document is required" : null;
    static string? Validate(Invoice r) => r.Total < 0 || string.IsNullOrWhiteSpace(r.Currency) ? "Invalid invoice" : null;
}

public sealed record MappedParameters([property: JsonPropertyName("doc_text")] string Document, string? Locale = "en", [property: JsonIgnore] string? Secret = null);
public sealed record MappedInvoice([property: JsonPropertyName("total_due")] decimal? Total, string? Note = null, [property: JsonIgnore] string? Hidden = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MappedParameters)), JsonSerializable(typeof(MappedInvoice))]
public partial class MappedJsonContext : JsonSerializerContext;
[PromptCommand("invoice.mapped", Revision=2, Profile="extraction", JsonContext=typeof(MappedJsonContext))]
public sealed partial class MappedExtract : PromptCommand<MappedParameters,MappedInvoice> {
    const string SystemTemplate = "Extract structured data.";
    const string UserTemplate = "Read {Document} ({Locale})";
}
