using System.Text.Json;
using Microsoft.Extensions.AI;
using SerializationSpikes;

/// <summary>
/// Exercises the same standard MEAI request boundary as the library. The codec
/// owns schema and reading, so it does not need to replace the host's client.
/// </summary>
public static class CodecMeaiProof
{
    public static async Task<int> Run()
    {
        var codec = new CodecSerializer();
        var sample = Conformance.Sample();
        var schema = codec.Schema<DocumentWhole>();
        Require(
            schema.GetProperty("properties").TryGetProperty("header_data", out _),
            "Codec schema is opaque."
        );

        using var cancellation = new CancellationTokenSource();
        using var fixture = new LocalClient(codec.Serialize(sample), cancellation.Token);
        using var middleware = new HostMiddleware(fixture);
        var options = new ChatOptions
        {
            ModelId = "offline-fixture",
            MaxOutputTokens = 128,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "document"),
        };

        var response = await middleware.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Extract the local fixture.")],
            options,
            cancellation.Token
        );
        var result = codec.Deserialize<DocumentWhole>(response.Text);
        Require(result.Header.Number == "INV-42", "Codec response changed.");
        Require(
            result.Totals["net"] == result.Lines.Sum(line => line.Amount * line.Quantity),
            "Whole invariant changed."
        );
        Require(
            middleware.Calls == 1 && fixture.Calls == 1,
            "Host middleware was bypassed or another request ran."
        );
        Require(!JsonSerializer.IsReflectionEnabledByDefault, "Reflection must remain disabled.");

        Console.WriteLine(
            "MEAI codec proof passed: generated schema through ForJsonSchema, host DelegatingChatClient, preserved options/cancellation, one fake request, typed whole readback, no reflection."
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

    private sealed class HostMiddleware(IChatClient inner) : DelegatingChatClient(inner)
    {
        public int Calls { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            return await base.GetResponseAsync(messages, options, cancellationToken);
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
            Require(cancellationToken == expectedCancellation, "Cancellation was lost.");
            Require(
                options?.ModelId == "offline-fixture" && options.MaxOutputTokens == 128,
                "Host options were lost."
            );
            Require(
                options?.ResponseFormat is ChatResponseFormatJson,
                "Schema response format was lost."
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
