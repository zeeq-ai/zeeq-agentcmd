using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerializationSpikes;

public abstract class PromptCommand<TParameters, TResult>;

public sealed class Greeting : PromptCommand<Request, Reply>;

public sealed class ManyGreetings : PromptCommand<Request, Reply[]>;

public sealed class CollectionsCommand : PromptCommand<Request, CollectionData>;

public sealed record CollectionData(
    IReadOnlyList<Request> Items,
    Dictionary<string, decimal> Totals,
    ImmutableArray<int> Codes
);

public sealed record Request(
    [property: JsonPropertyName("display_name")] string Name,
    int Count = 3
);

public sealed record Box<TValue>(TValue Value);

public sealed record Reply(
    string Message,
    Request Request,
    Box<string?> Label,
    decimal Amount,
    DateOnly Issued,
    Guid Id
);

public static class Program
{
    public static int Main()
    {
        ISpikeSerializer codec = new CodecSerializer();
        var input = new Reply(
            "hello",
            new Request("Ada"),
            new Box<string?>(null),
            12.5m,
            new DateOnly(2026, 1, 2),
            Guid.Empty
        );
        var json = codec.Serialize(input);
        var copy = codec.Deserialize<Reply>(json);
        if (copy != input || codec.Deserialize<Request>("{\"display_name\":\"Ada\"}").Count != 3)
        {
            return 1;
        }

        try
        {
            codec.Deserialize<Request>("{\"display_name\":\"Ada\",\"extra\":1}");
            return 2;
        }
        catch (JsonException) { }

        if (
            codec
                .Schema<Request>()
                .GetProperty("properties")
                .GetProperty("display_name")
                .GetProperty("type")
                .GetString() != "string"
        )
        {
            return 3;
        }

        var collections = new CollectionData(
            [new Request("Ada")],
            new() { ["net"] = 12.5m },
            [1, 2]
        );
        var restored = codec.Deserialize<CollectionData>(codec.Serialize(collections));
        if (
            restored.Items[0] != collections.Items[0]
            || restored.Totals["net"] != 12.5m
            || !restored.Codes.SequenceEqual(collections.Codes)
        )
        {
            return 4;
        }
        var extended = new ExtendedData(new Code("C-1"), Kind.Second);
        if (codec.Deserialize<ExtendedData>(codec.Serialize(extended)) != extended)
        {
            return 5;
        }
        var review = codec.Deserialize<Review>("{\"reviewer\":\"Ada\"}");
        if (
            review.Reviewer != "Ada"
            || review.Revision != 1
            || codec.Serialize(review).Contains("localOnly", StringComparison.Ordinal)
        )
        {
            return 6;
        }
        Console.WriteLine("Codec subset smoke passed");
        return 0;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<Kind>))]
public enum Kind
{
    First,
    Second,
}

[JsonConverter(typeof(CodeConverter))]
public readonly record struct Code(string Value);

public sealed class CodeConverter : JsonConverter<Code>
{
    public override Code Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        var value = reader.GetString();
        return value is not null && value.StartsWith("C-", StringComparison.Ordinal)
            ? new Code(value)
            : throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, Code value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed record ExtendedData(Code Code, Kind Kind);

public sealed class ExtendedCommand : PromptCommand<Request, ExtendedData>;

public sealed class Review
{
    public required string Reviewer { get; init; }
    public int Revision { get; init; } = 1;

    [JsonIgnore]
    public string LocalOnly => "local";
}

public sealed class ReviewCommand : PromptCommand<Request, Review>;
