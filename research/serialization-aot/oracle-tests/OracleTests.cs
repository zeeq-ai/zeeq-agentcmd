using SerializationSpikes;

namespace SerializationSpikes.Tests;

/// <summary>The independent oracle must satisfy every explicit expectation before comparison.</summary>
public sealed class OracleTests
{
    [Test]
    public async Task Source_generated_oracle_satisfies_the_common_corpus()
    {
        var results = Conformance.Evaluate(new OracleSerializer());
        var failures = results.Where(result => !result.Passed).ToArray();

        await Assert.That(failures.Length).IsEqualTo(0);
    }
}
