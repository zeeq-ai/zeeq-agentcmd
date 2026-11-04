using System.Buffers;
using System.Text;
using System.Text.Json;
using CompositionDemo;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Zeeq.AgentCmd;

/// <summary>Exercises generated metadata through library, STJ and MEAI entry points in native code.</summary>
internal static class NativeProof
{
    public static async Task RunAsync(
        IServiceProvider services,
        CompositeDescriptor<DocumentExtraction> descriptor,
        string storedWhole
    )
    {
        var explicitHeader = new CommandJsonContract<Header, Header>(
            () => ExplicitHeaderContext.Default,
            static info =>
            {
                if (info.Type == typeof(Header))
                {
                    foreach (var property in info.Properties)
                    {
                        property.IsRequired = true;
                    }
                }
            }
        );
        explicitHeader.Validate();
        Require(
            ContractCompatibility.Compatible(
                descriptor.Whole.Options.GetTypeInfo(typeof(Header)),
                explicitHeader.Result
            ),
            "An authored STJ wrapper was rejected despite equivalent generated metadata."
        );

        var shapes = await services
            .GetRequiredService<ShapeCommand>()
            .WithParameters(new ShapeInput("document"))
            .ExecuteAsync();
        Require(
            shapes is Outcome<ShapeOutput>.Success { Value.Label.Value: null } ok
                && ok.Value.Items.Single().Description == "line"
                && ok.Value.Totals["net"] == 12.50m
                && ok.Value.Codes.SequenceEqual([7, 11])
                && ok.Value.Code.Value == "CODE-X",
            "Nested, generic or collection metadata did not round-trip."
        );

        var command = services.GetRequiredService<OverrideCommand>();
        var rejectedInput = await command.WithParameters(new OverrideInput("")).ExecuteAsync();
        Require(
            rejectedInput
                is Outcome<OverrideOutput>.Failed
                {
                    Failure: ExecutionFailure.InvalidInput { Reason: "Document is required" },
                },
            "The native interface input validator did not run."
        );
        var rejectedResult = await command
            .WithParameters(new OverrideInput("reject-result"))
            .ExecuteAsync();
        Require(
            rejectedResult
                is Outcome<OverrideOutput>.Failed
                {
                    Failure: ExecutionFailure.InvalidOutput { Reason: "Receipt total is negative" },
                },
            "The native interface result validator did not run."
        );
        var outcome = await command.WithParameters(new OverrideInput("document")).ExecuteAsync();
        Require(
            outcome is Outcome<OverrideOutput>.Success { Value.ReceiptTotal: 42 } explicitResult
                && explicitResult.Value.Extra?.TryGetValue("future", out var extension) == true
                && extension.GetInt32() == 7,
            "Explicit serializer context was not used."
        );

        var bytes = Encoding.UTF8.GetBytes(storedWhole);
        var split = bytes.Length / 2;
        var first = new ByteSegment(bytes.AsMemory(0, split));
        var last = first.Append(bytes.AsMemory(split));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var reader = new Utf8JsonReader(sequence);
        var read = JsonSerializer.Deserialize(ref reader, descriptor.Whole);
        Require(
            read?.Header.Number == "INV-1" && read.Header.Kind == InvoiceKind.Invoice,
            "Segmented JSON or native enum metadata did not round-trip."
        );

        using var stream = new MemoryStream(bytes, writable: false);
        var streamed = await JsonSerializer.DeserializeAsync(stream, descriptor.Whole);
        Require(streamed?.Totals.Amount == 12.50m, "Streamed JSON did not round-trip.");

        Reject("{", descriptor);
        Reject("{}", descriptor);
        var invalidCount = storedWhole.Replace(
            "\"Count\":2",
            "\"Count\":\"bad\"",
            StringComparison.Ordinal
        );
        Require(
            invalidCount != storedWhole,
            "The hostile value probe did not change the stored JSON."
        );
        Reject(invalidCount, descriptor);
        var exceededDepth = false;
        try
        {
            var shallowReader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 1 });
            _ = JsonSerializer.Deserialize(ref shallowReader, descriptor.Whole);
        }
        catch (JsonException)
        {
            exceededDepth = true;
        }
        Require(exceededDepth, "Reader depth limit was not enforced.");

        using var cts = new CancellationTokenSource();
        using var client = new WholeClient(storedWhole, cts.Token);
        var result = await client.GetResponseAsync<DocumentExtraction>(
            "Return the stored whole.",
            descriptor.Whole.Options,
            options: new ChatOptions { ModelId = "native-proof", MaxOutputTokens = 128 },
            useJsonSchemaResponseFormat: true,
            cancellationToken: cts.Token
        );
        Require(
            result.Result.Header.Number == "INV-1"
                && result.Result.Totals.Amount == 12.50m
                && client.Calls == 1,
            "MEAI typed helper failed."
        );
        Console.WriteLine(
            "Native package metadata, explicit context, segmented/stream JSON and MEAI typed response passed."
        );
    }

    private static void Reject(string json, CompositeDescriptor<DocumentExtraction> descriptor)
    {
        try
        {
            _ = JsonSerializer.Deserialize(json, descriptor.Whole);
        }
        catch (JsonException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid JSON was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ByteSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSegment(ReadOnlyMemory<byte> bytes)
        {
            Memory = bytes;
        }

        public ByteSegment Append(ReadOnlyMemory<byte> next)
        {
            var segment = new ByteSegment(next) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }

    private sealed class WholeClient(string json, CancellationToken expected) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Require(
                cancellationToken == expected
                    && options?.ModelId == "native-proof"
                    && options.MaxOutputTokens == 128,
                "MEAI lost options or cancellation."
            );
            Require(
                options?.ResponseFormat is ChatResponseFormatJson { Schema: not null },
                "MEAI schema was not requested."
            );
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
