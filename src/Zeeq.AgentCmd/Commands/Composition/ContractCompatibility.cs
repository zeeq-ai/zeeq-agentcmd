using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Zeeq.AgentCmd;

// WHY this check exists: the aggregate is the leaf's stored wire JSON, read back through the WHOLE's serializer metadata. If the two sides
// disagree on a wire name, a required member, or how an enum or number is written, the mismatch only shows after every part was paid for
// and attached (Attached is terminal), and every rerun of that draft key fails the same way. So it is compared before any claim.
// WHY not "same JsonSerializerOptions instance": separately generated, compatible contexts are the normal case. Compare what shows on the wire.
// WHY conservative: a converter's own configuration (naming policy of an enum converter, a custom converter's state) cannot be read back.
// Different configurable instances need either shared host ownership or generated
// declaration provenance; equality of converter types alone does not prove intent.
/// <summary>
/// Whether two serializer descriptions of the same type write and read the same wire JSON: names, required members, number and enum
/// handling, checked through nested objects, collections and dictionaries. Used by the engine before any claim; a host or generator may call it at startup.
/// </summary>
public static class ContractCompatibility
{
    /// <summary>Checks whether the source and destination JSON contracts can safely represent the same pinned value.</summary>
    public static bool Compatible(JsonTypeInfo leaf, JsonTypeInfo part)
    {
        // Resolvers, modifiers and converters can throw. Unknown metadata is a
        // preflight rejection, not an exception or permission to spend.
        try
        {
            return Compatible(leaf, part, []);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool Compatible(JsonTypeInfo leaf, JsonTypeInfo part, HashSet<Type> seen)
    {
        if (leaf.Type != part.Type || leaf.Kind != part.Kind)
        {
            return false;
        }

        if (!seen.Add(leaf.Type))
        {
            return true; // recursive type: already being compared
        }
        // These options affect emitted or accepted wire values even when all property names match.
        if (
            leaf.Options.NumberHandling != part.Options.NumberHandling
            || leaf.Options.DefaultIgnoreCondition != part.Options.DefaultIgnoreCondition
            || leaf.Options.IgnoreReadOnlyProperties != part.Options.IgnoreReadOnlyProperties
            || leaf.Options.IgnoreReadOnlyFields != part.Options.IgnoreReadOnlyFields
            || leaf.Options.IncludeFields != part.Options.IncludeFields
            || leaf.Options.PropertyNameCaseInsensitive != part.Options.PropertyNameCaseInsensitive
            || leaf.Options.RespectNullableAnnotations != part.Options.RespectNullableAnnotations
            || leaf.Options.RespectRequiredConstructorParameters
                != part.Options.RespectRequiredConstructorParameters
            || leaf.Options.UnmappedMemberHandling != part.Options.UnmappedMemberHandling
            || leaf.Options.UnknownTypeHandling != part.Options.UnknownTypeHandling
            || leaf.Options.PreferredObjectCreationHandling
                != part.Options.PreferredObjectCreationHandling
            || leaf.Options.MaxDepth != part.Options.MaxDepth
            || !ReferenceEquals(leaf.Options.DictionaryKeyPolicy, part.Options.DictionaryKeyPolicy)
            || !ReferenceEquals(leaf.Options.ReferenceHandler, part.Options.ReferenceHandler)
        )
        {
            return false;
        }

        if (!SameConverter(leaf.Options, part.Options, leaf.Type))
        {
            return false;
        }

        if (!SamePolymorphism(leaf, part, seen))
        {
            return false;
        }

        switch (leaf.Kind)
        {
            case JsonTypeInfoKind.Object:
                var mine = leaf.Properties.ToDictionary(p => p.Name, StringComparer.Ordinal);
                if (mine.Count != part.Properties.Count)
                {
                    return false;
                }

                foreach (var p in part.Properties)
                {
                    if (
                        !mine.TryGetValue(p.Name, out var q)
                        || q.PropertyType != p.PropertyType
                        || q.IsRequired != p.IsRequired
                        || q.IsGetNullable != p.IsGetNullable
                        || q.IsSetNullable != p.IsSetNullable
                        || q.NumberHandling != p.NumberHandling
                        || (q.Get is null) != (p.Get is null)
                        || (q.Set is null) != (p.Set is null)
                        || !ReferenceEquals(q.ShouldSerialize, p.ShouldSerialize)
                    )
                    {
                        return false;
                    }

                    if (!SameInstance(q.CustomConverter, p.CustomConverter))
                    {
                        return false;
                    }

                    if (!CompatibleType(leaf.Options, part.Options, p.PropertyType, seen))
                    {
                        return false;
                    }
                }
                return true;
            case JsonTypeInfoKind.Enumerable:
            case JsonTypeInfoKind.Dictionary:
                // Element and value types decide how each entry is written, so descend into them.
                foreach (var inner in ElementTypes(leaf))
                {
                    if (!CompatibleType(leaf.Options, part.Options, inner, seen))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    static bool CompatibleType(
        JsonSerializerOptions a,
        JsonSerializerOptions b,
        Type type,
        HashSet<Type> seen
    )
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (!SameConverter(a, b, t))
        {
            return false;
        }
        // An unavailable nested contract is unknown, not proof of compatibility.
        try
        {
            return a.TryGetTypeInfo(t, out var x)
                && b.TryGetTypeInfo(t, out var y)
                && Compatible(x, y, seen);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool SamePolymorphism(JsonTypeInfo a, JsonTypeInfo b, HashSet<Type> seen)
    {
        var x = a.PolymorphismOptions;
        var y = b.PolymorphismOptions;
        if (x is null || y is null)
        {
            return x is null && y is null;
        }

        if (
            x.TypeDiscriminatorPropertyName != y.TypeDiscriminatorPropertyName
            || x.IgnoreUnrecognizedTypeDiscriminators != y.IgnoreUnrecognizedTypeDiscriminators
            || x.UnknownDerivedTypeHandling != y.UnknownDerivedTypeHandling
            || x.DerivedTypes.Count != y.DerivedTypes.Count
        )
        {
            return false;
        }

        foreach (var derived in x.DerivedTypes)
        {
            var match = y.DerivedTypes.FirstOrDefault(d => d.DerivedType == derived.DerivedType);
            if (
                match.DerivedType is null
                || !Equals(match.TypeDiscriminator, derived.TypeDiscriminator)
                || !CompatibleType(a.Options, b.Options, derived.DerivedType, seen)
            )
            {
                return false;
            }
        }
        return true;
    }

    // The converter each side would pick for this type. Different converter types (a custom one vs the default) change the bytes.
    static bool SameConverter(JsonSerializerOptions a, JsonSerializerOptions b, Type type)
    {
        // WHY compare behaviour for enums: since .NET 9 one converter class writes numbers or names, so its TYPE does not tell them apart.
        // Writing every defined value through both options shows the real difference (number vs name, naming policy) without reading private state.
        if (type.IsEnum)
        {
            return SameEnumWire(a, b, type);
        }

        JsonConverter ca,
            cb;
        JsonTypeInfo left,
            right;
        try
        {
            left = a.GetTypeInfo(type);
            right = b.GetTypeInfo(type);
            ca = left.Converter;
            cb = right.Converter;
        }
        catch (Exception)
        {
            return false;
        }
        if (ca.GetType() != cb.GetType())
        {
            // STJ may wrap its generated fast writer in a different converter
            // class. The object/collection wire contract is compared below;
            // an internal wrapper name must not reject equivalent metadata.
            return !Configurable(ca)
                && !Configurable(cb)
                && left.Kind == right.Kind
                && left.Kind
                    is JsonTypeInfoKind.Object
                        or JsonTypeInfoKind.Enumerable
                        or JsonTypeInfoKind.Dictionary;
        }

        return ReferenceEquals(ca, cb)
            || !Configurable(ca)
            || ReferenceEquals(a.PropertyNamingPolicy, b.PropertyNamingPolicy)
                && ReferenceEquals(a.Encoder, b.Encoder)
                && a.Converters.Count == 0
                && b.Converters.Count == 0
                && GeneratedJsonConverterMetadata.SameDeclaration(left, right);
    }

    static bool SameEnumWire(JsonSerializerOptions a, JsonSerializerOptions b, Type type)
    {
        try
        {
            var left = a.GetTypeInfo(type);
            var right = b.GetTypeInfo(type);
            // Underlying arrays do not require generating an Enum[] at runtime.
            // Metadata already closes the enum type for native serialization.
            foreach (var underlying in Enum.GetValuesAsUnderlyingType(type))
            {
                var value = Enum.ToObject(type, underlying);
                if (JsonSerializer.Serialize(value, left) != JsonSerializer.Serialize(value, right))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool SameInstance(JsonConverter? x, JsonConverter? y) =>
        x is null && y is null
        || x is not null
            && y is not null
            && x.GetType() == y.GetType()
            && (ReferenceEquals(x, y) || !Configurable(x));

    // Built-in converters for plain types carry no hidden configuration. Anything outside System.Text.Json might, and cannot be inspected.
    static bool Configurable(JsonConverter c) =>
        c.GetType().Assembly != typeof(JsonSerializer).Assembly;

    static IEnumerable<Type> ElementTypes(JsonTypeInfo info)
    {
        // Use STJ's resolved wire shape. Reflecting over collection interfaces
        // would require preserving unrelated members merely for preflight.
        if (info.KeyType is { } key)
        {
            yield return key;
        }
        if (info.ElementType is { } element)
        {
            yield return element;
        }
    }
}
