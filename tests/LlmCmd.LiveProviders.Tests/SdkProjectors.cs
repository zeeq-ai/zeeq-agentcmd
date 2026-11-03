using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Chat;
using Zeeq.LlmCmd.Providers;
using AnthropicMessage = Anthropic.Models.Messages.Message;

namespace LlmCmd.LiveProviders.Tests;

// SDK pins and serializers belong at the integration boundary. ProviderAdapters
// stays SDK-free, and its existing per-attempt middleware stays unchanged.
internal sealed class OpenAiSdkProjector : INativeResponseProjector
{
    public string Id => "live-tests.openai-2.14.0";
    public ApiKind Api => ApiKind.ChatCompletions;

    public bool CanProject(object native) => native is ChatCompletion;

    public JsonElement Project(object native)
    {
        using var document = JsonDocument.Parse(
            ModelReaderWriter.Write((ChatCompletion)native).ToMemory()
        );
        var root = document.RootElement;
        var selected = new Dictionary<string, object?>();
        if (root.TryGetProperty("usage", out var usage))
        {
            selected["usage"] = usage;
        }

        if (root.TryGetProperty("service_tier", out var tier))
        {
            selected["service_tier"] = tier;
        }

        if (root.TryGetProperty("choices", out var choices))
        {
            selected["choices"] = choices
                .EnumerateArray()
                .Select(choice =>
                    choice.TryGetProperty("finish_reason", out var reason)
                        ? new Dictionary<string, object?> { ["finish_reason"] = reason }
                        : new Dictionary<string, object?>()
                )
                .ToArray();
        }
        // Retain only accounting and stop metadata. Never persist SDK objects,
        // request headers, response text or tool arguments in capture artifacts.
        return JsonSerializer.SerializeToElement(selected);
    }
}

internal sealed class AnthropicSdkProjector : INativeResponseProjector
{
    public string Id => "live-tests.anthropic-13.3.0";
    public ApiKind Api => ApiKind.Messages;

    public bool CanProject(object native) => native is AnthropicMessage;

    public JsonElement Project(object native) =>
        JsonSerializer.SerializeToElement(
            ((AnthropicMessage)native)
                .RawData.Where(property => property.Key is "usage" or "stop_reason")
                .ToDictionary(property => property.Key, property => property.Value)
        );
}
