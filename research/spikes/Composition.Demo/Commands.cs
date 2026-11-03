using CommandRoot;
using System.Text.Json.Serialization;
namespace CompositionDemo;

// The CLR names select the generated methods. The JSON name is separately
// resolved from the active serializer contract when the aggregate is assembled.
[CompositeOutput("invoice.composite", Revision=1)]
public sealed partial record DocumentExtraction(
    [property: JsonPropertyName("header_data")] Header Header,
    Parties Parties,
    LineItems LineItems,
    Totals Totals);

public sealed record Header(string Number);
public sealed record Parties(string Seller);
public sealed record LineItems(int Count);
public sealed record Totals(decimal Amount);
public sealed record SourceText(string Document);
public sealed record TotalsParameters(string Document, int LineCount);

public sealed partial class ExtractHeader : PromptCommand<SourceText,Header>
{ public const string Id="example.header"; public const string UserTemplate="HEADER {Document}"; }
public sealed partial class ExtractParties : PromptCommand<SourceText,Parties>
{ public const string Id="example.parties"; public const string UserTemplate="PARTIES {Document}"; }
public sealed partial class ExtractLineItems : PromptCommand<SourceText,LineItems>
{ public const string Id="example.lines"; public const string UserTemplate="LINES {Document}"; }
public sealed partial class ExtractQuickLineItems : PromptCommand<SourceText,LineItems>
{ public const string Id="example.lines.quick"; public const string UserTemplate="QUICK {Document}"; }
public sealed partial class ExtractTotals : PromptCommand<TotalsParameters,Totals>
{ public const string Id="example.totals"; public const string UserTemplate="TOTALS {Document} {LineCount}"; }
