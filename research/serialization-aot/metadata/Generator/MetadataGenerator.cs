using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MetadataSpike;

/// <summary>Discovers command and aggregate roots and emits typed STJ metadata.</summary>
[Generator]
public sealed class MetadataGenerator : IIncrementalGenerator
{
    private const string JsonIgnoreAttribute = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonConstructorAttribute =
        "System.Text.Json.Serialization.JsonConstructorAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context
            .SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) =>
                    ctx.SemanticModel.GetDeclaredSymbol((TypeDeclarationSyntax)ctx.Node)
                    as INamedTypeSymbol
            )
            .Where(static symbol => symbol is not null)
            .Collect();
        context.RegisterSourceOutput(
            candidates,
            static (production, symbols) => Emit(production, symbols)
        );
    }

    private static string TypeName(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.None)
            .ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                )
            );

    private static void Emit(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<INamedTypeSymbol?> symbols
    )
    {
        var roots = new List<ITypeSymbol>();
        foreach (
            var symbol in symbols
                .Where(s => s is not null)
                .Distinct(SymbolEqualityComparer.Default)
                .OfType<INamedTypeSymbol>()
        )
        {
            for (var baseType = symbol.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                if (
                    baseType.OriginalDefinition.ToDisplayString()
                    == "SerializationSpikes.PromptCommand<TParameters, TResult>"
                )
                {
                    roots.AddRange(baseType.TypeArguments);
                }
            }
            if (
                symbol
                    .GetAttributes()
                    .Any(a =>
                        a.AttributeClass?.ToDisplayString()
                        == "SerializationSpikes.CompositeOutputAttribute"
                    )
            )
            {
                roots.Add(symbol);
            }
        }

        var types = new List<ITypeSymbol>();
        var pending = new Queue<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue().WithNullableAnnotation(NullableAnnotation.None);
            if (types.Any(t => SymbolEqualityComparer.Default.Equals(t, type)))
            {
                continue;
            }
            types.Add(type);
            if (type is IArrayTypeSymbol array)
            {
                pending.Enqueue(array.ElementType);
            }
            else if (type is INamedTypeSymbol named)
            {
                foreach (var argument in named.TypeArguments)
                {
                    pending.Enqueue(argument);
                }
                if (
                    !named
                        .ContainingNamespace.ToDisplayString()
                        .StartsWith("System", System.StringComparison.Ordinal)
                    && named.TypeKind != TypeKind.Enum
                    && !HasConverter(named)
                )
                {
                    foreach (var property in Properties(named))
                    {
                        pending.Enqueue(property.Type);
                    }
                    foreach (
                        var attr in named
                            .GetAttributes()
                            .Where(a => a.AttributeClass?.Name == "JsonDerivedTypeAttribute")
                    )
                    {
                        if (attr.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol derived)
                        {
                            pending.Enqueue(derived);
                        }
                    }
                }
            }
        }

        foreach (var type in types)
        {
            if (IsSupported(type, out var reason))
            {
                continue;
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "META001",
                        "Unsupported metadata shape",
                        "Type '{0}' is not supported by the metadata spike: {1}. Supply an explicit STJ context or change the contract",
                        "Serialization",
                        DiagnosticSeverity.Error,
                        true
                    ),
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(),
                    reason ?? "unsupported contract shape"
                )
            );
        }

        var b = new StringBuilder(
            "// <auto-generated/>\n#nullable enable\nusing System;\nusing System.Text.Json;\nusing System.Text.Json.Serialization.Metadata;\n"
        );
        b.AppendLine(
            "internal sealed class GeneratedMetadataResolver : IJsonTypeInfoResolver { public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) { if (options.Converters.Count != 0) throw new NotSupportedException(\"Options-level converters are not supported by this spike.\");"
        );
        var accessors = new StringBuilder();
        var accessorIndex = 0;
        foreach (var type in types)
        {
            if (IsSupported(type, out _))
            {
                EmitType(b, type, accessors, ref accessorIndex);
            }
        }
        b.AppendLine("return null; }");
        b.Append(accessors);
        b.AppendLine("}");
        context.AddSource(
            "GeneratedMetadataResolver.g.cs",
            SourceText.From(b.ToString(), Encoding.UTF8)
        );
    }

    private static bool IsSupported(ITypeSymbol type, out string? reason)
    {
        reason = null;
        if (type is IArrayTypeSymbol array)
        {
            return array.Rank == 1;
        }
        if (
            type.SpecialType
                is SpecialType.System_String
                    or SpecialType.System_Int32
                    or SpecialType.System_Decimal
                    or SpecialType.System_Boolean
            || type.ToDisplayString() is "System.Guid" or "System.DateOnly"
        )
        {
            return true;
        }
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }
        if (HasConverter(named))
        {
            return true;
        }
        if (
            named.OriginalDefinition.ToDisplayString()
            is "System.Collections.Generic.IReadOnlyList<T>"
                or "System.Collections.Generic.Dictionary<TKey, TValue>"
                or "System.Collections.Immutable.ImmutableArray<T>"
        )
        {
            return true;
        }
        if (
            named
                .ContainingNamespace.ToDisplayString()
                .StartsWith("System", System.StringComparison.Ordinal)
            || named.TypeKind != TypeKind.Class
            || named.IsGenericType
                && named.TypeParameters.Any(p =>
                    p.HasConstructorConstraint
                    || p.ConstraintTypes.Length > 0
                    || p.HasReferenceTypeConstraint
                    || p.HasValueTypeConstraint
                )
        )
        {
            return false;
        }
        var ctor = SelectConstructor(named, out reason);
        if (reason is not null)
        {
            return false;
        }

        var props = Properties(named).ToArray();
        if (props.Any(property => IgnoreCondition(property) == "Unsupported"))
        {
            reason = "unrecognized JsonIgnore condition";
            return false;
        }

        return (
                named.IsAbstract
                || ctor is not null
                    && ctor.Parameters.All(p =>
                        props.Any(x =>
                            string.Equals(x.Name, p.Name, System.StringComparison.OrdinalIgnoreCase)
                            && SymbolEqualityComparer.Default.Equals(x.Type, p.Type)
                        )
                    )
            ) && props.All(p => !p.IsIndexer);
    }

    private static bool HasConverter(INamedTypeSymbol type) =>
        type.GetAttributes().Any(a => a.AttributeClass?.Name == "JsonConverterAttribute");

    private static IMethodSymbol? SelectConstructor(INamedTypeSymbol type, out string? reason)
    {
        reason = null;
        if (type.IsAbstract)
        {
            return null;
        }

        // A new overload must not change the JSON contract. Match STJ's explicit
        // choice first, then its parameterless/single-public-constructor defaults.
        // Reuse this selection for validation and emission so they cannot disagree.
        var marked = type
            .InstanceConstructors.Where(constructor =>
                constructor
                    .GetAttributes()
                    .Any(attribute =>
                        attribute.AttributeClass?.ToDisplayString() == JsonConstructorAttribute
                    )
            )
            .ToArray();
        if (marked.Length > 1)
        {
            reason = "multiple constructors have [JsonConstructor]";
            return null;
        }

        if (marked.Length == 1)
        {
            if (marked[0].DeclaredAccessibility != Accessibility.Public)
            {
                // This prototype emits direct constructor calls. Reject an
                // inaccessible annotation instead of silently choosing a public one.
                reason = "[JsonConstructor] must select a public constructor in this spike";
                return null;
            }

            return marked[0];
        }

        var constructors = type
            .InstanceConstructors.Where(constructor =>
                constructor.DeclaredAccessibility == Accessibility.Public
            )
            .ToArray();
        var parameterless = constructors.FirstOrDefault(constructor =>
            constructor.Parameters.Length == 0
        );
        if (parameterless is not null)
        {
            return parameterless;
        }

        if (constructors.Length == 1)
        {
            return constructors[0];
        }

        reason =
            constructors.Length == 0
                ? "no public constructor is available"
                : "multiple public parameterized constructors require [JsonConstructor]";
        return null;
    }

    private static string? IgnoreCondition(IPropertySymbol property)
    {
        var attribute = property
            .GetAttributes()
            .FirstOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == JsonIgnoreAttribute
            );
        if (attribute is null)
        {
            return null;
        }

        var argument = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "Condition");
        if (argument.Key is null)
        {
            return "Always";
        }

        // Resolve the enum symbol rather than copy numeric values from one SDK.
        // STJ applies the condition itself, including Never's override of global
        // defaults. Unknown values must not become silently omitted properties.
        var condition = (argument.Value.Type as INamedTypeSymbol)
            ?.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(field =>
                field.HasConstantValue && Equals(field.ConstantValue, argument.Value.Value)
            )
            ?.Name;
        return
            condition
                is "Always"
                    or "Never"
                    or "WhenWritingDefault"
                    or "WhenWritingNull"
                    or "WhenWriting"
                    or "WhenReading"
            ? condition
            : "Unsupported";
    }

    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var names = new HashSet<string>();
        for (
            var current = type;
            current is not null && current.SpecialType != SpecialType.System_Object;
            current = current.BaseType
        )
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (
                    !property.IsStatic
                    && property.DeclaredAccessibility == Accessibility.Public
                    && property.GetMethod is not null
                    && IgnoreCondition(property) != "Always"
                    && names.Add(property.Name)
                )
                {
                    yield return property;
                }
            }
        }
    }

    private static void EmitType(
        StringBuilder b,
        ITypeSymbol type,
        StringBuilder accessors,
        ref int accessorIndex
    )
    {
        var t = TypeName(type);
        var scalar = type.SpecialType switch
        {
            SpecialType.System_String => "String",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_Decimal => "Decimal",
            SpecialType.System_Boolean => "Boolean",
            _ => type.ToDisplayString() switch
            {
                "System.Guid" => "Guid",
                "System.DateOnly" => "DateOnly",
                _ => null,
            },
        };
        if (scalar is not null)
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateValueInfo<{t}>(options, JsonMetadataServices.{scalar}Converter);"
            );
            return;
        }
        if (type is IArrayTypeSymbol array)
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateArrayInfo<{TypeName(array.ElementType)}>(options, new JsonCollectionInfoValues<{t}>());"
            );
            return;
        }
        if (type is not INamedTypeSymbol named)
        {
            return;
        }
        var converter = named
            .GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "JsonConverterAttribute");
        if (
            converter is not null
            && converter.ConstructorArguments[0].Value is INamedTypeSymbol converterType
        )
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateValueInfo<{t}>(options, new {TypeName(converterType)}(){(named.TypeKind == TypeKind.Enum ? ".CreateConverter(type, options)" : "")});"
            );
            return;
        }
        var generic = named.OriginalDefinition.ToDisplayString();
        if (generic == "System.Collections.Generic.IReadOnlyList<T>")
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateIEnumerableInfo<{t}, {TypeName(named.TypeArguments[0])}>(options, new JsonCollectionInfoValues<{t}>());"
            );
            return;
        }
        if (generic == "System.Collections.Generic.Dictionary<TKey, TValue>")
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateDictionaryInfo<{t}, {TypeName(named.TypeArguments[0])}, {TypeName(named.TypeArguments[1])}>(options, new JsonCollectionInfoValues<{t}> {{ ObjectCreator = static () => new {t}() }});"
            );
            return;
        }
        if (generic == "System.Collections.Immutable.ImmutableArray<T>")
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateImmutableEnumerableInfo<{t}, {TypeName(named.TypeArguments[0])}>(options, new JsonCollectionInfoValues<{t}>(), global::System.Collections.Immutable.ImmutableArray.CreateRange);"
            );
            return;
        }
        if (
            named
                .ContainingNamespace.ToDisplayString()
                .StartsWith("System", System.StringComparison.Ordinal)
            || named.TypeKind == TypeKind.Enum
        )
        {
            return;
        }
        var ctor = SelectConstructor(named, out _);
        if (ctor is null && !named.IsAbstract)
        {
            return;
        }
        var props = Properties(named).ToArray();
        if (
            ctor is not null
            && ctor.Parameters.Any(p =>
                !props.Any(x =>
                    string.Equals(x.Name, p.Name, System.StringComparison.OrdinalIgnoreCase)
                )
            )
        )
        {
            return;
        }
        var required = props.Where(p => p.IsRequired).ToArray();
        b.AppendLine(
            $"if (type == typeof({t})) {{ var result = JsonMetadataServices.CreateObjectInfo<{t}>(options, new JsonObjectInfoValues<{t}> {{"
        );
        if (named.IsAbstract) { }
        else if (ctor!.Parameters.Length == 0 && required.Length == 0)
        {
            b.AppendLine($"ObjectCreator = static () => new {t}(),");
        }
        else
        {
            b.AppendLine(
                $"ObjectWithParameterizedConstructorCreator = static args => new {t}({string.Join(", ", ctor!.Parameters.Select((p, i) => $"({TypeName(p.Type)})args[{i}]!"))}){(required.Length == 0 ? "" : " { " + string.Join(", ", required.Select((p, i) => $"{p.Name} = ({TypeName(p.Type)})args[{ctor.Parameters.Length + i}]!")) + " }")},"
            );
            b.AppendLine(
                "ConstructorParameterMetadataInitializer = static () => new JsonParameterInfoValues[] {"
            );
            foreach (var p in ctor.Parameters)
            {
                var value = p.HasExplicitDefaultValue
                    ? p.ExplicitDefaultValue is null
                        ? "null"
                        : p.ExplicitDefaultValue is string s
                            ? SymbolDisplay.FormatLiteral(s, true)
                            : System.Convert.ToString(
                                p.ExplicitDefaultValue,
                                System.Globalization.CultureInfo.InvariantCulture
                            )
                    : "null";
                b.AppendLine(
                    $"new() {{ Name = {SymbolDisplay.FormatLiteral(p.Name, true)}, ParameterType = typeof({TypeName(p.Type)}), Position = {p.Ordinal}, HasDefaultValue = {p.HasExplicitDefaultValue.ToString().ToLowerInvariant()}, DefaultValue = {value}, IsNullable = {(p.NullableAnnotation == NullableAnnotation.Annotated).ToString().ToLowerInvariant()} }},"
                );
            }
            foreach (var p in required)
            {
                b.AppendLine(
                    $"new() {{ Name = {SymbolDisplay.FormatLiteral(p.Name, true)}, ParameterType = typeof({TypeName(p.Type)}), Position = {ctor.Parameters.Length + System.Array.IndexOf(required, p)}, IsMemberInitializer = true, IsNullable = {(p.NullableAnnotation == NullableAnnotation.Annotated).ToString().ToLowerInvariant()} }},"
                );
            }
            b.AppendLine("},");
        }
        var derivedTypes = named
            .GetAttributes()
            .Where(a => a.AttributeClass?.Name == "JsonDerivedTypeAttribute")
            .ToArray();
        if (derivedTypes.Length > 0)
        {
            var polymorphic = named
                .GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "JsonPolymorphicAttribute");
            var discriminator =
                polymorphic
                    ?.NamedArguments.FirstOrDefault(a => a.Key == "TypeDiscriminatorPropertyName")
                    .Value.Value as string
                ?? "$type";
            b.AppendLine(
                $"PolymorphismOptions = new JsonPolymorphismOptions {{ TypeDiscriminatorPropertyName = {SymbolDisplay.FormatLiteral(discriminator, true)}, DerivedTypes = {{"
            );
            foreach (var attr in derivedTypes)
            {
                if (
                    attr.ConstructorArguments[0].Value is ITypeSymbol derived
                    && attr.ConstructorArguments[1].Value is string tag
                )
                {
                    b.AppendLine(
                        $"new JsonDerivedType(typeof({TypeName(derived)}), {SymbolDisplay.FormatLiteral(tag, true)}),"
                    );
                }
            }
            b.AppendLine("} },");
        }
        b.AppendLine(
            "PropertyMetadataInitializer = _ => { var list = new System.Collections.Generic.List<JsonPropertyInfo>();"
        );
        foreach (var p in props)
        {
            var attr = p.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute");
            var jsonName = attr?.ConstructorArguments[0].Value is string n
                ? SymbolDisplay.FormatLiteral(n, true)
                : "null";
            var ignoreCondition = IgnoreCondition(p) is { } condition
                ? "global::System.Text.Json.Serialization.JsonIgnoreCondition." + condition
                : "null";
            var setter = "null";
            if (
                p.SetMethod is not null
                && p.SetMethod.DeclaredAccessibility == Accessibility.Public
            )
            {
                var id = accessorIndex++;
                var declaring = TypeName(p.ContainingType);
                var genericParameters = p.ContainingType.TypeParameters;
                var genericSuffix =
                    genericParameters.Length == 0
                        ? ""
                        : "<" + string.Join(", ", genericParameters.Select(x => x.Name)) + ">";
                var declaredType =
                    genericParameters.Length == 0
                        ? declaring
                        : TypeName(p.ContainingType.OriginalDefinition);
                var valueType =
                    genericParameters.Length == 0
                        ? TypeName(p.Type)
                        : TypeName(p.OriginalDefinition.Type);
                accessors.AppendLine(
                    $"[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"set_{p.Name}\")] private static extern void Set{id}{genericSuffix}({declaredType} obj, {valueType} value);"
                );
                setter = $"static (obj, value) => Set{id}(({declaring})obj, value!)";
            }
            var nullable = p.NullableAnnotation == NullableAnnotation.Annotated;
            b.AppendLine(
                $"{{ var prop = JsonMetadataServices.CreatePropertyInfo<{TypeName(p.Type)}>(options, new JsonPropertyInfoValues<{TypeName(p.Type)}> {{ IsProperty = true, IsPublic = true, DeclaringType = typeof({TypeName(p.ContainingType)}), PropertyName = {SymbolDisplay.FormatLiteral(p.Name, true)}, JsonPropertyName = {jsonName}, IgnoreCondition = {ignoreCondition}, Getter = static obj => (({t})obj).{p.Name}, Setter = {setter} }}); prop.IsRequired = {p.IsRequired.ToString().ToLowerInvariant()}; prop.IsGetNullable = {nullable.ToString().ToLowerInvariant()}; prop.IsSetNullable = {nullable.ToString().ToLowerInvariant()}; list.Add(prop); }}"
            );
        }
        b.AppendLine("return list.ToArray(); }, }); return result; }");
    }
}
