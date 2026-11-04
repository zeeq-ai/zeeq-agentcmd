using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zeeq.AgentCmd.Generators;

/// <summary>Generates typed profile keys from source-authored profile catalog declarations.</summary>
[Generator]
public sealed class ProfileGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Invalid = new DiagnosticDescriptor(
        "AGENTPROFILE001",
        "Invalid profile catalog",
        "{0}",
        "AgentCmd",
        DiagnosticSeverity.Error,
        true
    );

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var profiles = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "Zeeq.AgentCmd.ModelProfileAttribute",
                (node, _) => node is PropertyDeclarationSyntax,
                (ctx, _) => ctx
            )
            .Collect();
        context.RegisterSourceOutput(
            profiles,
            (output, values) =>
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (
                    var group in values.GroupBy(
                        v => ((IPropertySymbol)v.TargetSymbol).ContainingType,
                        SymbolEqualityComparer.Default
                    )
                )
                {
                    var type = (INamedTypeSymbol)group.Key!;
                    var good = true;
                    Action<string, Location> error = (message, location) =>
                    {
                        output.ReportDiagnostic(Diagnostic.Create(Invalid, location, message));
                        good = false;
                    };
                    var location = type.Locations.First();
                    if (
                        type.ContainingType != null
                        || type.IsStatic
                        || !type.IsSealed
                        || type.TypeParameters.Length != 0
                        || type.ContainingNamespace.IsGlobalNamespace
                        || type.DeclaredAccessibility != Accessibility.Public
                        || type.DeclaringSyntaxReferences.Any(r =>
                            !(r.GetSyntax() is ClassDeclarationSyntax c)
                            || !c.Modifiers.Any(SyntaxKind.PartialKeyword)
                        )
                    )
                    {
                        error(
                            "Profile catalog must be a public, sealed, non-static, top-level partial class in a namespace",
                            location
                        );
                    }

                    if (
                        type.InstanceConstructors.Any(c => !c.IsImplicitlyDeclared)
                        || type.GetMembers("Instance").Length > 0
                    )
                    {
                        error(
                            "The generator owns the catalog constructor and Instance member",
                            location
                        );
                    }

                    var entries = new List<Tuple<string, string>>();
                    foreach (var value in group)
                    {
                        var property = (IPropertySymbol)value.TargetSymbol;
                        var syntax = (PropertyDeclarationSyntax)value.TargetNode;
                        var id =
                            value.Attributes[0].ConstructorArguments.FirstOrDefault().Value
                            as string;
                        if (
                            property.IsStatic
                            || property.DeclaredAccessibility != Accessibility.Public
                            || property.Type.ToDisplayString() != "Zeeq.AgentCmd.ProfileKey"
                            || property.NullableAnnotation == NullableAnnotation.Annotated
                            || property.SetMethod != null
                            || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword)
                            || syntax.AccessorList == null
                            || syntax.AccessorList.Accessors.Count != 1
                            || syntax.AccessorList.Accessors[0].Body != null
                            || syntax.AccessorList.Accessors[0].ExpressionBody != null
                        )
                        {
                            error(
                                "Profile must be a public non-nullable instance partial ProfileKey getter without a body",
                                property.Locations.First()
                            );
                        }

                        if (
                            string.IsNullOrWhiteSpace(id)
                            || id!.Length > 128
                            || !id.All(c =>
                                c <= 127
                                && (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
                            )
                            || !ids.Add(id)
                        )
                        {
                            error(
                                "Profile ID must be valid and unique within the compilation",
                                property.Locations.First()
                            );
                        }
                        else
                        {
                            entries.Add(Tuple.Create(property.Name, id));
                        }
                    }
                    if (!good)
                    {
                        continue;
                    }

                    var qualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                    var text = new StringBuilder(
                        "// <auto-generated/>\n#pragma warning disable CS1591\n#nullable enable\nnamespace "
                    )
                        .Append(type.ContainingNamespace.ToDisplayString())
                        .Append(" { public sealed partial class ")
                        .Append(type.Name)
                        .Append(" : global::Zeeq.AgentCmd.IProfileCatalog<")
                        .Append(qualified)
                        .Append("> {\nprivate ")
                        .Append(type.Name)
                        .Append("() {}\npublic static ")
                        .Append(qualified)
                        .Append(" Instance { get; } = new ")
                        .Append(qualified)
                        .Append("();\n");
                    foreach (var entry in entries)
                    {
                        var name = "@" + entry.Item1;
                        text.Append(
                                "private static readonly global::Zeeq.AgentCmd.ProfileKey __profile_"
                            )
                            .Append(entry.Item1)
                            .Append(" = global::Zeeq.AgentCmd.ProfileKey.Declare(")
                            .Append(SymbolDisplay.FormatLiteral(entry.Item2, true))
                            .Append(");\n")
                            .Append("public partial global::Zeeq.AgentCmd.ProfileKey ")
                            .Append(name)
                            .Append(" => __profile_")
                            .Append(entry.Item1)
                            .Append(";\n");
                    }
                    text.Append("} }\n");
                    output.AddSource(
                        type.ToDisplayString().Replace('.', '_') + ".Profiles.g.cs",
                        text.ToString()
                    );
                }
            }
        );
    }
}
