using System.Text.Json;
using Microsoft.Extensions.AI;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;

namespace LlmCmd.ProviderAdapters.Tests;

static class Fx
{
    public static readonly ProviderIdentity OpenAi = new(
        "openai",
        "openai-main",
        ApiKind.ChatCompletions
    );
    public static readonly ProviderIdentity Anthropic = new(
        "anthropic",
        "anthropic-main",
        ApiKind.Messages
    );
    public static readonly ProviderIdentity DeepSeek = new(
        "deepseek",
        "deepseek-main",
        ApiKind.ChatCompletions
    );

    public static ProviderAdapterRegistry Registry(params INativeResponseProjector[] projectors) =>
        new(
            [new OpenAiChatAdapter(), new AnthropicMessagesAdapter(), new DeepSeekChatAdapter()],
            projectors
        );

    public static ProviderExtractor Extractor(
        ProviderExtractionOptions? options = null,
        params INativeResponseProjector[] projectors
    ) => new(Registry(projectors), options);

    public static JsonElement Json(string text)
    {
        using var d = JsonDocument.Parse(text);
        return d.RootElement.Clone();
    }

    /// <summary>Loads a synthetic fixture. The meai section builds the normalized layer; the wire section becomes RawRepresentation.</summary>
    public static (ChatResponse Response, JsonElement Wire, JsonElement Meta) Load(
        string name,
        bool withUsage = true,
        bool withRaw = true
    )
    {
        var root = Json(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json"))
        );
        var wire = root.GetProperty("wire");
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthetic answer"));
        if (withUsage)
        {
            response.Usage = Usage(root.GetProperty("meai"));
        }

        if (withRaw)
        {
            response.RawRepresentation = wire;
        }

        return (response, wire, root.GetProperty("fixture"));
    }

    static UsageDetails Usage(JsonElement m)
    {
        long? L(string n) => m.TryGetProperty(n, out var v) ? v.GetInt64() : null;
        var u = new UsageDetails
        {
            InputTokenCount = L("input"),
            OutputTokenCount = L("output"),
            TotalTokenCount = L("total"),
            CachedInputTokenCount = L("cached"),
            ReasoningTokenCount = L("reasoning"),
        };
        if (m.TryGetProperty("additional", out var add))
        {
            u.AdditionalCounts = [];
            foreach (var p in add.EnumerateObject())
            {
                u.AdditionalCounts[p.Name] = p.Value.GetInt64();
            }
        }
        return u;
    }

    public static ExtractionResult Run(
        string fixture,
        ProviderIdentity registration,
        bool withUsage = true,
        bool withRaw = true
    )
    {
        var (response, _, _) = Load(fixture, withUsage, withRaw);
        return Extractor().Extract(registration, response, "attempt-1");
    }

    public static string Status(ExtractionResult r) =>
        r switch
        {
            ExtractionResult.Extracted => "Extracted",
            ExtractionResult.Partial => "Partial",
            ExtractionResult.Invalid => "Invalid",
        };

    public static Origin[] Origins(UsageMeasurement? m) =>
        m is null ? [] : [.. m.Observations.Select(o => o.Origin)];

    public static string[] Codes(ExtractionResult r) =>
        [.. r.Snapshot.Diagnostics.Select(d => d.Code)];
}

sealed class CaptureSink : IExtractionSink
{
    readonly List<ExtractionResult> results = [];
    public IReadOnlyList<ExtractionResult> Results
    {
        get
        {
            lock (results)
            {
                return [.. results];
            }
        }
    }

    public ValueTask OnExtractedAsync(ExtractionResult result, CancellationToken cancellationToken)
    {
        lock (results)
        {
            results.Add(result);
        }

        return ValueTask.CompletedTask;
    }
}

sealed class ThrowingSink : IExtractionSink
{
    public ValueTask OnExtractedAsync(
        ExtractionResult result,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException("sink down");
}

sealed class ScriptedClient : IChatClient
{
    public Func<
        IEnumerable<ChatMessage>,
        ChatOptions?,
        CancellationToken,
        Task<ChatResponse>
    > Respond { get; set; } = (_, _, _) => throw new NotSupportedException();
    public Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Stream { get; set; } =
        _ => throw new NotSupportedException();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => Respond(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => Stream(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    public static async IAsyncEnumerable<ChatResponseUpdate> Updates(
        IEnumerable<ChatResponseUpdate> updates,
        Exception? thenThrow = null
    )
    {
        foreach (var u in updates)
        {
            await Task.Yield();
            yield return u;
        }
        if (thenThrow is not null)
        {
            throw thenThrow;
        }
    }
}
