using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Zeeq.LlmCmd.Generators;

/// <summary>Discovers command and aggregate roots and emits typed STJ metadata.</summary>
[Generator]
public sealed class MetadataGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Unsupported = new DiagnosticDescriptor(
        "LLMMETA001",
        "Unsupported metadata contract",
        "Type '{0}' cannot be generated: {1}; supply an explicit STJ context or change the contract",
        "Serialization",
        DiagnosticSeverity.Error,
        true
    );

    private const string JsonIgnoreAttribute = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonConstructorAttribute =
        "System.Text.Json.Serialization.JsonConstructorAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context
            .SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, cancellationToken) =>
                    ctx.SemanticModel.GetDeclaredSymbol(
                        (TypeDeclarationSyntax)ctx.Node,
                        cancellationToken
                    ) as INamedTypeSymbol
            )
            .Where(static symbol => symbol is not null)
            .Collect();
        context.RegisterSourceOutput(
            candidates.Combine(context.CompilationProvider),
            static (production, input) => Emit(production, input.Left, input.Right)
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
        System.Collections.Immutable.ImmutableArray<INamedTypeSymbol?> symbols,
        Compilation compilation
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
            // An explicitly authored context owns its graph and must bypass automatic metadata.
            if (
                symbol
                    .GetAttributes()
                    .Any(a =>
                        (
                            a.AttributeClass?.ToDisplayString()
                                == "Zeeq.LlmCmd.PromptCommandAttribute"
                            || a.AttributeClass?.ToDisplayString()
                                == "Zeeq.LlmCmd.CompositeOutputAttribute"
                        )
                        && a.NamedArguments.Any(pair =>
                            pair.Key == "JsonContext" && pair.Value.Value is INamedTypeSymbol
                        )
                    )
            )
            {
                continue;
            }

            if (
                symbol.BaseType?.OriginalDefinition.ToDisplayString()
                    == "Zeeq.LlmCmd.PromptCommand<TP, TR>"
                && !symbol.IsGenericType
            )
            {
                roots.AddRange(symbol.BaseType.TypeArguments);
            }
            if (
                symbol
                    .GetAttributes()
                    .Any(a =>
                        a.AttributeClass?.ToDisplayString()
                        == "Zeeq.LlmCmd.CompositeOutputAttribute"
                    )
            )
            {
                roots.Add(symbol);
            }
        }

        var types = new List<ITypeSymbol>();
        var dtoShapes = new Dictionary<string, ITypeSymbol>(StringComparer.Ordinal);
        var pending = new Queue<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            var type = pending.Dequeue().WithNullableAnnotation(NullableAnnotation.None);
            if (
                type is INamedTypeSymbol genericDto
                && genericDto.IsGenericType
                && genericDto.TypeKind == TypeKind.Class
                && !IsFramework(genericDto)
            )
            {
                var key = genericDto.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (
                    dtoShapes.TryGetValue(key, out var previous)
                    && !SymbolEqualityComparer.IncludeNullability.Equals(previous, genericDto)
                )
                {
                    context.ReportDiagnostic(
                        Diagnostic.Create(
                            Unsupported,
                            type.Locations.FirstOrDefault(),
                            type.ToDisplayString(),
                            "conflicting nullable generic contracts"
                        )
                    );
                }
                else
                {
                    dtoShapes[key] = genericDto;
                }
            }
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
                if (!IsFramework(named) && named.TypeKind != TypeKind.Enum && !HasConverter(named))
                {
                    foreach (var property in Properties(named))
                    {
                        pending.Enqueue(property.Type);
                    }
                    foreach (
                        var attr in named
                            .GetAttributes()
                            .Where(a =>
                                a.AttributeClass?.ToDisplayString()
                                == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
                            )
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
            var accessible =
                compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)
                && ConverterIsAccessible(type, compilation);
            if (accessible && IsSupported(type, out _))
            {
                continue;
            }

            _ = IsSupported(type, out var validationReason);
            var reason = accessible
                ? validationReason
                : "type is not accessible from the consuming assembly";
            context.ReportDiagnostic(
                Diagnostic.Create(
                    Unsupported,
                    type.Locations.FirstOrDefault(),
                    type.ToDisplayString(),
                    reason ?? "unsupported contract shape"
                )
            );
        }

        if (roots.Count == 0)
        {
            return;
        }

        types.Sort(
            (left, right) =>
                string.Compare(TypeName(left), TypeName(right), StringComparison.Ordinal)
        );
        var b = new StringBuilder(
            "// <auto-generated/>\n#nullable enable\nusing System;\nusing System.Text.Json;\nusing System.Text.Json.Serialization.Metadata;\n"
        );
        b.AppendLine(
            "namespace Zeeq.LlmCmd.Generated { internal sealed class LlmCmdJsonResolver : IJsonTypeInfoResolver { public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) { if (options.Converters.Count != 0) throw new NotSupportedException(\"Options-level converters require an explicit JSON context.\");"
        );
        var accessors = new StringBuilder();
        var accessorIndex = 0;
        foreach (var type in types)
        {
            if (
                compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)
                && ConverterIsAccessible(type, compilation)
                && IsSupported(type, out _)
            )
            {
                EmitType(b, type, accessors, ref accessorIndex);
            }
        }
        b.AppendLine("return null; }");
        b.Append(accessors);
        b.AppendLine("} }");
        context.AddSource("LlmCmdJsonResolver.g.cs", SourceText.From(b.ToString(), Encoding.UTF8));
    }

    private static bool IsSupported(ITypeSymbol type, out string? reason)
    {
        reason = null;
        if (type is IArrayTypeSymbol array)
        {
            reason = array.Rank == 1 ? null : "only one-dimensional arrays are supported";
            return array.Rank == 1;
        }
        if (
            type.SpecialType
                is SpecialType.System_String
                    or SpecialType.System_Int32
                    or SpecialType.System_Decimal
                    or SpecialType.System_Boolean
                    or SpecialType.System_Char
                    or SpecialType.System_Byte
                    or SpecialType.System_SByte
                    or SpecialType.System_Int16
                    or SpecialType.System_UInt16
                    or SpecialType.System_UInt32
                    or SpecialType.System_Int64
                    or SpecialType.System_UInt64
                    or SpecialType.System_Single
                    or SpecialType.System_Double
            || type.ToDisplayString() is "System.Guid" or "System.DateOnly"
        )
        {
            return true;
        }
        if (type is not INamedTypeSymbol named)
        {
            return false;
        }
        if (!SupportedAttributes(named, out reason))
        {
            return false;
        }
        var derivedTypes = named
            .GetAttributes()
            .Where(a =>
                a.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
            );
        if (
            derivedTypes.Any(a =>
                a.ConstructorArguments.Length != 2 || a.ConstructorArguments[1].Value is not string
            )
        )
        {
            reason = "only explicitly named polymorphic derived types are supported";
            return false;
        }
        if (HasConverter(named))
        {
            if (
                named
                    .GetAttributes()
                    .Count(a =>
                        a.AttributeClass?.ContainingNamespace.ToDisplayString()
                        == "System.Text.Json.Serialization"
                    ) != 1
            )
            {
                reason =
                    "converter combined with other JSON attributes requires an explicit context";
                return false;
            }
            var converter = named
                .GetAttributes()
                .First(a =>
                    a.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonConverterAttribute"
                );
            var converterType =
                converter.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol;
            var current = converterType;
            while (
                current is not null
                && current.OriginalDefinition.ToDisplayString()
                    != "System.Text.Json.Serialization.JsonConverter<T>"
            )
            {
                current = current.BaseType;
            }
            if (
                converterType is null
                || converterType.IsAbstract
                || converterType.DeclaredAccessibility != Accessibility.Public
                || !converterType.InstanceConstructors.Any(c =>
                    c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length == 0
                )
                || current is null
                || !SymbolEqualityComparer.Default.Equals(current.TypeArguments[0], named)
            )
            {
                reason =
                    "converter must be an accessible concrete parameterless JsonConverter<T>; factories require an explicit context";
                return false;
            }
            return true;
        }
        if (
            named.TypeKind == TypeKind.Enum
            || named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        )
        {
            return true;
        }
        if (
            named.OriginalDefinition.ToDisplayString()
                == "System.Collections.Generic.Dictionary<TKey, TValue>"
            && named.TypeArguments[0].SpecialType != SpecialType.System_String
        )
        {
            reason = "only string dictionary keys are supported";
            return false;
        }
        if (
            named.OriginalDefinition.ToDisplayString()
            is "System.Collections.Generic.List<T>"
                or "System.Collections.Generic.IReadOnlyList<T>"
                or "System.Collections.Generic.Dictionary<TKey, TValue>"
                or "System.Collections.Immutable.ImmutableArray<T>"
        )
        {
            return true;
        }
        if (
            IsFramework(named)
            || named.TypeKind != TypeKind.Class
            || named.IsGenericType
                && named.TypeParameters.Any(p =>
                    p.HasConstructorConstraint
                    || p.HasUnmanagedTypeConstraint
                    || p.HasNotNullConstraint
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
        if (
            named
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Any(p =>
                    !p.IsStatic
                    && p.DeclaredAccessibility == Accessibility.Public
                    && p.GetMethod is null
                    && p.SetMethod?.DeclaredAccessibility == Accessibility.Public
                )
        )
        {
            reason = "set-only properties require an explicit context";
            return false;
        }
        if (props.Any(p => p.IsRequired && IgnoreCondition(p) == "Always"))
        {
            reason = "ignored required properties require an explicit context";
            return false;
        }
        if (
            named
                .GetMembers()
                .Any(member =>
                    member is IFieldSymbol or IPropertySymbol
                    && member
                        .GetAttributes()
                        .Any(a =>
                            a.AttributeClass?.ContainingNamespace.ToDisplayString()
                            == "System.Text.Json.Serialization"
                        )
                    && !props.Any(property =>
                        SymbolEqualityComparer.Default.Equals(property, member)
                    )
                )
        )
        {
            reason = "JSON attributes on inaccessible or field members require an explicit context";
            return false;
        }
        if (
            ctor is not null
            && props.Any(p =>
                p.IsRequired
                && ctor.Parameters.Any(parameter =>
                    string.Equals(parameter.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                )
            )
        )
        {
            reason =
                "required properties bound to constructor parameters require an explicit context";
            return false;
        }
        if (props.Any(property => !SupportedAttributes(property, out _)))
        {
            reason = "property uses an unsupported JSON attribute";
            return false;
        }
        if (
            props.Any(property =>
                property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                || Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(property.Name)
                    != SyntaxKind.None
            )
        )
        {
            reason = "property access is not supported";
            return false;
        }
        if (
            ctor is not null
            && ctor.Parameters.Any(p =>
                p.HasExplicitDefaultValue
                && p.ExplicitDefaultValue is not null
                && p.ExplicitDefaultValue is not string
                && p.ExplicitDefaultValue is not int
                && p.ExplicitDefaultValue is not bool
            )
        )
        {
            reason = "constructor default value is not supported";
            return false;
        }
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

    private static bool ConverterIsAccessible(ITypeSymbol type, Compilation compilation)
    {
        var converter = type.GetAttributes()
            .FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonConverterAttribute"
            );
        return converter is null
            || converter.ConstructorArguments.FirstOrDefault().Value is INamedTypeSymbol named
                && compilation.IsSymbolAccessibleWithin(named, compilation.Assembly);
    }

    private static bool IsFramework(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace.ToDisplayString();
        return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal);
    }

    private static bool SupportedAttributes(ISymbol symbol, out string? reason)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            var name = attribute.AttributeClass?.ToDisplayString() ?? "";
            if (!name.StartsWith("System.Text.Json.Serialization.", StringComparison.Ordinal))
            {
                for (
                    var parent = attribute.AttributeClass?.BaseType;
                    parent is not null;
                    parent = parent.BaseType
                )
                {
                    if (
                        parent.ToDisplayString()
                        == "System.Text.Json.Serialization.JsonConverterAttribute"
                    )
                    {
                        reason = "custom converter attributes require an explicit context";
                        return false;
                    }
                }
                continue;
            }

            var supported =
                symbol is IPropertySymbol
                    ? name
                        is "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                            or JsonIgnoreAttribute
                            or "System.Text.Json.Serialization.JsonRequiredAttribute"
                    : name
                        is "System.Text.Json.Serialization.JsonPolymorphicAttribute"
                            or "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
                            or "System.Text.Json.Serialization.JsonConverterAttribute";
            if (
                name == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                && (
                    attribute.ConstructorArguments.Length != 1
                    || attribute.ConstructorArguments[0].Value is not string
                )
            )
            {
                reason = "JsonPropertyName must specify a non-null string";
                return false;
            }

            if (
                !supported
                || name == "System.Text.Json.Serialization.JsonPolymorphicAttribute"
                    && attribute.NamedArguments.Any(x => x.Key != "TypeDiscriminatorPropertyName")
            )
            {
                reason = "unsupported JSON attribute " + name;
                return false;
            }
        }

        reason = null;
        return true;
    }

    private static bool HasConverter(INamedTypeSymbol type) =>
        type.GetAttributes()
            .Any(a =>
                a.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonConverterAttribute"
            );

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
                reason = "[JsonConstructor] must select a public constructor by generated metadata";
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
        return condition is "Always" or "Never" or "WhenWritingDefault" or "WhenWritingNull"
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
            SpecialType.System_Char => "Char",
            SpecialType.System_Byte => "Byte",
            SpecialType.System_SByte => "SByte",
            SpecialType.System_Int16 => "Int16",
            SpecialType.System_UInt16 => "UInt16",
            SpecialType.System_UInt32 => "UInt32",
            SpecialType.System_Int64 => "Int64",
            SpecialType.System_UInt64 => "UInt64",
            SpecialType.System_Single => "Single",
            SpecialType.System_Double => "Double",
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
            if (array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                // STJ encodes byte arrays as base64 instead of a numeric array.
                b.AppendLine(
                    $"if (type == typeof({t})) return JsonMetadataServices.CreateValueInfo<{t}>(options, JsonMetadataServices.ByteArrayConverter);"
                );
                return;
            }

            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateArrayInfo<{TypeName(array.ElementType)}>(options, new JsonCollectionInfoValues<{t}>());"
            );
            return;
        }
        if (type is not INamedTypeSymbol named)
        {
            return;
        }
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateValueInfo<{t}>(options, JsonMetadataServices.GetNullableConverter<{TypeName(named.TypeArguments[0])}>(options));"
            );
            return;
        }
        if (named.TypeKind == TypeKind.Enum && !HasConverter(named))
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateValueInfo<{t}>(options, JsonMetadataServices.GetEnumConverter<{t}>(options));"
            );
            return;
        }
        var converter = named
            .GetAttributes()
            .FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonConverterAttribute"
            );
        if (
            converter is not null
            && converter.ConstructorArguments[0].Value is INamedTypeSymbol converterType
        )
        {
            // Provenance permits compatible contracts without sharing options-dependent converter state.
            b.AppendLine(
                $"if (type == typeof({t})) return global::Zeeq.LlmCmd.GeneratedJsonConverterMetadata.AttributeDeclared(JsonMetadataServices.CreateValueInfo<{t}>(options, new {TypeName(converterType)}()));"
            );
            return;
        }
        var generic = named.OriginalDefinition.ToDisplayString();
        if (generic == "System.Collections.Generic.List<T>")
        {
            b.AppendLine(
                $"if (type == typeof({t})) return JsonMetadataServices.CreateListInfo<{t}, {TypeName(named.TypeArguments[0])}>(options, new JsonCollectionInfoValues<{t}> {{ ObjectCreator = static () => new {t}() }});"
            );
            return;
        }
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
        if (IsFramework(named) || named.TypeKind == TypeKind.Enum)
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
                            : p.ExplicitDefaultValue is bool boolean
                                ? boolean
                                    ? "true"
                                    : "false"
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
            .Where(a =>
                a.AttributeClass?.ToDisplayString()
                == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
            )
            .ToArray();
        if (derivedTypes.Length > 0)
        {
            var polymorphic = named
                .GetAttributes()
                .FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonPolymorphicAttribute"
                );
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
                .FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                );
            var jsonName = attr?.ConstructorArguments[0].Value is string n
                ? SymbolDisplay.FormatLiteral(n, true)
                : "null";
            var ignoreCondition = IgnoreCondition(p) is { } condition
                ? "global::System.Text.Json.Serialization.JsonIgnoreCondition." + condition
                : "null";
            var ignored = IgnoreCondition(p) == "Always";
            var setter = "null";
            if (
                !ignored
                && p.SetMethod is not null
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
            var getter = ignored ? "null" : $"static obj => (({t})obj).{p.Name}";
            b.AppendLine(
                $"{{ var prop = JsonMetadataServices.CreatePropertyInfo<{TypeName(p.Type)}>(options, new JsonPropertyInfoValues<{TypeName(p.Type)}> {{ IsProperty = true, IsPublic = true, DeclaringType = typeof({TypeName(p.ContainingType)}), PropertyName = {SymbolDisplay.FormatLiteral(p.Name, true)}, JsonPropertyName = {jsonName}, IgnoreCondition = {ignoreCondition}, Getter = {getter}, Setter = {setter} }}); prop.IsRequired = {p.IsRequired.ToString().ToLowerInvariant()}; prop.IsGetNullable = {nullable.ToString().ToLowerInvariant()}; prop.IsSetNullable = {nullable.ToString().ToLowerInvariant()}; list.Add(prop); }}"
            );
        }
        b.AppendLine("return list.ToArray(); }, }); return result; }");
    }
}
