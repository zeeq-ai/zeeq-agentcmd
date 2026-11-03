using System.Collections.Immutable;
using System.Text.Json;
using SerializationSpikes;

public sealed class CodecTests
{
    [Test]
    public async Task RoundTripAndDefaults()
    {
        var codec = new CodecSerializer();
        var original = new Reply(
            "hello",
            new Request("Ada", 4),
            new Box<string?>(null),
            12.5m,
            new DateOnly(2026, 1, 2),
            Guid.Empty
        );
        await Assert.That(codec.Deserialize<Reply>(codec.Serialize(original))).IsEqualTo(original);
        await Assert
            .That(codec.Deserialize<Request>("{\"display_name\":\"Ada\"}").Count)
            .IsEqualTo(3);
    }

    [Test]
    public async Task ArrayRoundTrip()
    {
        var codec = new CodecSerializer();
        var item = new Reply(
            "hi",
            new Request("Ada"),
            new Box<string?>(null),
            4m,
            new DateOnly(2026, 1, 2),
            Guid.Empty
        );
        var values = codec.Deserialize<Reply[]>(codec.Serialize(new[] { item }));
        await Assert.That(values.Single()).IsEqualTo(item);
    }

    [Test]
    public async Task CollectionShapesRoundTrip()
    {
        var codec = new CodecSerializer();
        var input = new CollectionData(
            [new Request("Ada")],
            new() { ["net"] = 12.5m },
            ImmutableArray.Create(1, 2)
        );
        var value = codec.Deserialize<CollectionData>(codec.Serialize(input));
        await Assert.That(value.Items.Single()).IsEqualTo(input.Items.Single());
        await Assert.That(value.Totals["net"]).IsEqualTo(12.5m);
        await Assert.That(value.Codes.SequenceEqual(input.Codes)).IsTrue();
    }

    [Test]
    public async Task ConverterAndEnumRoundTrip()
    {
        var codec = new CodecSerializer();
        var input = new ExtendedData(new Code("C-1"), Kind.Second);
        await Assert.That(codec.Deserialize<ExtendedData>(codec.Serialize(input))).IsEqualTo(input);
        await Assert
            .That(() => codec.Deserialize<ExtendedData>("{\"code\":\"bad\",\"kind\":\"Second\"}"))
            .Throws<JsonException>();
        await Assert
            .That(() => codec.Deserialize<ExtendedData>("{\"code\":\"C-1\",\"kind\":\"missing\"}"))
            .Throws<JsonException>();
        await Assert
            .That(
                codec.Schema<ExtendedData>().GetProperty("properties").GetProperty("code").ValueKind
                    == JsonValueKind.True
            )
            .IsTrue();
    }

    [Test]
    public async Task RequiredInitAndDefaults()
    {
        var codec = new CodecSerializer();
        var value = codec.Deserialize<Review>("{\"reviewer\":\"Ada\"}");
        await Assert.That(value.Revision).IsEqualTo(1);
        await Assert.That(() => codec.Deserialize<Review>("{}")).Throws<JsonException>();
        await Assert
            .That(() => codec.Deserialize<Review>("{\"reviewer\":null}"))
            .Throws<JsonException>();
        await Assert
            .That(codec.Serialize(value).Contains("localOnly", StringComparison.Ordinal))
            .IsFalse();
    }

    [Test]
    public async Task LastDuplicateAndEscapedName()
    {
        var codec = new CodecSerializer();
        var value = codec.Deserialize<Request>(
            "{\"display_name\":\"first\",\"display_\\u006eame\":\"last\"}"
        );
        await Assert.That(value.Name).IsEqualTo("last");
    }

    [Test]
    public async Task InvalidScalarValuesFailAsJsonExceptions()
    {
        var codec = new CodecSerializer();
        await Assert
            .That(() => codec.Deserialize<Request>("{\"display_name\":7}"))
            .Throws<JsonException>();
        await Assert
            .That(() => codec.Deserialize<Request>("{\"display_name\":\"x\",\"count\":2147483648}"))
            .Throws<JsonException>();
        await Assert
            .That(() => codec.Deserialize<Request>("{\"display_name\":null}"))
            .Throws<JsonException>();
    }

    [Test]
    public async Task UnknownAndMissingFail()
    {
        var codec = new CodecSerializer();
        await Assert
            .That(() => codec.Deserialize<Request>("{\"display_name\":\"Ada\",\"extra\":1}"))
            .Throws<JsonException>();
        await Assert.That(() => codec.Deserialize<Request>("{}")).Throws<JsonException>();
    }
}
