using System.Text.Json.Serialization;
using Zeeq.AgentCmd;

// Export must only build and read generated data. Startup deliberately fails.
throw new InvalidOperationException("Manifest export must not start this application.");

namespace ManifestExportFixture
{
    public sealed record InvoiceInput(
        string Document,
        [property: RuntimeOverride] string? Locale = null
    );

    public sealed record InvoiceOutput([property: JsonPropertyName("total")] decimal Total);

    public sealed partial class ExtractInvoice : PromptCommand<InvoiceInput, InvoiceOutput>
    {
        public const string Id = "invoice.extract";
        public const string Profile = "offline-fixture";
        public const string UserTemplate = "Extract {Document} in {Locale}";
    }

    [CompositeOutput("invoice.whole")]
    public sealed partial record InvoiceWhole(InvoiceOutput Header, string ReviewNote);
}
