using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

[Generator]
public sealed class PromptGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor Invalid = new("PROMPT001", "Invalid template", "{0}", "Prompt", DiagnosticSeverity.Error, true);
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(c => c.AddSource("PromptAttribute.g.cs", SourceText.From(@"using System;
[AttributeUsage(AttributeTargets.Class)] public sealed class PromptAttribute : Attribute {
 public PromptAttribute(string template) { }
 public PromptAttribute(string id, string template) { } }
", Encoding.UTF8)));
        var commands = context.SyntaxProvider.ForAttributeWithMetadataName("PromptAttribute", (n, _) => n is ClassDeclarationSyntax, (c, _) => c);
        context.RegisterSourceOutput(commands, Generate);
    }
    static void Generate(SourceProductionContext context, GeneratorAttributeSyntaxContext c)
    {
        var cls = (INamedTypeSymbol)c.TargetSymbol;
        var methods = cls.GetMembers("Define").OfType<IMethodSymbol>().ToArray();
        if (c.Attributes.Length != 1) { Error("Expected exactly one Prompt attribute."); return; }
        if (methods.Length != 1) { Error("Expected exactly one Define method."); return; }
        var method = methods[0];
        if ( !cls.IsStatic || cls.ContainingType != null || cls.ContainingNamespace.IsGlobalNamespace || !method.IsStatic || !method.ReturnsVoid || !method.IsPartialDefinition || method.Parameters.Any(p => p.RefKind != RefKind.None)) { Error("Expected a top-level static partial class in a namespace with a static partial void Define(value parameters)."); return; }
        var attr = c.Attributes[0];
        var args = attr.ConstructorArguments;
        if ((args.Length != 1 && args.Length != 2) || args.Any(a => a.Value is not string)) { Error("Prompt ID and template must be non-null constant strings."); return; }
        string id = args.Length == 1 ? cls.ToDisplayString() : (string)args[0].Value!;
        string template = (string)args[args.Length - 1].Value!;
        var names = new HashSet<string>();
        for (int i = 0; i < template.Length; i++) {
            if (template[i] == '{') {
                if (i + 1 < template.Length && template[i + 1] == '{') { i++; continue; }
                int end = template.IndexOf('}', i + 1);
                if (end < 0) { Error("Unclosed placeholder."); return; }
                string name = template.Substring(i + 1, end - i - 1);
                if (!SyntaxFacts.IsValidIdentifier(name)) { Error("Invalid placeholder: " + name); return; }
                names.Add(name);
                i = end;
            } else if (template[i] == '}') {
                if (i + 1 < template.Length && template[i + 1] == '}') { i++; continue; }
                Error("Unescaped closing brace."); return;
            }
        }
        var ps = method.Parameters;
        foreach (string n in names) if (!ps.Any(p => p.Name == n)) { Error("Unresolved placeholder: " + n); return; }
        foreach (var p in ps) if (!names.Contains(p.Name)) { Error("Unused parameter: " + p.Name); return; }
        if (ps.Any(p => p.IsOptional && !p.HasExplicitDefaultValue)) { Error("Optional parameters must declare an explicit default value."); return; }
        string Type(IParameterSymbol p) => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
        string Setter(IParameterSymbol p) => "With" + char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1);
        if (ps.Any(p => p.Name == "Model") || ps.Select(Setter).Distinct(StringComparer.Ordinal).Count() != ps.Length || ps.Any(p => p.IsOptional && Setter(p) == "WithModel")) { Error("Parameter names collide with generated members."); return; }
        string Default(IParameterSymbol p) {
            object? v = p.ExplicitDefaultValue;
            if (v is null) return "default";
            if (p.Type.TypeKind == TypeKind.Enum) return "(" + Type(p) + ")" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            if (v is float f) return float.IsNaN(f) ? "global::System.Single.NaN" : float.IsPositiveInfinity(f) ? "global::System.Single.PositiveInfinity" : float.IsNegativeInfinity(f) ? "global::System.Single.NegativeInfinity" : f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "F";
            if (v is double d) return double.IsNaN(d) ? "global::System.Double.NaN" : double.IsPositiveInfinity(d) ? "global::System.Double.PositiveInfinity" : double.IsNegativeInfinity(d) ? "global::System.Double.NegativeInfinity" : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "D";
            if (v is decimal m) return m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "M";
            return SymbolDisplay.FormatPrimitive(v, quoteStrings: true, useHexadecimalNumbers: false);
        }
        string Declaration(IParameterSymbol p, bool defaults) => Type(p) + " @" + p.Name + (defaults && p.HasExplicitDefaultValue ? " = " + Default(p) : "");
        string Params(IEnumerable<IParameterSymbol> items, bool defaults = false) => string.Join(", ", items.Select(p => Declaration(p, defaults)));
        string Args(IEnumerable<IParameterSymbol> items) => string.Join(", ", items.Select(p => "@" + p.Name));
        var required = ps.Where(p => !p.IsOptional).ToArray();
        var optional = ps.Where(p => p.IsOptional).ToArray();
        var b = new StringBuilder();
        b.AppendLine("// <auto-generated/>\n#nullable enable").Append("namespace ").Append(cls.ContainingNamespace.ToDisplayString()).AppendLine(" {");
        b.Append("partial class ").Append(cls.Name).AppendLine(" {");
        b.Append("static partial void Define(").Append(Params(ps)).AppendLine(") { }");
        b.Append("public const string Id = ").Append(SymbolDisplay.FormatLiteral(id, true)).AppendLine(";");
        b.Append("public const string Template = ").Append(SymbolDisplay.FormatLiteral(template, true)).AppendLine(";");
        b.Append("public sealed record Bound(").Append(Params(ps, true)); if (ps.Length > 0) b.Append(", ");
        b.AppendLine("string? Model = null) { public Bound WithModel(string model) => this with { Model = model };");
        foreach (var p in optional) b.Append("public Bound ").Append(Setter(p)).Append('(').Append(Declaration(p, false)).Append(") => this with { @").Append(p.Name).Append(" = @").Append(p.Name).AppendLine(" };");
        b.AppendLine("}");
        b.Append("public static Bound Bind(").Append(Params(ps, true)).Append(") => new Bound(").Append(Args(ps)).AppendLine(");");
        b.AppendLine("public static Stage0 Start() => new Stage0();");
        for (int i = 0; i <= required.Length; i++) {
            b.Append("public sealed class Stage").Append(i).AppendLine(" {");
            foreach (var p in required.Take(i)) b.Append("private readonly ").Append(Type(p)).Append(" @").Append(p.Name).AppendLine(";");
            b.Append("internal Stage").Append(i).Append('(').Append(Params(required.Take(i))).AppendLine(") {");
            foreach (var p in required.Take(i)) b.Append("this.@").Append(p.Name).Append(" = @").Append(p.Name).AppendLine(";");
            b.AppendLine("}");
            if (i < required.Length) { var p = required[i]; b.Append("public Stage").Append(i + 1).Append(' ').Append(Setter(p)).Append('(').Append(Type(p)).Append(" @").Append(p.Name).Append(") => new Stage").Append(i + 1).Append('(').Append(Args(required.Take(i + 1))).AppendLine(");"); }
            else b.Append("public Bound Build() => Bind(").Append(Args(required)).AppendLine(");");
            b.AppendLine("}");
        }
        b.AppendLine("} }");
        context.AddSource(cls.ToDisplayString().Replace('.', '_') + ".g.cs", SourceText.From(b.ToString(), Encoding.UTF8));
        void Error(string message) => context.ReportDiagnostic(Diagnostic.Create(Invalid, c.TargetNode.GetLocation(), message));
    }
}
