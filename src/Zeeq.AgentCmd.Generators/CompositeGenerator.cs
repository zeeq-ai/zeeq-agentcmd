using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// One model per authored aggregate. Public wrappers are emitted only after the
// complete destination shape has been validated: a partial plan would be unsafe.
internal sealed class CompositeModel
{
    public INamedTypeSymbol Type = null!;
    public INamedTypeSymbol? Context;
    public RecordDeclarationSyntax Syntax = null!;
    public string Id = "";
    public int Revision;
    public List<ContractTypeModel> Contracts = new List<ContractTypeModel>();
    public List<PropertyModel> Parts = new List<PropertyModel>();
}

internal static class CompositeModels
{
    internal static CompositeModel? Build(
        INamedTypeSymbol type,
        AttributeData attribute,
        Action<SyntaxNode, string> error
    )
    {
        var syntax = type
            .DeclaringSyntaxReferences.Select(r => r.GetSyntax())
            .OfType<RecordDeclarationSyntax>()
            .First();
        var messages = new List<string>();
        void Fail(string message) => messages.Add(message);
        if (
            !type.IsRecord
            || !type.IsSealed
            || type.IsGenericType
            || type.ContainingType is not null
            || type.ContainingNamespace.IsGlobalNamespace
            || type.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax() is not RecordDeclarationSyntax d
                || !d.Modifiers.Any(SyntaxKind.PartialKeyword)
            )
        )
        {
            Fail(
                "CompositeOutput requires a sealed, partial, top-level non-generic record in a namespace."
            );
        }

        var model = new CompositeModel
        {
            Type = type,
            Syntax = syntax,
            Id = attribute.ConstructorArguments.FirstOrDefault().Value as string ?? "",
            Revision = 1,
        };
        INamedTypeSymbol? explicitContext = null;
        foreach (var item in attribute.NamedArguments)
        {
            if (item.Key == "Revision" && item.Value.Value is int revision)
            {
                model.Revision = revision;
            }

            if (item.Key == "JsonContext")
            {
                explicitContext = item.Value.Value as INamedTypeSymbol;
            }
        }
        if (
            string.IsNullOrWhiteSpace(model.Id)
            || model.Id.Length > 128
            || model.Id.Any(c =>
                !(
                    c
                    is >= 'a'
                        and <= 'z'
                        or >= 'A'
                        and <= 'Z'
                        or >= '0'
                        and <= '9'
                        or '_'
                        or '.'
                        or '-'
                        or ':'
                )
            )
            || model.Revision < 1
        )
        {
            Fail(
                "CompositeOutput needs a stable ID (letters, digits, '.', '_', '-' or ':', up to 128 characters) and positive revision."
            );
        }

        model.Contracts = CommandModels.ContractGraph(type, type, Fail);
        model.Parts =
            model
                .Contracts.FirstOrDefault(c => SymbolEqualityComparer.Default.Equals(c.Type, type))
                ?.Properties
            ?? new List<PropertyModel>();
        if (model.Parts.Count == 0)
        {
            Fail("CompositeOutput must declare at least one public part.");
        }

        var allNames = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (
                var property in current
                    .GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(p =>
                        !p.IsStatic
                        && !p.IsIndexer
                        && p.DeclaredAccessibility == Accessibility.Public
                        && p.GetMethod?.DeclaredAccessibility == Accessibility.Public
                    )
            )
            {
                if (!allNames.Add(property.Name))
                {
                    Fail("Ambiguous inherited composite member: " + property.Name);
                }
            }
        }

        foreach (var part in model.Parts)
        {
            if (part.Ignored)
            {
                Fail("A composite part cannot be ignored: " + part.Symbol.Name);
            }

            if (
                part.Symbol.Name.Length > 128
                || part.Symbol.Name.Any(c =>
                    !(
                        c
                        is >= 'a'
                            and <= 'z'
                            or >= 'A'
                            and <= 'Z'
                            or >= '0'
                            and <= '9'
                            or '_'
                            or '.'
                            or '-'
                            or ':'
                    )
                )
            )
            {
                Fail("Composite part name cannot be used as a stable slot ID: " + part.Symbol.Name);
            }

            if (
                !part.Required
                && (
                    !part.Symbol.Type.IsReferenceType
                    || part.Symbol.Type.NullableAnnotation != NullableAnnotation.Annotated
                )
            )
            {
                Fail("An optional composite part must be nullable: " + part.Symbol.Name);
            }

            if (part.Symbol.Name == "Of")
            {
                Fail("Composite part name conflicts with StepCatalog.Of: " + part.Symbol.Name);
            }
        }
        foreach (var suffix in new[] { "CompositeMetadata", "CompositionExtensions", "Parts" })
        {
            if (type.ContainingNamespace.GetTypeMembers(type.Name + suffix).Length > 0)
            {
                Fail("Generated composite type name collision: " + type.Name + suffix);
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in model.Parts)
        {
            foreach (
                var name in new[] { "With" + part.Symbol.Name + "From", "Reuse" + part.Symbol.Name }
            )
            {
                if (!names.Add(name))
                {
                    Fail("Generated composite member name collision: " + name);
                }
            }
        }

        foreach (
            var group in model
                .Parts.Where(p => p.JsonName is not null)
                .GroupBy(p => p.JsonName!, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
        )
        {
            Fail("Composite parts have the same explicit JSON name: " + group.Key);
        }

        model.Context = explicitContext;
        if (
            explicitContext is not null
            && !explicitContext
                .GetAttributes()
                .Any(a =>
                    a.AttributeClass?.ToDisplayString()
                        == "System.Text.Json.Serialization.JsonSerializableAttribute"
                    && a.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol declared
                    && SymbolEqualityComparer.Default.Equals(declared, type)
                )
        )
        {
            Fail("The selected JsonContext must declare the aggregate type.");
        }
        if (messages.Count > 0)
        {
            foreach (var message in messages)
            {
                error(syntax, message);
            }

            return null;
        }
        return model;
    }

    internal static string Helper(CompositeModel model) =>
        "global::"
        + model.Type.ContainingNamespace.ToDisplayString()
        + "."
        + model.Type.Name
        + "CompositeMetadata";

    static string Quote(string text) => SymbolDisplay.FormatLiteral(text, true);

    internal static void Emit(SourceProductionContext output, CompositeModel model)
    {
        var whole = CommandModels.Type(model.Type);
        var ns = model.Type.ContainingNamespace.ToDisplayString();
        var helper = model.Type.Name + "CompositeMetadata";
        var shape = string.Join(
            "",
            model
                .Contracts.OrderBy(c => CommandModels.Type(c.Type), StringComparer.Ordinal)
                .Select(c =>
                    PromptGenerator.Frame(CommandModels.Type(c.Type))
                    + PromptGenerator.Attributes(c.Type)
                    + string.Join(
                        "",
                        c.Properties.Select(p =>
                            PromptGenerator.Frame(p.Symbol.Name)
                            + PromptGenerator.Frame(CommandModels.Type(p.Symbol.Type))
                            + PromptGenerator.Frame(p.Required ? "required" : "optional")
                            + PromptGenerator.Frame(p.JsonName ?? "policy")
                            + PromptGenerator.Attributes(p.Symbol)
                        )
                    )
                    + string.Join(
                        "",
                        c.Type.InstanceConstructors.Select(ctor =>
                            PromptGenerator.Frame(
                                string.Join(
                                    "",
                                    ctor.Parameters.Select(p =>
                                        PromptGenerator.Frame(p.Name)
                                        + PromptGenerator.Frame(CommandModels.Type(p.Type))
                                        + PromptGenerator.Frame(
                                            p.IsOptional ? "optional" : "required"
                                        )
                                        + PromptGenerator.Frame(
                                            p.HasExplicitDefaultValue
                                                ? Convert.ToString(
                                                    p.ExplicitDefaultValue,
                                                    System
                                                        .Globalization
                                                        .CultureInfo
                                                        .InvariantCulture
                                                ) ?? "null"
                                                : "none"
                                        )
                                    )
                                )
                            )
                        )
                    )
                )
        );
        var policy = model.Context is null
            ? ""
            : string.Join(
                "",
                model
                    .Context.GetAttributes()
                    .Where(a =>
                        a.AttributeClass?.ToDisplayString()
                        == "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute"
                    )
                    .Select(PromptGenerator.Attribute)
            );
        string hash;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            hash = string.Concat(
                sha.ComputeHash(
                        Encoding.UTF8.GetBytes(
                            PromptGenerator.Frame("composite-policy-v1")
                                + PromptGenerator.Frame(model.Id)
                                + PromptGenerator.Frame(
                                    model.Revision.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture
                                    )
                                )
                                + shape
                                + policy
                        )
                    )
                    .Select(x => x.ToString("x2"))
            );
        }

        var b = new StringBuilder(
            "// <auto-generated/>\n#pragma warning disable CS1591\n#nullable enable\nnamespace "
        )
            .Append(ns)
            .Append(" {\ninternal static class ")
            .Append(helper)
            .Append(" {\n");
        b.Append("private static readonly global::Zeeq.AgentCmd.CommandJsonContract<")
            .Append(whole)
            .Append(',')
            .Append(whole)
            .Append("> __contract=new(static()=>")
            .Append(
                model.Context is null
                    ? "new global::Zeeq.AgentCmd.Generated.AgentCmdJsonResolver()"
                    : CommandModels.RuntimeType(model.Context) + ".Default"
            )
            .Append(",__ConfigureContract");
        foreach (var c in model.Contracts)
        {
            b.Append(",typeof(").Append(CommandModels.RuntimeType(c.Type)).Append(')');
        }

        b.Append(");\n");
        PromptGenerator.EmitContractModifier(b, model.Contracts);
        b.Append("internal static void __ValidateContract()=>__contract.Validate();\n");
        PromptGenerator.EmitValidation(
            b,
            new CommandModel
            {
                Parameters = model.Type,
                Result = model.Type,
                Contracts = model.Contracts,
            }
        );
        foreach (var p in model.Parts)
        {
            b.Append("internal static readonly global::Zeeq.AgentCmd.PartSlot<")
                .Append(whole)
                .Append(',')
                .Append(CommandModels.Type(p.Symbol.Type))
                .Append("> @")
                .Append(p.Symbol.Name)
                .Append(" = global::Zeeq.AgentCmd.PartSlot<")
                .Append(whole)
                .Append(',')
                .Append(CommandModels.Type(p.Symbol.Type))
                .Append(">.Declare(")
                .Append(Quote(p.Symbol.Name))
                .Append(',')
                .Append(PromptGenerator.JsonName(p, "__contract.Result.Options"))
                .Append(',')
                .Append(p.Required ? "true" : "false")
                .Append(");\n");
        }
        b.Append("internal static readonly global::Zeeq.AgentCmd.CompositeDescriptor<")
            .Append(whole)
            .Append("> Descriptor = new(")
            .Append(Quote(model.Id))
            .Append(',')
            .Append(model.Revision)
            .Append(',')
            .Append(Quote(hash))
            .Append(",__contract.Result,new global::Zeeq.AgentCmd.SlotDescriptor[] {");
        foreach (var p in model.Parts)
        {
            b.Append('@').Append(p.Symbol.Name).Append(".Descriptor,");
        }

        b.Append("},__ValidateResult);\n}\npublic static class ")
            .Append(model.Type.Name)
            .Append("Parts {\n");
        foreach (var p in model.Parts)
        {
            b.Append("public static global::Zeeq.AgentCmd.PartSlot<")
                .Append(whole)
                .Append(',')
                .Append(CommandModels.Type(p.Symbol.Type))
                .Append("> @")
                .Append(p.Symbol.Name)
                .Append(" => ")
                .Append(helper)
                .Append(".@")
                .Append(p.Symbol.Name)
                .Append(";\n");
        }

        b.Append("}\npublic static class ")
            .Append(model.Type.Name)
            .Append("CompositionExtensions {\n");
        const string caller =
            ", [global::System.Runtime.CompilerServices.CallerMemberName] string member=\"\", [global::System.Runtime.CompilerServices.CallerFilePath] string file=\"\", [global::System.Runtime.CompilerServices.CallerLineNumber] int line=0";
        // Source wording makes deferred work explicit; invariant PartSlot keeps two same-type destinations distinct.
        foreach (var p in model.Parts)
        {
            string part = CommandModels.Type(p.Symbol.Type),
                name = p.Symbol.Name,
                slot = helper + ".@" + name;
            string prefix = "public static global::Zeeq.AgentCmd.CompositionRoot<" + whole + "> ";
            b.Append(prefix)
                .Append("With")
                .Append(name)
                .Append("From<TP>(this global::Zeeq.AgentCmd.CompositionRoot<")
                .Append(whole)
                .Append("> root, global::Zeeq.AgentCmd.Invocation<TP,")
                .Append(part)
                .Append("> call")
                .Append(caller)
                .Append(") => root.WithPartFrom(")
                .Append(slot)
                .Append(",call,member,file,line);\n");
            b.Append(prefix)
                .Append("With")
                .Append(name)
                .Append("From<TP>(this global::Zeeq.AgentCmd.CompositionRoot<")
                .Append(whole)
                .Append("> root, global::Zeeq.AgentCmd.PromptCommand<TP,")
                .Append(part)
                .Append("> command, TP parameters")
                .Append(caller)
                .Append(") => root.WithPartFrom(")
                .Append(slot)
                .Append(",command.WithParameters(parameters),member,file,line);\n");
            b.Append(prefix)
                .Append("With")
                .Append(name)
                .Append("From(this global::Zeeq.AgentCmd.CompositionRoot<")
                .Append(whole)
                .Append("> root, global::Zeeq.AgentCmd.PartSource<")
                .Append(part)
                .Append("> source")
                .Append(caller)
                .Append(") => root.WithPartFrom(")
                .Append(slot)
                .Append(",source,member,file,line);\n");
            b.Append(prefix)
                .Append("Reuse")
                .Append(name)
                .Append("(this global::Zeeq.AgentCmd.CompositionRoot<")
                .Append(whole)
                .Append("> root, global::Zeeq.AgentCmd.Pinned<")
                .Append(part)
                .Append("> value) => root.ReusePart(")
                .Append(slot)
                .Append(",value);\n");
        }
        // A typed step token carries a slot identity, not a result or ambient lookup.
        b.Append("extension(global::Zeeq.AgentCmd.StepCatalog<")
            .Append(whole)
            .Append("> steps) {\n");
        foreach (var p in model.Parts)
        {
            b.Append("public global::Zeeq.AgentCmd.StepRef<")
                .Append(CommandModels.Type(p.Symbol.Type))
                .Append("> @")
                .Append(p.Symbol.Name)
                .Append(" => steps.Of(")
                .Append(helper)
                .Append(".@")
                .Append(p.Symbol.Name)
                .Append(");\n");
        }

        b.Append("}\n}\n}\n");
        // Hash the fully qualified type in the hint name, since replacing dots
        // makes distinct namespaces such as A_B.C and A.B_C collide.
        string hint;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            hint = string.Concat(
                sha.ComputeHash(
                        Encoding.UTF8.GetBytes(
                            model.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        )
                    )
                    .Select(x => x.ToString("x2"))
            );
        }

        output.AddSource(
            "Composite_" + hint + ".g.cs",
            SourceText.From(b.ToString(), Encoding.UTF8)
        );
    }
}
