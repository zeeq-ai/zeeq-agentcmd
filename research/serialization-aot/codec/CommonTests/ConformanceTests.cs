using SerializationSpikes;

public sealed class ConformanceTests
{
    [Test]
    public async Task SharedOracleChecksPass()
    {
        var failures = Conformance
            .Evaluate(new CodecSerializer())
            .Where(result => !result.Passed)
            .ToArray();
        await Assert
            .That(
                string.Join("\n", failures.Select(failure => $"{failure.Name}: {failure.Detail}"))
            )
            .IsEqualTo("");
    }
}
