using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd;

/// <summary>One lazy contract owns serialization, JSON names and schema. It never falls back to reflection.</summary>
public sealed class CommandJsonContract<TParameters, TResult>
{
    sealed record State(
        JsonTypeInfo<TParameters> Parameters,
        JsonTypeInfo<TResult> Result,
        JsonSerializerOptions Options
    );

    readonly Lazy<State> state;
    readonly Lazy<JsonElement> schema;
    readonly Lazy<bool> validated;

    /// <summary>Creates a lazy contract from AOT-safe serializer metadata and generated requiredness configuration.</summary>
    public CommandJsonContract(
        Func<IJsonTypeInfoResolver> context,
        Action<JsonTypeInfo> configure,
        params Type[] contractTypes
    )
    {
        state = new(() =>
        {
            var source = context();
            // STJ freezes options after first use. Apply generated requiredness
            // before resolving metadata, and reuse it for schema and reading so
            // the model is never told a field is optional when readers require it.
            // An authored STJ context owns its naming and converter policies.
            // The generated resolver instead starts with ordinary STJ defaults;
            // neither path enables a reflection resolver implicitly.
            var options = new JsonSerializerOptions(
                source is JsonSerializerContext authored
                    ? authored.Options
                    : new JsonSerializerOptions()
            )
            {
                RespectNullableAnnotations = true,
                TypeInfoResolver = source.WithAddedModifier(configure),
            };
            options.MakeReadOnly();
            return new(
                (JsonTypeInfo<TParameters>)options.GetTypeInfo(typeof(TParameters)),
                (JsonTypeInfo<TResult>)options.GetTypeInfo(typeof(TResult)),
                options
            );
        });
        schema = new(() =>
            AIJsonUtilities.CreateJsonSchema(
                typeof(TResult),
                serializerOptions: state.Value.Options
            )
        );
        validated = new(() =>
        {
            _ = Parameters;
            _ = Result;
            _ = Schema;
            foreach (var type in contractTypes)
            {
                _ = state.Value.Options.GetTypeInfo(type);
            }

            return true;
        });
    }

    /// <summary>Serializer metadata for input parameters.</summary>
    public JsonTypeInfo<TParameters> Parameters => state.Value.Parameters;

    /// <summary>Serializer metadata used to read and validate results.</summary>
    public JsonTypeInfo<TResult> Result => state.Value.Result;

    /// <summary>Result schema generated from the same serializer contract used by the reader.</summary>
    public JsonElement Schema => schema.Value;

    /// <summary>Resolves all required metadata and schema; throws if the contract is incomplete.</summary>
    public void Validate()
    {
        _ = validated.Value;
    }
}
