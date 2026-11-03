using SerializationSpikes;

/// <summary>Exercises the same stored-whole contract used by the native smoke runner.</summary>
public sealed class ConformanceTests
{
    [Test]
    public async Task SharedContractPasses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "metadata-tunit-" + Guid.NewGuid());
        var result = Conformance.Run(new MetadataSerializer(), ["--report-dir=" + directory]);
        await Assert.That(result).IsEqualTo(0);
    }
}

/// <summary>Checks host customization at the exposed options boundary.</summary>
public sealed class HostOptionsTests
{
    [Test]
    public async Task NamingPolicyIsUsed()
    {
        var serializer = new MetadataSerializer();
        serializer.Options.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseUpper;
        var json = serializer.Serialize(new DocumentParameters("x"));
        await Assert.That(json.Contains("\"DOCUMENT\"")).IsTrue();
    }

    [Test]
    public async Task OptionsConverterIsRejectedAtResolverBoundary()
    {
        var serializer = new MetadataSerializer();
        serializer.Options.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter<LineKind>()
        );
        await Assert
            .That(() => serializer.Options.GetTypeInfo(typeof(DocumentParameters)))
            .Throws<NotSupportedException>();
    }
}
