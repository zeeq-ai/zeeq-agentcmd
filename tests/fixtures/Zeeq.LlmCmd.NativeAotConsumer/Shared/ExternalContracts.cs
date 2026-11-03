using System.Text.Json;
using System.Text.Json.Serialization;
using Zeeq.LlmCmd;

namespace ExternalNative;

/// <summary>Input to the separately compiled generated leaf.</summary>
public sealed record ExternalInput(string Document);

/// <summary>A converted scalar shared by the leaf and consumer whole.</summary>
[JsonConverter(typeof(NativeCodeConverter))]
public readonly record struct NativeCode(string Value);

/// <summary>Reads the bounded local code format.</summary>
public sealed class NativeCodeConverter : JsonConverter<NativeCode>
{
    private JsonSerializerOptions? owner;

    /// <inheritdoc />
    public override NativeCode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        CheckOwner(options);
        var text = reader.GetString();
        if (text is null || !text.StartsWith("CODE-", StringComparison.Ordinal))
        {
            throw new JsonException("Invalid local code.");
        }

        return new NativeCode(text);
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        NativeCode value,
        JsonSerializerOptions options
    )
    {
        CheckOwner(options);
        writer.WriteStringValue(value.Value);
    }

    private void CheckOwner(JsonSerializerOptions options)
    {
        // This intentionally options-bound converter exposes accidental sharing
        // between independent generated contracts without using private state.
        var seen = Interlocked.CompareExchange(ref owner, options, null);
        if (seen is not null && !ReferenceEquals(seen, options))
        {
            throw new InvalidOperationException(
                "Converter instance crossed serializer option scopes."
            );
        }
    }
}

/// <summary>Output part generated in a different assembly from its whole.</summary>
public sealed record CodePart(NativeCode Code);

/// <summary>A generated command declared in the external DTO assembly.</summary>
public sealed partial class ExternalCodeCommand : PromptCommand<ExternalInput, CodePart>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "native.external-code";

    /// <summary>Prompt understood by the offline client.</summary>
    public const string UserTemplate = "CODE {Document}";
}
