using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CommandRoot;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
namespace LlmCmd.Composition.Tests;

// What the contract preflight must see BEFORE a part is paid for: anything that changes the stored wire JSON the whole will read back.
public class ContractCompatibilityUnitTests
{
    public enum Level { Low, High }
    public sealed record Inner(string ValueText, Level Level);
    public sealed record Outer([property: JsonPropertyName("child")] Inner Child, List<Inner> Many, Dictionary<string, Inner> ByName, Level Mode);

    static JsonTypeInfo Info(JsonSerializerOptions options, Type type)
    {
        if (!options.IsReadOnly)
        {
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
            options.MakeReadOnly(populateMissingResolver: true);
        }
        return options.GetTypeInfo(type);
    }

    static JsonSerializerOptions Plain() => new();
    static JsonSerializerOptions Camel() => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Test]
    public async Task Two_separately_built_identical_options_are_compatible()
        => await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Outer)), Info(Plain(), typeof(Outer)))).IsTrue();

    [Test]
    public async Task A_naming_policy_difference_at_the_top_level_is_incompatible()
        => await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Outer)), Info(Camel(), typeof(Outer)))).IsFalse();

    [Test]
    public async Task A_difference_that_only_exists_inside_a_nested_object_is_found()
    {
        // The explicit [JsonPropertyName] pins the outer name under both policies, so only Inner's property names differ.
        var plain = Info(Plain(), typeof(Inner)); var camel = Info(Camel(), typeof(Inner));
        await Assert.That(ContractCompatibility.Compatible(plain, camel)).IsFalse();
        var onlyChild = new JsonSerializerOptions();
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Wrapper)), Info(Camel(), typeof(Wrapper)))).IsFalse();
        _ = onlyChild;
    }
    public sealed record Wrapper([property: JsonPropertyName("child")] Inner Child);

    [Test]
    public async Task A_string_enum_converter_on_one_side_is_incompatible()
    {
        var names = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Inner)), Info(names, typeof(Inner)))).IsFalse();
    }

    [Test]
    public async Task Enums_written_with_different_naming_are_incompatible_and_equal_settings_are_compatible()
    {
        var c = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var d = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var same = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        await Assert.That(ContractCompatibility.Compatible(Info(c, typeof(Inner)), Info(d, typeof(Inner)))).IsFalse();
        await Assert.That(ContractCompatibility.Compatible(Info(c, typeof(Inner)), Info(same, typeof(Inner)))).IsTrue(); // two instances, same wire
    }

    // A custom converter's own state cannot be read, so two instances are never assumed equal; the same instance is.
    public sealed class Upper : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions o) => writer.WriteStringValue(value.ToUpperInvariant());
    }

    [Test]
    public async Task Unknown_custom_converter_instances_are_rejected_unless_they_are_the_same_instance()
    {
        var shared = new Upper();
        var a = new JsonSerializerOptions { Converters = { shared } }; var b = new JsonSerializerOptions { Converters = { shared } };
        var c = new JsonSerializerOptions { Converters = { new Upper() } };
        await Assert.That(ContractCompatibility.Compatible(Info(a, typeof(Inner)), Info(b, typeof(Inner)))).IsTrue();
        await Assert.That(ContractCompatibility.Compatible(Info(a, typeof(Inner)), Info(c, typeof(Inner)))).IsFalse();
    }

    [Test]
    public async Task Numbers_written_as_strings_on_one_side_are_incompatible()
    {
        var strings = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.WriteAsString };
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Money)), Info(strings, typeof(Money)))).IsFalse();
    }
    public sealed record Money(decimal Amount);

    [Test]
    public async Task A_different_required_flag_or_property_set_is_incompatible()
    {
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Strict)), Info(Plain(), typeof(Strict)))).IsTrue();
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Strict)), Info(Plain(), typeof(Loose)))).IsFalse();
    }
    public sealed class Strict { [JsonRequired] public string? A { get; set; } }
    public sealed class Loose { public string? A { get; set; } }

    [Test]
    public async Task Different_null_omission_and_dictionary_key_policies_are_incompatible()
    {
        var omit = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Inner)), Info(omit, typeof(Inner)))).IsFalse();
        var keys = new JsonSerializerOptions { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase };
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Dictionary<string, Inner>)), Info(keys, typeof(Dictionary<string, Inner>)))).IsFalse();
    }

    public sealed class ChildList : List<Inner> { }
    public sealed record ListWrapper([property: JsonPropertyName("list")] ChildList Values);

    [Test]
    public async Task Non_generic_collection_subclass_still_checks_its_element_contract()
        => await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(ListWrapper)), Info(Camel(), typeof(ListWrapper)))).IsFalse();

    sealed class FilteredResolver : IJsonTypeInfoResolver
    {
        readonly DefaultJsonTypeInfoResolver inner = new();
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => type == typeof(Inner) ? null : inner.GetTypeInfo(type, options);
    }

    [Test]
    public async Task Missing_nested_metadata_is_not_assumed_compatible()
    {
        var resolver = new FilteredResolver();
        var filtered = new JsonSerializerOptions { TypeInfoResolver = resolver };
        filtered.MakeReadOnly();
        await Assert.That(ContractCompatibility.Compatible(Info(Plain(), typeof(Wrapper)), resolver.GetTypeInfo(typeof(Wrapper), filtered)!)).IsFalse();
    }

    [Test]
    public async Task Distinct_property_specific_write_conditions_are_not_assumed_equal()
    {
        static JsonSerializerOptions WithCondition(bool include)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info => { if (info.Type == typeof(Inner)) info.Properties[0].ShouldSerialize = (_, _) => include; });
            var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
            options.MakeReadOnly(); return options;
        }
        await Assert.That(ContractCompatibility.Compatible(WithCondition(true).GetTypeInfo(typeof(Inner)), WithCondition(false).GetTypeInfo(typeof(Inner)))).IsFalse();
    }

    public class Shape { public string? Label { get; set; } }
    public sealed class Circle : Shape { public int Radius { get; set; } }

    [Test]
    public async Task Polymorphic_discriminators_must_match()
    {
        static JsonTypeInfo ShapeInfo(string discriminator)
        {
            var resolver = new DefaultJsonTypeInfoResolver();
            resolver.Modifiers.Add(info =>
            {
                if (info.Type != typeof(Shape)) return;
                info.PolymorphismOptions = new JsonPolymorphismOptions();
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(typeof(Circle), discriminator));
            });
            var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
            options.MakeReadOnly(); return options.GetTypeInfo(typeof(Shape));
        }
        await Assert.That(ContractCompatibility.Compatible(ShapeInfo("circle"), ShapeInfo("round"))).IsFalse();
        await Assert.That(ContractCompatibility.Compatible(ShapeInfo("circle"), ShapeInfo("circle"))).IsTrue();
    }
}
