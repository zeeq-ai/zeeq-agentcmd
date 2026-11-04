using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

/// <summary>Generates typed command wiring and catalog registration from authored prompt definitions.</summary>
[Generator]
public sealed class PromptGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor Invalid = new DiagnosticDescriptor(
        "AGENTCMD001",
        "Invalid prompt command",
        "{0}",
        "AgentCmd",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context
            .SyntaxProvider.CreateSyntaxProvider(
                (node, _) =>
                    node is ClassDeclarationSyntax cls
                    && (cls.BaseList is not null || cls.AttributeLists.Count > 0),
                (syntax, ct) =>
                    syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, ct) as INamedTypeSymbol
            )
            .Where(type => type is not null)
            .Collect();
        context.RegisterSourceOutput(
            candidates.Combine(context.CompilationProvider),
            (output, input) => Generate(output, input.Right, input.Left)
        );
    }

    static void Generate(
        SourceProductionContext output,
        Compilation compilation,
        IEnumerable<INamedTypeSymbol?> candidates
    )
    {
        var types = candidates
            .Where(c => c is not null)
            .Cast<INamedTypeSymbol>()
            .Distinct(SymbolEqualityComparer.Default)
            .Cast<INamedTypeSymbol>()
            .ToArray();
        var models = new List<CommandModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            var attribute = type.GetAttributes()
                .FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "Zeeq.AgentCmd.PromptCommandAttribute"
                );
            if (!CommandModels.IsCommand(type) && attribute is null)
            {
                continue;
            }

            var model = Build(type, attribute, compilation, output);
            if (model is null)
            {
                continue;
            }

            if (!ids.Add(model.Id))
            {
                Error(
                    output,
                    model.Syntax,
                    "Prompt ID is duplicated in this assembly: " + model.Id
                );
                continue;
            }
            Emit(output, model);
            models.Add(model);
        }
        var composites = new List<CompositeModel>();
        foreach (
            var type in compilation
                .SyntaxTrees.SelectMany(tree =>
                    tree.GetRoot()
                        .DescendantNodes()
                        .OfType<RecordDeclarationSyntax>()
                        .Select(node => compilation.GetSemanticModel(tree).GetDeclaredSymbol(node))
                        .OfType<INamedTypeSymbol>()
                )
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<INamedTypeSymbol>()
        )
        {
            var attribute = type.GetAttributes()
                .FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "Zeeq.AgentCmd.CompositeOutputAttribute"
                );
            if (attribute is null)
            {
                continue;
            }

            var composite = CompositeModels.Build(
                type,
                attribute,
                (syntax, message) => Error(output, syntax, message)
            );
            if (composite is null)
            {
                continue;
            }

            if (!ids.Add(composite.Id))
            {
                Error(
                    output,
                    composite.Syntax,
                    "Output ID is duplicated in this assembly: " + composite.Id
                );
                continue;
            }
            CompositeModels.Emit(output, composite);
            composites.Add(composite);
        }
        if (models.Count > 0 || composites.Count > 0)
        {
            EmitRegistration(output, compilation.AssemblyName ?? "Application", models, composites);
        }
    }

    static CommandModel? Build(
        INamedTypeSymbol type,
        AttributeData? attribute,
        Compilation compilation,
        SourceProductionContext output
    )
    {
        var syntax = type
            .DeclaringSyntaxReferences.Select(r => r.GetSyntax())
            .OfType<ClassDeclarationSyntax>()
            .First();
        var errors = new List<string>();
        void Fail(string message) => errors.Add(message);
        if (
            !type.IsSealed
            || type.ContainingType is not null
            || type.ContainingNamespace.IsGlobalNamespace
            || type.IsGenericType
            || type.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax() is not ClassDeclarationSyntax s
                || !s.Modifiers.Any(SyntaxKind.PartialKeyword)
            )
            || !CommandModels.IsCommand(type)
        )
        {
            Error(
                output,
                syntax,
                "Expected sealed partial top-level class deriving PromptCommand<TParameters,TResult>."
            );
            return null;
        }
        if (type.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared))
        {
            Fail("The generator owns the command constructor; omit the authored constructor.");
        }

        // A declared but unreadable setting must not silently become a default:
        // that can discard a system instruction or change routing and identity.
        var validId = DeclaredConstant(type, "Id", SpecialType.System_String, Fail);
        var validRevision = DeclaredConstant(type, "Revision", SpecialType.System_Int32, Fail);
        DeclaredConstant(type, "Profile", SpecialType.System_String, Fail);
        DeclaredConstant(type, "SystemTemplate", SpecialType.System_String, Fail);
        var declaredId = CommandModels.Constant(type, "Id");
        var declaredRevision = CommandModels.Integer(type, "Revision");
        var declaredProfile = CommandModels.Constant(type, "Profile");
        var model = new CommandModel
        {
            Type = type,
            Syntax = syntax,
            Parameters = type.BaseType!.TypeArguments[0],
            Result = type.BaseType.TypeArguments[1],
            Id = declaredId ?? "",
            Revision = declaredRevision ?? 1,
            Profile = declaredProfile ?? "",
            System = CommandModels.Constant(type, "SystemTemplate") ?? "",
            User = CommandModels.Constant(type, "UserTemplate") ?? "",
        };
        INamedTypeSymbol? explicitContext = null;
        if (attribute is not null)
        {
            var id = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (declaredId is not null && declaredId != id)
            {
                Fail("Authored Id and [PromptCommand] ID disagree.");
            }

            model.Id = id ?? "";
            foreach (var item in attribute.NamedArguments)
            {
                if (item.Key == "Revision" && item.Value.Value is int revision)
                {
                    if (declaredRevision is not null && declaredRevision.Value != revision)
                    {
                        Fail("Authored Revision and [PromptCommand] Revision disagree.");
                    }
                    model.Revision = revision;
                }

                if (item.Key == "Profile")
                {
                    if (item.Value.Value is not string profile)
                    {
                        Fail("[PromptCommand] Profile must be a non-null string.");
                    }
                    else
                    {
                        if (declaredProfile is not null && declaredProfile != profile)
                        {
                            Fail("Authored Profile and [PromptCommand] Profile disagree.");
                        }
                        model.Profile = profile;
                    }
                }

                if (item.Key == "JsonContext")
                {
                    explicitContext = item.Value.Value as INamedTypeSymbol;
                }
            }
        }
        if (
            (validId && string.IsNullOrWhiteSpace(model.Id))
            || (validRevision && model.Revision < 1)
        )
        {
            Fail(
                "A stable const string Id (or [PromptCommand] ID) and a positive revision are required."
            );
        }

        var userTemplate = CommandModels.Constant(type, "UserTemplate");
        if (userTemplate is null)
        {
            Fail("UserTemplate must be a const string.");
        }
        else if (string.IsNullOrWhiteSpace(userTemplate))
        {
            Fail("UserTemplate must contain non-whitespace text.");
        }

        model.Contracts = CommandModels.ContractGraph(model.Parameters, model.Result, Fail);
        model.ParametersShape =
            model
                .Contracts.FirstOrDefault(c =>
                    SymbolEqualityComparer.Default.Equals(c.Type, model.Parameters)
                )
                ?.Properties
            ?? new List<PropertyModel>();
        if (model.ParametersShape.Count == 0)
        {
            Fail("Parameter type must expose public properties.");
        }

        var overrides = model
            .ParametersShape.Where(RuntimeBindingGenerator.IsOverridable)
            .ToArray();
        if (
            overrides.Length > 0
            && (
                !RuntimeBindingGenerator.IsSafeRecord(model.Parameters)
                || overrides.Any(p => p.Ignored || !CommandModels.Scalar(p.Symbol.Type))
            )
        )
        {
            Fail(
                "Runtime overrides require non-ignored scalar properties on a sealed positional record with no authored members or base type and only scalar positional properties."
            );
        }

        var names = model.ParametersShape.ToDictionary(p => p.Symbol.Name, StringComparer.Ordinal);
        if (Render(model.User, names) is null || Render(model.System, names) is null)
        {
            Fail(
                "Invalid template, unknown or ignored placeholder, or unsupported non-scalar rendered property."
            );
        }

        model.Context = explicitContext;
        if (
            explicitContext is not null
            && (
                !Declares(explicitContext, model.Parameters)
                || !Declares(explicitContext, model.Result)
            )
        )
        {
            Fail("The explicit JsonContext must declare both parameter and result types.");
        }
        foreach (
            var member in type.GetMembers("Validate")
                .Concat(type.GetMembers("ValidateInput"))
                .Concat(type.GetMembers("ValidateResult"))
        )
        {
            if (
                member is not IMethodSymbol method
                || !method.IsStatic
                || method.IsAsync
                || method.Parameters.Length != 1
                || method.Parameters[0].RefKind != RefKind.None
                || method.ReturnType.SpecialType != SpecialType.System_String
                || !SymbolEqualityComparer.Default.Equals(
                    method.Parameters[0].Type,
                    model.Parameters
                ) && !SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, model.Result)
                || method.Name == "ValidateInput"
                    && !SymbolEqualityComparer.Default.Equals(
                        method.Parameters[0].Type,
                        model.Parameters
                    )
                || method.Name == "ValidateResult"
                    && !SymbolEqualityComparer.Default.Equals(
                        method.Parameters[0].Type,
                        model.Result
                    )
            )
            {
                Fail(
                    "Validation hooks must be static string? ValidateInput(TParameters), ValidateResult(TResult), or Validate(TParameters/TResult)."
                );
            }
        }
        model.InputValidator = Validator(
            type,
            "ValidateInput",
            model.Parameters,
            compilation.GetTypeByMetadataName("Zeeq.AgentCmd.IInputValidator`1"),
            out model.InputValidatorInterface,
            Fail
        );
        model.ResultValidator = Validator(
            type,
            "ValidateResult",
            model.Result,
            compilation.GetTypeByMetadataName("Zeeq.AgentCmd.IResultValidator`1"),
            out model.ResultValidatorInterface,
            Fail
        );
        foreach (var hook in type.GetMembers("ConfigureDefaults"))
        {
            if (
                hook is not IMethodSymbol m
                || !m.IsStatic
                || m.IsAsync
                || !m.ReturnsVoid
                || m.Parameters.Length != 1
                || m.Parameters[0].Type.ToDisplayString() != "Zeeq.AgentCmd.ChatSettingsPatch"
                || m.Parameters[0].RefKind != RefKind.None
                || m.DeclaringSyntaxReferences.Any(r =>
                    r.GetSyntax() is not MethodDeclarationSyntax s
                    || !s.Modifiers.Any(SyntaxKind.PartialKeyword)
                    || s.Modifiers.Any(SyntaxKind.PublicKeyword)
                    || s.Modifiers.Any(SyntaxKind.PrivateKeyword)
                    || s.Body is null && s.ExpressionBody is null
                )
            )
            {
                Fail(
                    "ConfigureDefaults must be optional static partial void ConfigureDefaults(ChatSettingsPatch defaults) with a body and no access modifier."
                );
            }
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Error(output, syntax, error);
            }

            return null;
        }
        return model;
    }

    static bool DeclaredConstant(
        INamedTypeSymbol command,
        string name,
        SpecialType expected,
        Action<string> error
    )
    {
        var members = command.GetMembers(name);
        if (members.Length == 0)
        {
            return true;
        }
        if (
            members.Length == 1
            && members[0] is IFieldSymbol field
            && field.IsConst
            && field.Type.SpecialType == expected
            && field.ConstantValue is not null
        )
        {
            return true;
        }

        var type = expected == SpecialType.System_String ? "non-null const string" : "const int";
        error(name + " must be a " + type + " if declared.");
        return false;
    }

    static string? Validator(
        INamedTypeSymbol command,
        string specific,
        ITypeSymbol parameter,
        INamedTypeSymbol? interfaceDefinition,
        out bool interfaceHook,
        Action<string> error
    )
    {
        interfaceHook = false;
        var methods = command
            .GetMembers(specific)
            .OfType<IMethodSymbol>()
            .Concat(command.GetMembers("Validate").OfType<IMethodSymbol>())
            .Where(m =>
                m.IsStatic
                && m.Parameters.Length == 1
                && m.ReturnType.SpecialType == SpecialType.System_String
                && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, parameter)
            )
            .ToList();
        var interfaces = interfaceDefinition is null
            ? Array.Empty<INamedTypeSymbol>()
            : command
                .AllInterfaces.Where(i =>
                    SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, interfaceDefinition)
                )
                .ToArray();
        if (interfaces.Length > 0)
        {
            if (
                interfaces.Length != 1
                || !SymbolEqualityComparer.IncludeNullability.Equals(
                    interfaces[0].TypeArguments[0],
                    parameter
                )
            )
            {
                error(specific + " interface must use the command's exact corresponding type.");
                return null;
            }

            if (
                !command.Interfaces.Any(i =>
                    SymbolEqualityComparer.Default.Equals(i, interfaces[0])
                )
            )
            {
                error(specific + " interface must be declared directly on the command.");
                return null;
            }

            var member = interfaces[0].GetMembers(specific).OfType<IMethodSymbol>().Single();
            var implementation =
                command.FindImplementationForInterfaceMember(member) as IMethodSymbol;
            if (
                implementation is null
                || !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, command)
            )
            {
                error(specific + " interface must be implemented on the command.");
                return null;
            }

            if (
                !methods.Any(method =>
                    SymbolEqualityComparer.Default.Equals(method, implementation)
                )
            )
            {
                methods.Add(implementation);
            }
            interfaceHook = true;
        }
        if (methods.Count > 1)
        {
            error(
                "Ambiguous validation hooks; use distinct ValidateInput and ValidateResult names."
            );
        }

        return methods.FirstOrDefault()?.Name;
    }

    static bool Declares(INamedTypeSymbol context, ITypeSymbol type) =>
        context
            .GetAttributes()
            .Any(a =>
                a.AttributeClass?.ToDisplayString()
                    == "System.Text.Json.Serialization.JsonSerializableAttribute"
                && a.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol t
                && SymbolEqualityComparer.Default.Equals(t, type)
            );

    static string? Render(string template, Dictionary<string, PropertyModel> properties)
    {
        var body = new StringBuilder("var b=new global::System.Text.StringBuilder();");
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length == 0)
            {
                return;
            }

            body.Append("b.Append(").Append(Quote(literal.ToString())).Append(");");
            literal.Clear();
        }
        for (int i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if (ch == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                literal.Append('{');
                i++;
                continue;
            }
            if (ch == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                literal.Append('}');
                i++;
                continue;
            }
            if (ch == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                {
                    return null;
                }

                var key = template.Substring(i + 1, end - i - 1);
                if (
                    !properties.TryGetValue(key, out var p)
                    || p.Ignored
                    || !CommandModels.Scalar(p.Symbol.Type)
                )
                {
                    return null;
                }

                Flush();
                body.Append("b.Append(global::System.Convert.ToString(p.@")
                    .Append(key)
                    .Append(",global::System.Globalization.CultureInfo.InvariantCulture));");
                i = end;
                continue;
            }
            if (ch == '}')
            {
                return null;
            }

            literal.Append(ch);
        }
        Flush();
        return body.Append("return b.ToString();").ToString();
    }

    static void Emit(SourceProductionContext output, CommandModel model)
    {
        var tp = CommandModels.Type(model.Parameters);
        var tr = CommandModels.Type(model.Result);
        var cn = model.Context is null
            ? "new global::Zeeq.AgentCmd.Generated.AgentCmdJsonResolver()"
            : CommandModels.RuntimeType(model.Context) + ".Default";
        var properties = model.ParametersShape.ToDictionary(
            p => p.Symbol.Name,
            StringComparer.Ordinal
        );
        var shape = string.Join(
            "",
            model
                .Contracts.OrderBy(c => CommandModels.Type(c.Type), StringComparer.Ordinal)
                .Select(c =>
                    Frame(CommandModels.Type(c.Type))
                    + Attributes(c.Type)
                    + string.Join(
                        "",
                        c.Properties.Select(p =>
                            Frame(p.Symbol.Name)
                            + Frame(
                                RuntimeBindingGenerator.IsOverridable(p) ? "overridable" : "caller"
                            )
                            + Frame(CommandModels.Type(p.Symbol.Type))
                            + Frame(p.Required ? "required" : "optional")
                            + Frame(p.Ignored ? "ignored" : "included")
                            + Frame(p.JsonName ?? "policy")
                            + Attributes(p.Symbol)
                        )
                    )
                    + string.Join(
                        "",
                        c.Type.InstanceConstructors.Select(cntr =>
                            Frame(
                                string.Join(
                                    "",
                                    cntr.Parameters.Select(p =>
                                        Frame(p.Name)
                                        + Frame(CommandModels.Type(p.Type))
                                        + Frame(p.IsOptional ? "optional" : "required")
                                        + Frame(
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
                    .Select(Attribute)
            );
        var hash = Hash(
            Frame("contract-policy-v2")
                + Frame(model.Id)
                + Frame(model.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + Frame(model.System)
                + Frame(model.User)
                + shape
                + policy
        );
        var clean = new string(
            model
                .Id.Select(ch =>
                    ch < 128 && (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-') ? ch : '_'
                )
                .ToArray()
        );
        if (clean.Length > 55)
        {
            clean = clean.Substring(0, 55);
        }

        var schemaName = clean + "_" + Hash(model.Id).Substring(0, 8);
        var b = new StringBuilder(
            "// <auto-generated/>\n#pragma warning disable CS1591\n#nullable enable\nnamespace "
        )
            .Append(model.Type.ContainingNamespace.ToDisplayString())
            .Append(" { partial class ")
            .Append(model.Type.Name)
            .Append(" {\n");
        b.Append("private static readonly global::Zeeq.AgentCmd.CommandJsonContract<")
            .Append(tp)
            .Append(',')
            .Append(tr)
            .Append("> __contract=new(static()=>")
            .Append(cn)
            .Append(",__ConfigureContract");
        foreach (var contract in model.Contracts)
        {
            b.Append(",typeof(").Append(CommandModels.RuntimeType(contract.Type)).Append(')');
        }

        b.Append(");\n");
        EmitContractModifier(b, model.Contracts);
        b.Append(
            "static partial void ConfigureDefaults(global::Zeeq.AgentCmd.ChatSettingsPatch defaults);\nprivate static void __ApplyDefaults(global::Zeeq.AgentCmd.ChatSettingsPatch defaults)=>ConfigureDefaults(defaults);\n"
        );
        b.Append("internal static void __ValidateContract()=>__contract.Validate();\n");
        EmitValidation(b, model);
        var bindingSignature = Hash(
            Frame("runtime-binding-v1")
                + Frame(tp)
                + Frame(tr)
                + shape
                + policy
                + string.Join(
                    "",
                    model
                        .ParametersShape.OrderBy(p => p.Symbol.Name, StringComparer.Ordinal)
                        .Select(p =>
                            Frame(p.Symbol.Name)
                            + Frame(
                                p.Ignored ? "ignored"
                                : CommandModels.Scalar(p.Symbol.Type) ? "scalar"
                                : "unsupported"
                            )
                        )
                )
        );
        b.Append("private sealed class __RuntimeBinding : global::Zeeq.AgentCmd.IRuntimeBinding<")
            .Append(tp)
            .Append("> { public string ContractSignature => ")
            .Append(Quote(bindingSignature))
            .Append(
                "; public global::System.Collections.Generic.IReadOnlyDictionary<string,string?> Freeze("
            )
            .Append(tp)
            .Append(
                " p) { var values = new global::System.Collections.Generic.Dictionary<string,string?>(global::System.StringComparer.Ordinal); "
            );
        foreach (
            var p in model.ParametersShape.Where(p =>
                !p.Ignored && CommandModels.Scalar(p.Symbol.Type)
            )
        )
        {
            b.Append("values.Add(")
                .Append(Quote(p.Symbol.Name))
                .Append(",global::System.Convert.ToString(p.@")
                .Append(p.Symbol.Name)
                .Append(",global::System.Globalization.CultureInfo.InvariantCulture));");
        }
        b.Append(
                "return new global::System.Collections.ObjectModel.ReadOnlyDictionary<string,string?>(values); } public bool TryApplyOverrides("
            )
            .Append(tp);
        RuntimeBindingGenerator.EmitOverrides(b, model);
        b.Append("private static global::Zeeq.AgentCmd.PromptDescriptor<")
            .Append(tp)
            .Append(',')
            .Append(tr)
            .Append("> Descriptor {get;}=new(")
            .Append(Quote(model.Id))
            .Append(',')
            .Append(model.Revision)
            .Append(',')
            .Append(Quote(model.Profile))
            .Append(',')
            .Append(Quote(model.System))
            .Append(',')
            .Append(Quote(model.User))
            .Append(',')
            .Append(Quote(hash))
            .Append(',')
            .Append(Quote(schemaName))
            .Append(
                ",new global::System.Lazy<global::System.Text.Json.JsonElement>(()=>__contract.Schema),null,p=>{"
            )
            .Append(Render(model.User, properties))
            .Append("},p=>{")
            .Append(Render(model.System, properties))
            .Append(
                "},p=>{ var type=__contract.Parameters; var j=global::System.Text.Json.JsonSerializer.SerializeToElement(p,type); var values=new global::System.Collections.Generic.List<global::Zeeq.AgentCmd.ParameterRecord>();\n"
            );
        foreach (var p in model.ParametersShape.Where(p => !p.Ignored))
        {
            b.Append("{ var name=")
                .Append(JsonName(p, "type.Options"))
                .Append("; if(j.TryGetProperty(name,out var value)) values.Add(new(")
                .Append(Quote(p.Symbol.Name))
                .Append(',')
                .Append(Quote(CommandModels.Type(p.Symbol.Type)))
                .Append(",value.Clone(),name)); }\n");
        }

        b.Append(
                "return values.AsReadOnly(); },p=>__ValidateInput(p),p=>__ValidateResult(p),__contract,__ApplyDefaults,new __RuntimeBinding());\n"
            )
            .Append("[global::System.CodeDom.Compiler.GeneratedCode(\"AgentCmd\",\"1\")] public ")
            .Append(model.Type.Name)
            .Append(
                "(global::Zeeq.AgentCmd.CommandRuntime runtime):base(runtime,Descriptor) {}\n} }\n"
            );
        output.AddSource(
            model.Type.ToDisplayString().Replace('.', '_') + ".g.cs",
            SourceText.From(b.ToString(), Encoding.UTF8)
        );
    }

    internal static void EmitContractModifier(StringBuilder b, List<ContractTypeModel> contracts)
    {
        b.Append(
            "private static void __ConfigureContract(global::System.Text.Json.Serialization.Metadata.JsonTypeInfo info) {\n"
        );
        foreach (var contract in contracts)
        {
            b.Append("if(info.Type==typeof(")
                .Append(CommandModels.RuntimeType(contract.Type))
                .Append(
                    ")) { if(info.Kind!=global::System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object) throw new global::System.InvalidOperationException(\"Custom converter requires an explicit object contract\");\n"
                );
            foreach (
                var p in contract.Properties.Where(p =>
                    !p.Ignored
                    && (
                        p.Required
                        || p.Symbol.OriginalDefinition.Type.TypeKind == TypeKind.TypeParameter
                            && p.Symbol.Type.IsReferenceType
                    )
                )
            )
            {
                b.Append("{ var name=")
                    .Append(JsonName(p, "info.Options"))
                    .Append(
                        "; var found=false; foreach(var property in info.Properties) if(property.Name==name) {"
                    );
                if (p.Required)
                {
                    b.Append("property.IsRequired=true;");
                }

                if (
                    p.Symbol.OriginalDefinition.Type.TypeKind == TypeKind.TypeParameter
                    && p.Symbol.Type.IsReferenceType
                )
                {
                    b.Append("property.IsGetNullable=")
                        .Append(
                            p.Symbol.Type.NullableAnnotation == NullableAnnotation.Annotated
                                ? "true"
                                : "false"
                        )
                        .Append("; property.IsSetNullable=")
                        .Append(
                            p.Symbol.Type.NullableAnnotation == NullableAnnotation.Annotated
                                ? "true"
                                : "false"
                        )
                        .Append(';');
                }

                b.Append("found=true; break; }");
                b.Append(
                    "if(!found) throw new global::System.InvalidOperationException(\"Required property missing from JSON contract\");"
                );
                b.Append("}\n");
            }
            b.Append("return; }\n");
        }
        b.Append("}\n");
    }

    internal static string JsonName(PropertyModel p, string options) =>
        p.JsonName is not null
            ? Quote(p.JsonName)
            : "("
                + options
                + ".PropertyNamingPolicy?.ConvertName("
                + Quote(p.Symbol.Name)
                + ") ?? "
                + Quote(p.Symbol.Name)
                + ")";

    internal static void EmitValidation(StringBuilder b, CommandModel model)
    {
        var methods = model.Contracts.Select((c, i) => new { c.Type, Index = i }).ToArray();
        int variable = 0;
        foreach (var entry in model.Contracts.Select((c, i) => new { Contract = c, Index = i }))
        {
            var type = entry.Contract.Type;
            b.Append("private static string? __Check")
                .Append(entry.Index)
                .Append('(')
                .Append(CommandModels.Type(type))
                .Append(type.IsReferenceType ? "?" : "")
                .Append(" value,global::System.Collections.Generic.HashSet<object> seen) {\n");
            if (type.IsReferenceType)
            {
                b.Append("if(value is null) return \"A non-nullable value was null\";\n");
                var derived = type.GetAttributes()
                    .Where(a =>
                        a.AttributeClass?.ToDisplayString()
                        == "System.Text.Json.Serialization.JsonDerivedTypeAttribute"
                    )
                    .Select(a => a.ConstructorArguments.FirstOrDefault().Value)
                    .OfType<INamedTypeSymbol>()
                    .OrderByDescending(InheritanceDepth);
                foreach (var child in derived)
                {
                    var target = methods.FirstOrDefault(m =>
                        SymbolEqualityComparer.Default.Equals(m.Type, child)
                    );
                    if (target is not null)
                    {
                        b.Append("if(value is ")
                            .Append(CommandModels.Type(child))
                            .Append(" derived")
                            .Append(target.Index)
                            .Append(") return __Check")
                            .Append(target.Index)
                            .Append("(derived")
                            .Append(target.Index)
                            .Append(",seen);\n");
                    }
                }
                b.Append("if(!seen.Add(value)) return null;\n");
            }
            foreach (var property in entry.Contract.Properties.Where(p => !p.Ignored))
            {
                Check("value.@" + property.Symbol.Name, property.Symbol.Type);
            }

            b.Append("return null; }\n");
        }
        Root("Input", model.Parameters, model.InputValidator, model.InputValidatorInterface);
        Root("Result", model.Result, model.ResultValidator, model.ResultValidatorInterface);
        void Root(string suffix, ITypeSymbol type, string? hook, bool interfaceHook)
        {
            if (interfaceHook)
            {
                // Static interface dispatch is needed for explicit implementations; the
                // structural checks remain in the descriptor's generated wrapper.
                b.Append("private static string? __Invoke")
                    .Append(suffix)
                    .Append("Validator<TCommand>(")
                    .Append(CommandModels.Type(type))
                    .Append(" value) where TCommand:global::Zeeq.AgentCmd.I")
                    .Append(suffix)
                    .Append("Validator<")
                    .Append(CommandModels.Type(type))
                    .Append("> => TCommand.Validate")
                    .Append(suffix)
                    .Append("(value);\n");
            }

            b.Append("private static string? __Validate")
                .Append(suffix)
                .Append('(')
                .Append(CommandModels.Type(type))
                .Append(
                    " value) { var seen=new global::System.Collections.Generic.HashSet<object>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);\n"
                );
            if (type.IsReferenceType)
            {
                b.Append("if(value is null) return \"A non-nullable root value was null\";\n");
            }

            var target = methods.FirstOrDefault(m =>
                SymbolEqualityComparer.Default.Equals(m.Type, type)
            );
            if (target is not null)
            {
                b.Append("{ var error=__Check")
                    .Append(target.Index)
                    .Append("(value,seen); if(error is not null) return error; }\n");
            }
            else
            {
                Check("value", type);
            }

            var call =
                interfaceHook
                    ? "__Invoke"
                        + suffix
                        + "Validator<"
                        + CommandModels.Type(model.Type)
                        + ">(value!)"
                : hook is null ? "null"
                : hook + "(value!)";
            b.Append("return ").Append(call).Append("; }\n");
        }
        void Check(string expression, ITypeSymbol type)
        {
            if (type.IsReferenceType)
            {
                if (type.NullableAnnotation == NullableAnnotation.NotAnnotated)
                {
                    b.Append("if(")
                        .Append(expression)
                        .Append(" is null) return \"A non-nullable value was null\";\n");
                }

                b.Append("if(").Append(expression).Append(" is not null) {\n");
            }
            if (
                type is INamedTypeSymbol nullable
                && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            )
            {
                b.Append("if(").Append(expression).Append(".HasValue) {\n");
                Check(expression + ".Value", nullable.TypeArguments[0]);
                b.Append("}\n");
            }
            else if (type is IArrayTypeSymbol array)
            {
                var item = "item" + variable++;
                b.Append("foreach(var ")
                    .Append(item)
                    .Append(" in ")
                    .Append(expression)
                    .Append(") {\n");
                Check(item, array.ElementType);
                b.Append("}\n");
            }
            else if (!CommandModels.Scalar(type) && type is INamedTypeSymbol named)
            {
                var all = named.AllInterfaces.Concat(new[] { named }).ToArray();
                var dictionary = all.FirstOrDefault(i =>
                    i.OriginalDefinition.ToDisplayString()
                        is "System.Collections.Generic.IDictionary<TKey, TValue>"
                            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                );
                var enumerable = all.FirstOrDefault(i =>
                    i.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IEnumerable_T
                );
                var target = methods.FirstOrDefault(m =>
                    SymbolEqualityComparer.Default.Equals(m.Type, type)
                );
                if (dictionary is not null)
                {
                    var item = "entry" + variable++;
                    b.Append("foreach(var ")
                        .Append(item)
                        .Append(" in ")
                        .Append(expression)
                        .Append(") {\n");
                    Check(item + ".Value", dictionary.TypeArguments[1]);
                    b.Append("}\n");
                }
                else if (enumerable is not null)
                {
                    var item = "element" + variable++;
                    b.Append("foreach(var ")
                        .Append(item)
                        .Append(" in ")
                        .Append(expression)
                        .Append(") {\n");
                    Check(item, enumerable.TypeArguments[0]);
                    b.Append("}\n");
                }
                else if (target is not null)
                {
                    b.Append("{ var error=__Check")
                        .Append(target.Index)
                        .Append('(')
                        .Append(expression)
                        .Append(",seen); if(error is not null) return error; }\n");
                }
            }
            if (type.IsReferenceType)
            {
                b.Append("}\n");
            }
        }
        static int InheritanceDepth(INamedTypeSymbol type)
        {
            int depth = 0;
            for (var t = type.BaseType; t is not null; t = t.BaseType)
            {
                depth++;
            }

            return depth;
        }
    }

    static void EmitRegistration(
        SourceProductionContext output,
        string assembly,
        List<CommandModel> models,
        List<CompositeModel> composites
    )
    {
        var name =
            new string(assembly.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray())
            + "Commands";
        if (!char.IsLetter(name[0]) && name[0] != '_')
        {
            name = "_" + name;
        }

        var b = new StringBuilder(
            "// <auto-generated/>\n#pragma warning disable CS1591\n#nullable enable\nnamespace Zeeq.AgentCmd.Generated { public sealed class "
        )
            .Append(name)
            .Append(" : global::Zeeq.AgentCmd.IGeneratedCommandCatalog { private ")
            .Append(name)
            .Append("() {}\n")
            .Append(
                "public static global::System.Collections.Generic.IReadOnlyList<global::Zeeq.AgentCmd.CommandRegistration> Commands {get;}=global::System.Array.AsReadOnly(new global::Zeeq.AgentCmd.CommandRegistration[] {\n"
            );
        foreach (var model in models)
        {
            b.Append("new(")
                .Append(Quote(model.Id))
                .Append(',')
                .Append(model.Revision)
                .Append(",typeof(")
                .Append(CommandModels.RuntimeType(model.Type))
                .Append("),typeof(")
                .Append(CommandModels.RuntimeType(model.Parameters))
                .Append("),typeof(")
                .Append(CommandModels.RuntimeType(model.Result))
                .Append("),")
                .Append(Quote(model.Profile))
                .Append("),\n");
        }

        b.Append("});\n");
        if (composites.Count > 0)
        {
            b.Append(
                "public static global::System.Collections.Generic.IReadOnlyList<global::Zeeq.AgentCmd.ICompositeDescriptor> Composites => global::System.Array.AsReadOnly(new global::Zeeq.AgentCmd.ICompositeDescriptor[] {\n"
            );
            foreach (var composite in composites)
            {
                b.Append(CompositeModels.Helper(composite)).Append(".Descriptor,\n");
            }

            b.Append("});\n");
        }
        b.Append(
            "public static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) {\n"
        );
        foreach (var model in models)
        {
            b.Append(
                    "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddTransient<"
                )
                .Append(CommandModels.RuntimeType(model.Type))
                .Append(
                    ">(services,static provider=>new(global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Zeeq.AgentCmd.CommandRuntime>(provider)));\n"
                );
        }

        foreach (var composite in composites)
        {
            b.Append(
                    "global::Zeeq.AgentCmd.CompositionServiceCollectionExtensions.AddCompositeDescriptor(services,"
                )
                .Append(CompositeModels.Helper(composite))
                .Append(".Descriptor);\n");
        }

        b.Append(
            "}\npublic static global::System.Collections.Generic.IReadOnlyList<global::Zeeq.AgentCmd.CommandContractError> ValidateContracts() { var errors=new global::System.Collections.Generic.List<global::Zeeq.AgentCmd.CommandContractError>();\n"
        );
        foreach (var model in models)
        {
            b.Append("try { ")
                .Append(CommandModels.RuntimeType(model.Type))
                .Append(
                    ".__ValidateContract(); } catch(global::System.Exception) { errors.Add(new("
                )
                .Append(Quote(model.Id))
                .Append(",\"json_contract_invalid\")); }\n");
        }

        foreach (var composite in composites)
        {
            b.Append("try { ")
                .Append(CompositeModels.Helper(composite))
                .Append(
                    ".__ValidateContract(); } catch(global::System.Exception) { errors.Add(new("
                )
                .Append(Quote(composite.Id))
                .Append(",\"json_contract_invalid\")); }\n");
        }

        b.Append("return errors.AsReadOnly(); } }\npublic static class ")
            .Append(name)
            .Append(
                "Services { public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddLlmCommandsFrom"
            )
            .Append(name.Substring(0, name.Length - "Commands".Length))
            .Append(
                "(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,global::Zeeq.AgentCmd.CommandRuntime runtime) => global::Zeeq.AgentCmd.CommandServices.AddLlmCommands<"
            )
            .Append(name)
            .Append(
                ">(services,runtime); internal static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddGeneratedCommands(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) => global::Zeeq.AgentCmd.CommandServices.AddLlmCommands<"
            )
            .Append(name)
            .Append(">(services); } }\n");
        output.AddSource(
            "AgentCmd.CommandRegistration.g.cs",
            SourceText.From(b.ToString(), Encoding.UTF8)
        );
    }

    static string Quote(string value) => SymbolDisplay.FormatLiteral(value, true);

    internal static string Frame(string value) =>
        value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;

    internal static string Attributes(ISymbol symbol) =>
        string.Join(
            "",
            symbol
                .GetAttributes()
                .Where(a =>
                    a.AttributeClass?.ContainingNamespace.ToDisplayString()
                        is "System.Text.Json.Serialization"
                            or "System.Diagnostics.CodeAnalysis"
                )
                .Select(Attribute)
                .OrderBy(a => a, StringComparer.Ordinal)
        );

    internal static string Attribute(AttributeData attribute) =>
        Frame(
            attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                ?? ""
        )
        + Frame(string.Join("", attribute.ConstructorArguments.Select(Constant)))
        + Frame(
            string.Join(
                "",
                attribute
                    .NamedArguments.OrderBy(a => a.Key, StringComparer.Ordinal)
                    .Select(a => Frame(a.Key) + Constant(a.Value))
            )
        );

    static string Constant(TypedConstant value) =>
        Frame(value.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "")
        + Frame(
            value.Kind == TypedConstantKind.Array ? string.Join("", value.Values.Select(Constant))
            : value.Value is ITypeSymbol type ? CommandModels.RuntimeType(type)
            : Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture)
                ?? "null"
        );

    static string Hash(string value)
    {
        using (var sha = SHA256.Create())
        {
            return string.Concat(
                sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2"))
            );
        }
    }

    static void Error(SourceProductionContext context, SyntaxNode syntax, string message) =>
        context.ReportDiagnostic(Diagnostic.Create(Invalid, syntax.GetLocation(), message));
}
