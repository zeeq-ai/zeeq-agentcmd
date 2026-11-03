using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerializationSpikes;

/// <summary>Discovery stubs keep the spike independent of production build tooling.</summary>
public abstract class PromptCommand<TParameters, TResult>;

/// <summary>Aggregate discovery must work without a leaf command for the whole type.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class CompositeOutputAttribute : Attribute;

public sealed partial class ExtractDocument : PromptCommand<DocumentParameters, Header>;

public sealed partial class ExtractLines : PromptCommand<DocumentParameters, Line[]>;

public sealed record DocumentParameters(string Document, string Locale = "en");

public sealed record Header(
    [property: JsonPropertyName("document_number")] string Number,
    Address Address,
    DateOnly IssuedOn,
    Guid DocumentId,
    string? Note = null
);

public sealed record Address(
    string City,
    [property: JsonPropertyName("postal_code")] string PostalCode
);

[JsonConverter(typeof(JsonStringEnumConverter<LineKind>))]
public enum LineKind
{
    Goods,
    Service,
}

public sealed record Line(string Description, decimal Amount, LineKind Kind, int Quantity = 1);

public sealed record Box<TValue>(TValue Value);

/// <summary>Init-only members exercise construction, not reflection-based mutation.</summary>
public sealed class ReviewState
{
    public required string Reviewer { get; init; }

    public int Revision { get; init; } = 1;

    [JsonIgnore]
    public string LocalOnly => "not-on-the-wire";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(PersonParty), "person")]
[JsonDerivedType(typeof(CompanyParty), "company")]
public abstract record Party(string Name);

public sealed record PersonParty(string Name, int Age) : Party(Name);

public sealed record CompanyParty(string Name, string Registration) : Party(Name);

/// <summary>A concrete converter must remain an explicit, AOT-safe extension seam.</summary>
[JsonConverter(typeof(DocumentCodeConverter))]
public readonly record struct DocumentCode(string Value);

public sealed class DocumentCodeConverter : JsonConverter<DocumentCode>
{
    public override DocumentCode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var value = reader.GetString();
        if (value is null || !value.StartsWith("DOC-", StringComparison.Ordinal))
        {
            throw new JsonException("Document codes must use the DOC- prefix.");
        }

        return new DocumentCode(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DocumentCode value,
        JsonSerializerOptions options
    )
    {
        writer.WriteStringValue(value.Value);
    }
}

/// <summary>The whole is a separate root: evals read its stored value, not component traces.</summary>
[CompositeOutput]
public sealed record DocumentWhole(
    [property: JsonPropertyName("header_data")] Header Header,
    IReadOnlyList<Line> Lines,
    Dictionary<string, decimal> Totals,
    Box<string?> OptionalLabel,
    Box<Address> Destination,
    ReviewState Review,
    Party Party,
    ImmutableArray<int> Codes,
    DocumentCode Code,
    int Count = 3
);
