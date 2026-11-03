using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using SerializationSpikes;

/// <summary>Tests the existing MEAI extension seam without replacing its client or options.</summary>
public static class MetadataMeaiProof
{
    public static async Task<int> Run()
    {
        var serializer = new MetadataSerializer();
        var expected = Conformance.Sample();
        var json = serializer.Serialize(expected);
        var schema = AIJsonUtilities.CreateJsonSchema(
            typeof(DocumentWhole),
            serializerOptions: serializer.Options
        );
        Require(
            schema.GetProperty("properties").TryGetProperty("header_data", out _),
            "MEAI schema lost aggregate fields."
        );

        using var cancellation = new CancellationTokenSource();
        using var client = new LocalClient(json, cancellation.Token);
        var result = await client.GetResponseAsync<DocumentWhole>(
            "Extract the local fixture.",
            serializer.Options,
            options: new ChatOptions { ModelId = "offline-fixture", MaxOutputTokens = 128 },
            useJsonSchemaResponseFormat: true,
            cancellationToken: cancellation.Token
        );

        Require(result.Result.Header.Number == "INV-42", "Typed MEAI response differs.");
        Require(
            result.Result.Lines.Sum(line => line.Amount * line.Quantity)
                == result.Result.Totals["net"],
            "Whole eval invariant differs."
        );
        Require(client.Calls == 1, "Typed readback caused another model request.");

        var named = new MetadataSerializer();
        named.Options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        using var renamed = JsonDocument.Parse(named.Serialize(new DocumentParameters("x")));
        Require(
            renamed.RootElement.TryGetProperty("document", out _),
            "Host naming policy was rejected."
        );
        // The explicit member override wins while ordinary multiword members
        // use the host naming policy. This is wire compatibility, not style.
        using var namedHeader = JsonDocument.Parse(named.Serialize(expected.Header));
        Require(
            namedHeader.RootElement.TryGetProperty("document_id", out _),
            "Host naming policy was ignored."
        );
        Require(
            namedHeader.RootElement.TryGetProperty("document_number", out _),
            "Explicit JSON name was ignored."
        );

        var withConverter = new MetadataSerializer();
        withConverter.Options.Converters.Add(new JsonStringEnumConverter<LineKind>());
        var rejected = false;
        try
        {
            _ = withConverter.Serialize(expected);
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }

        Require(rejected, "Options converters must be honored or explicitly rejected.");
        Require(!JsonSerializer.IsReflectionEnabledByDefault, "Reflection must stay disabled.");
        Console.WriteLine(
            "MEAI metadata proof passed: real schema and typed response helpers, one fake request, host naming, explicit converter rejection, no reflection."
        );
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class LocalClient(string json, CancellationToken expectedCancellation)
        : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Require(cancellationToken == expectedCancellation, "MEAI lost cancellation.");
            Require(
                options?.ModelId == "offline-fixture" && options.MaxOutputTokens == 128,
                "MEAI lost host options."
            );
            Require(
                options?.ResponseFormat is ChatResponseFormatJson,
                "MEAI did not request a JSON schema."
            );
            Require(messages.Any(), "Prompt was lost.");
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            throw new NotSupportedException("Streaming is outside this serializer spike.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose() { }
    }
}
