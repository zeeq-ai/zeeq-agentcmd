using System.Text.Json.Serialization;
using ExternalNative;
using Zeeq.AgentCmd;

namespace CompositionDemo;

// The CLR names select the generated methods. The JSON name is separately
// resolved from the active serializer contract when the aggregate is assembled.
/// <summary>Whole invoice assembled from independently stored component versions.</summary>
[CompositeOutput("invoice.composite", Revision = 1)]
public sealed partial record DocumentExtraction(
    [property: JsonPropertyName("header_data")] Header Header,
    Parties Parties,
    LineItems LineItems,
    Totals Totals,
    CodePart Code
);

/// <summary>Invoice identity extracted by the header pass.</summary>
public sealed record Header(string Number, InvoiceKind Kind);

/// <summary>Exercises enum preflight and wire comparison in the native runtime.</summary>
public enum InvoiceKind
{
    /// <summary>A normal invoice.</summary>
    Invoice = 1,

    /// <summary>A credit note.</summary>
    Credit = 2,
}

/// <summary>Seller extracted by the parties pass.</summary>
public sealed record Parties(string Seller);

/// <summary>Line count extracted by the selected line-item pass.</summary>
public sealed record LineItems(int Count);

/// <summary>Invoice total extracted after line items.</summary>
public sealed record Totals(decimal Amount);

/// <summary>Source document passed to an extraction command.</summary>
public sealed record SourceText(string Document);

/// <summary>Source document and pinned line count for the dependent pass.</summary>
public sealed record TotalsParameters(string Document, int LineCount);

/// <summary>Declares the header extraction command.</summary>
public sealed partial class ExtractHeader : PromptCommand<SourceText, Header>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "example.header";

    /// <summary>Prompt template rendered from the typed parameters.</summary>
    public const string UserTemplate = "HEADER {Document}";
}

/// <summary>Declares the seller extraction command.</summary>
public sealed partial class ExtractParties : PromptCommand<SourceText, Parties>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "example.parties";

    /// <summary>Prompt template rendered from the typed parameters.</summary>
    public const string UserTemplate = "PARTIES {Document}";
}

/// <summary>Declares the detailed line-item extraction command.</summary>
public sealed partial class ExtractLineItems : PromptCommand<SourceText, LineItems>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "example.lines";

    /// <summary>Prompt template rendered from the typed parameters.</summary>
    public const string UserTemplate = "LINES {Document}";
}

/// <summary>Declares an alternate line-item extraction command.</summary>
public sealed partial class ExtractQuickLineItems : PromptCommand<SourceText, LineItems>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "example.lines.quick";

    /// <summary>Prompt template rendered from the typed parameters.</summary>
    public const string UserTemplate = "QUICK {Document}";
}

/// <summary>Declares the totals pass that depends on the stored line count.</summary>
public sealed partial class ExtractTotals : PromptCommand<TotalsParameters, Totals>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "example.totals";

    /// <summary>Prompt template rendered from the typed parameters.</summary>
    public const string UserTemplate = "TOTALS {Document} {LineCount}";
}
