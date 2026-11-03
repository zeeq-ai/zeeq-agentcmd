using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace CommandRoot;

/// <summary>One lazy contract owns serialization, JSON names and schema. It never falls back to reflection.</summary>
public sealed class CommandJsonContract<TParameters,TResult>
{
    sealed record State(JsonTypeInfo<TParameters> Parameters,JsonTypeInfo<TResult> Result,JsonSerializerOptions Options);
    readonly Lazy<State> state;
    readonly Lazy<JsonElement> schema;
    readonly Lazy<bool> validated;
    public CommandJsonContract(Func<JsonSerializerContext> context,Action<JsonTypeInfo> configure,params Type[] contractTypes)
    {
        state=new(() =>
        {
            var source=context();
            var options=new JsonSerializerOptions(source.Options)
            {
                RespectNullableAnnotations=true,
                TypeInfoResolver=source.WithAddedModifier(configure)
            };
            options.MakeReadOnly();
            return new((JsonTypeInfo<TParameters>)options.GetTypeInfo(typeof(TParameters)),
                (JsonTypeInfo<TResult>)options.GetTypeInfo(typeof(TResult)),options);
        });
        schema=new(()=>AIJsonUtilities.CreateJsonSchema(typeof(TResult),serializerOptions:state.Value.Options));
        validated=new(() =>
        {
            _=Parameters; _=Result; _=Schema;
            foreach(var type in contractTypes) _=state.Value.Options.GetTypeInfo(type);
            return true;
        });
    }
    public JsonTypeInfo<TParameters> Parameters => state.Value.Parameters;
    public JsonTypeInfo<TResult> Result => state.Value.Result;
    public JsonElement Schema => schema.Value;
    public void Validate() { _=validated.Value; }
}
