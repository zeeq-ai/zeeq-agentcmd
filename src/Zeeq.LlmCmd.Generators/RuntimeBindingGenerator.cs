using System;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class RuntimeBindingGenerator
{
    internal static bool IsOverridable(PropertyModel property) =>
        property
            .Symbol.GetAttributes()
            .Any(a =>
                a.AttributeClass?.ToDisplayString() == "Zeeq.LlmCmd.RuntimeOverrideAttribute"
            );

    // Only compiler-generated positional members of one sealed record are retained.
    // This rejects custom copy logic and derived or mutable state rather than trusting a shallow copy.
    internal static bool IsSafeRecord(ITypeSymbol type)
    {
        if (
            type
                is not INamedTypeSymbol
                {
                    IsRecord: true,
                    IsSealed: true,
                    TypeKind: TypeKind.Class
                } record
            || record.DeclaringSyntaxReferences.Length != 1
            || record.DeclaringSyntaxReferences[0].GetSyntax() is not RecordDeclarationSyntax syntax
            || syntax.ParameterList is null
            || syntax.BaseList is not null
            || syntax.Members.Count != 0
            || record.BaseType?.SpecialType != SpecialType.System_Object
        )
        {
            return false;
        }

        return syntax.ParameterList.Parameters.Count > 0
            && record
                .GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsImplicitlyDeclared)
                .All(p =>
                    p.DeclaringSyntaxReferences.All(r => r.GetSyntax() is ParameterSyntax)
                    && CommandModels.Scalar(p.Type)
                    && p.SetMethod is { IsInitOnly: true }
                    && p.DeclaredAccessibility == Accessibility.Public
                )
            && syntax.ParameterList.Parameters.All(parameter =>
                record
                    .GetMembers(parameter.Identifier.ValueText)
                    .OfType<IPropertySymbol>()
                    .Any(p => CommandModels.Scalar(p.Type))
            );
    }

    internal static void EmitOverrides(StringBuilder b, CommandModel model)
    {
        var tp = CommandModels.Type(model.Parameters);
        var allowed = model.ParametersShape.Where(IsOverridable).ToArray();
        var safe = IsSafeRecord(model.Parameters);
        b.Append(
                " input, global::System.Collections.Generic.IReadOnlyDictionary<string,global::System.Text.Json.JsonElement> overrides,out "
            )
            .Append(tp)
            .Append(" effective,out string? error) { effective=input; error=null; ");
        if (!safe || allowed.Length == 0)
        {
            b.Append(
                "if(overrides.Count != 0) { error=\"No runtime overrides are permitted for this input.\"; return false; } return true; }"
            );
        }
        else
        {
            b.Append(
                "if (input is null) { error=\"Input is required.\"; return false; } var candidate=input; var seen=new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal); try { foreach (var entry in overrides) { if (!seen.Add(entry.Key)) { error=\"Invalid runtime override.\"; return false; } switch(entry.Key) {"
            );
            foreach (var p in allowed)
            {
                b.Append("case ").Append(Quote(p.Symbol.Name)).Append(": { ");
                EmitValue(b, p.Symbol.Type);
                b.Append("candidate=candidate with { @")
                    .Append(p.Symbol.Name)
                    .Append("=value }; break; }");
            }
            b.Append(
                "default: error=\"Invalid runtime override.\"; return false; } } } catch (global::System.InvalidOperationException) { error=\"Invalid runtime override.\"; return false; } catch (global::System.FormatException) { error=\"Invalid runtime override.\"; return false; } catch (global::System.OverflowException) { error=\"Invalid runtime override.\"; return false; } effective=candidate; return true; }"
            );
        }

        b.Append("public bool TrySnapshot(")
            .Append(tp)
            .Append(" input,out ")
            .Append(tp)
            .Append(" snapshot) { snapshot=input; return ")
            .Append(safe ? "input is not null" : "false")
            .Append("; } }\n");
    }

    private static void EmitValue(StringBuilder b, ITypeSymbol original)
    {
        var type = original;
        var nullable = original.NullableAnnotation == NullableAnnotation.Annotated;
        if (
            original is INamedTypeSymbol n
            && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        )
        {
            nullable = true;
            type = n.TypeArguments[0];
        }
        b.Append(CommandModels.Type(original))
            .Append(
                " value; if(entry.Value.ValueKind == global::System.Text.Json.JsonValueKind.Null) { "
            );
        b.Append(nullable ? "value=null;" : "error=\"Invalid runtime override.\"; return false;")
            .Append(" } else { ");
        if (type.TypeKind == TypeKind.Enum)
        {
            var underlying = ((INamedTypeSymbol)type).EnumUnderlyingType!;
            b.Append("value=(")
                .Append(CommandModels.Type(type))
                .Append(")entry.Value.")
                .Append(Getter(underlying.SpecialType))
                .Append("();");
        }
        else if (type.SpecialType == SpecialType.System_Char)
        {
            b.Append(
                "var text=entry.Value.GetString(); if(text is null || text.Length != 1) { error=\"Invalid runtime override.\"; return false; } value=text[0];"
            );
        }
        else if (type.SpecialType == SpecialType.System_String)
        {
            b.Append("value=entry.Value.GetString()!;");
        }
        else
        {
            b.Append("value=entry.Value.").Append(Getter(type.SpecialType)).Append("();");
            if (type.SpecialType is SpecialType.System_Single or SpecialType.System_Double)
            {
                b.Append("if(!global::System.")
                    .Append(type.SpecialType == SpecialType.System_Single ? "Single" : "Double")
                    .Append(
                        ".IsFinite(value"
                            + (nullable ? ".Value" : "")
                            + ")) { error=\"Invalid runtime override.\"; return false; }"
                    );
            }
        }
        b.Append(" }");
    }

    private static string Getter(SpecialType type) =>
        type switch
        {
            SpecialType.System_Boolean => "GetBoolean",
            SpecialType.System_Byte => "GetByte",
            SpecialType.System_SByte => "GetSByte",
            SpecialType.System_Int16 => "GetInt16",
            SpecialType.System_UInt16 => "GetUInt16",
            SpecialType.System_Int32 => "GetInt32",
            SpecialType.System_UInt32 => "GetUInt32",
            SpecialType.System_Int64 => "GetInt64",
            SpecialType.System_UInt64 => "GetUInt64",
            SpecialType.System_Single => "GetSingle",
            SpecialType.System_Double => "GetDouble",
            SpecialType.System_Decimal => "GetDecimal",
            _ => throw new InvalidOperationException("Unsupported scalar type."),
        };

    private static string Quote(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
}
