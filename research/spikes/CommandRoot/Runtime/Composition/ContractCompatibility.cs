using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
namespace CommandRoot;

// WHY this check exists: the aggregate is the leaf's stored wire JSON, read back through the WHOLE's serializer metadata. If the two sides
// disagree on a wire name, a required member, or how an enum or number is written, the mismatch only shows after every part was paid for
// and attached (Attached is terminal), and every rerun of that draft key fails the same way. So it is compared before any claim.
// WHY not "same JsonSerializerOptions instance": separately generated, compatible contexts are the normal case. Compare what shows on the wire.
// WHY conservative: a converter's own configuration (naming policy of an enum converter, a custom converter's state) cannot be read back.
// Two different instances of a configurable converter are therefore treated as incompatible rather than assumed equal.
/// <summary>
/// Whether two serializer descriptions of the same type write and read the same wire JSON: names, required members, number and enum
/// handling, checked through nested objects, collections and dictionaries. Used by the engine before any claim; a host or generator may call it at startup.
/// </summary>
public static class ContractCompatibility
{
    public static bool Compatible(JsonTypeInfo leaf, JsonTypeInfo part)
    {
        // Resolvers, modifiers and converters can throw. Unknown metadata is a
        // preflight rejection, not an exception or permission to spend.
        try { return Compatible(leaf, part, []); }
        catch (Exception) { return false; }
    }

    static bool Compatible(JsonTypeInfo leaf, JsonTypeInfo part, HashSet<Type> seen)
    {
        if (leaf.Type != part.Type || leaf.Kind != part.Kind) return false;
        if (!seen.Add(leaf.Type)) return true; // recursive type: already being compared
        // These options affect emitted or accepted wire values even when all property names match.
        if (leaf.Options.NumberHandling != part.Options.NumberHandling ||
            leaf.Options.DefaultIgnoreCondition != part.Options.DefaultIgnoreCondition ||
            leaf.Options.IgnoreReadOnlyProperties != part.Options.IgnoreReadOnlyProperties ||
            leaf.Options.IgnoreReadOnlyFields != part.Options.IgnoreReadOnlyFields ||
            leaf.Options.IncludeFields != part.Options.IncludeFields ||
            leaf.Options.PropertyNameCaseInsensitive != part.Options.PropertyNameCaseInsensitive ||
            leaf.Options.RespectNullableAnnotations != part.Options.RespectNullableAnnotations ||
            leaf.Options.RespectRequiredConstructorParameters != part.Options.RespectRequiredConstructorParameters ||
            leaf.Options.UnmappedMemberHandling != part.Options.UnmappedMemberHandling ||
            leaf.Options.UnknownTypeHandling != part.Options.UnknownTypeHandling ||
            leaf.Options.PreferredObjectCreationHandling != part.Options.PreferredObjectCreationHandling ||
            leaf.Options.MaxDepth != part.Options.MaxDepth ||
            !ReferenceEquals(leaf.Options.DictionaryKeyPolicy, part.Options.DictionaryKeyPolicy) ||
            !ReferenceEquals(leaf.Options.ReferenceHandler, part.Options.ReferenceHandler)) return false;
        if (!SameConverter(leaf.Options, part.Options, leaf.Type)) return false;
        if (!SamePolymorphism(leaf, part, seen)) return false;

        switch (leaf.Kind)
        {
            case JsonTypeInfoKind.Object:
                var mine = leaf.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
                if (mine.Count != part.Properties.Count) return false;
                foreach (var p in part.Properties)
                {
                    if (!mine.TryGetValue(p.Name, out var q) || q.PropertyType != p.PropertyType || q.IsRequired != p.IsRequired ||
                        q.IsGetNullable != p.IsGetNullable || q.IsSetNullable != p.IsSetNullable || q.NumberHandling != p.NumberHandling ||
                        (q.Get is null) != (p.Get is null) || (q.Set is null) != (p.Set is null) ||
                        !ReferenceEquals(q.ShouldSerialize, p.ShouldSerialize)) return false;
                    if (!SameInstance(q.CustomConverter, p.CustomConverter)) return false;
                    if (!CompatibleType(leaf.Options, part.Options, p.PropertyType, seen)) return false;
                }
                return true;
            case JsonTypeInfoKind.Enumerable:
            case JsonTypeInfoKind.Dictionary:
                // Element and value types decide how each entry is written, so descend into them.
                foreach (var inner in ElementTypes(leaf.Type))
                    if (!CompatibleType(leaf.Options, part.Options, inner, seen)) return false;
                return true;
            default:
                return true;
        }
    }

    static bool CompatibleType(JsonSerializerOptions a, JsonSerializerOptions b, Type type, HashSet<Type> seen)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (!SameConverter(a, b, t)) return false;
        // An unavailable nested contract is unknown, not proof of compatibility.
        try { return a.TryGetTypeInfo(t, out var x) && b.TryGetTypeInfo(t, out var y) && Compatible(x, y, seen); }
        catch (Exception) { return false; }
    }

    static bool SamePolymorphism(JsonTypeInfo a, JsonTypeInfo b, HashSet<Type> seen)
    {
        var x = a.PolymorphismOptions; var y = b.PolymorphismOptions;
        if (x is null || y is null) return x is null && y is null;
        if (x.TypeDiscriminatorPropertyName != y.TypeDiscriminatorPropertyName ||
            x.IgnoreUnrecognizedTypeDiscriminators != y.IgnoreUnrecognizedTypeDiscriminators ||
            x.UnknownDerivedTypeHandling != y.UnknownDerivedTypeHandling || x.DerivedTypes.Count != y.DerivedTypes.Count) return false;
        foreach (var derived in x.DerivedTypes)
        {
            var match = y.DerivedTypes.FirstOrDefault(d => d.DerivedType == derived.DerivedType);
            if (match.DerivedType is null || !Equals(match.TypeDiscriminator, derived.TypeDiscriminator) ||
                !CompatibleType(a.Options, b.Options, derived.DerivedType, seen)) return false;
        }
        return true;
    }

    // The converter each side would pick for this type. Different converter types (a custom one vs the default) change the bytes.
    static bool SameConverter(JsonSerializerOptions a, JsonSerializerOptions b, Type type)
    {
        // WHY compare behaviour for enums: since .NET 9 one converter class writes numbers or names, so its TYPE does not tell them apart.
        // Writing every defined value through both options shows the real difference (number vs name, naming policy) without reading private state.
        if (type.IsEnum) return SameEnumWire(a, b, type);
        JsonConverter ca, cb;
        try { ca = a.GetConverter(type); cb = b.GetConverter(type); } catch (Exception) { return false; }
        if (ca.GetType() != cb.GetType()) return false;
        return ReferenceEquals(ca, cb) || !Configurable(ca);
    }

    static bool SameEnumWire(JsonSerializerOptions a, JsonSerializerOptions b, Type type)
    {
        try
        {
            foreach (var value in Enum.GetValues(type))
                if (JsonSerializer.Serialize(value, type, a) != JsonSerializer.Serialize(value, type, b)) return false;
            return true;
        }
        catch (Exception) { return false; }
    }

    static bool SameInstance(JsonConverter? x, JsonConverter? y) =>
        x is null && y is null || x is not null && y is not null && x.GetType() == y.GetType() && (ReferenceEquals(x, y) || !Configurable(x));

    // Built-in converters for plain types carry no hidden configuration. Anything outside System.Text.Json might, and cannot be inspected.
    static bool Configurable(JsonConverter c) => c.GetType().Assembly != typeof(JsonSerializer).Assembly;

    static IEnumerable<Type> ElementTypes(Type type)
    {
        if (type.IsArray) return [type.GetElementType()!];
        // A non-generic subclass of List<T> or Dictionary<K,V> still has a wire element contract.
        var candidates = type.GetInterfaces().Concat([type]);
        var dictionary = candidates.FirstOrDefault(t => t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null) return dictionary.GetGenericArguments();
        var enumerable = candidates.FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null) return enumerable.GetGenericArguments();
        return [];
    }
}
