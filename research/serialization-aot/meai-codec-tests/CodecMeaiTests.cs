namespace SerializationSpikes.IntegrationTests;

public sealed class CodecMeaiTests
{
    [Test]
    public async Task Generated_codec_works_with_standard_MEAI_client_and_schema()
    {
        await Assert.That(await CodecMeaiProof.Run()).IsEqualTo(0);
    }
}
