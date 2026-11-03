namespace SerializationSpikes.IntegrationTests;

public sealed class MetadataMeaiTests
{
    [Test]
    public async Task Generated_metadata_works_with_existing_MEAI_helpers()
    {
        await Assert.That(await MetadataMeaiProof.Run()).IsEqualTo(0);
    }
}
