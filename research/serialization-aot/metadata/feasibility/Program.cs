using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

var options = new JsonSerializerOptions { TypeInfoResolver = new Resolver() };
var typeInfo = (JsonTypeInfo<Example>)options.GetTypeInfo(typeof(Example));
var json = JsonSerializer.Serialize(new Example { Name = "ok" }, typeInfo);
var value = JsonSerializer.Deserialize(json, typeInfo);
var schema = options.GetJsonSchemaAsNode(typeof(Example));
if (value?.Name != "ok" || schema is null)
{
    throw new InvalidOperationException("Metadata smoke failed.");
}

Console.WriteLine("Metadata roundtrip and schema passed.");

sealed class Example
{
    public string Name { get; set; } = "";
}

sealed class Resolver : IJsonTypeInfoResolver
{
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(string))
        {
            return JsonMetadataServices.CreateValueInfo<string>(
                options,
                JsonMetadataServices.StringConverter
            );
        }

        if (type == typeof(Example))
        {
            return JsonMetadataServices.CreateObjectInfo(
                options,
                new JsonObjectInfoValues<Example>
                {
                    ObjectCreator = static () => new Example(),
                    PropertyMetadataInitializer = _ =>
                        [
                            JsonMetadataServices.CreatePropertyInfo(
                                options,
                                new JsonPropertyInfoValues<string>
                                {
                                    IsProperty = true,
                                    IsPublic = true,
                                    DeclaringType = typeof(Example),
                                    PropertyName = nameof(Example.Name),
                                    Getter = static obj => ((Example)obj).Name,
                                    Setter = static (obj, value) => ((Example)obj).Name = value!,
                                }
                            ),
                        ],
                }
            );
        }

        return null;
    }
}
