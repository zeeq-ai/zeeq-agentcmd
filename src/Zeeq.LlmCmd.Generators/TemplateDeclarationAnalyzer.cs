using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Zeeq.LlmCmd.Generators;

/// <summary>Finds likely template declaration typos that optional system prompts would otherwise hide.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TemplateDeclarationAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor PossibleTypo = new DiagnosticDescriptor(
        "LLMCMD002",
        "Possible misspelled prompt template",
        "'{0}' is not a recognized prompt template declaration; did you mean '{1}'?",
        "LlmCmd",
        DiagnosticSeverity.Warning,
        true
    );

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(PossibleTypo);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var command = start.Compilation.GetTypeByMetadataName("Zeeq.LlmCmd.PromptCommand`2");
            if (command is null)
            {
                return;
            }

            start.RegisterSymbolAction(node => Analyze(node, command), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, INamedTypeSymbol command)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!SymbolEqualityComparer.Default.Equals(type.BaseType?.OriginalDefinition, command))
        {
            return;
        }

        foreach (var member in type.GetMembers())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (member.IsImplicitlyDeclared || !IsStaticString(member))
            {
                continue;
            }

            var expected = ExpectedName(member.Name);
            if (expected is null || type.GetMembers(expected).Length != 0)
            {
                continue;
            }
            var location = member.Locations.FirstOrDefault(item => item.IsInSource);
            if (location is not null)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(PossibleTypo, location, member.Name, expected)
                );
            }
        }
    }

    private static bool IsStaticString(ISymbol symbol) =>
        symbol switch
        {
            IFieldSymbol field => field.IsStatic
                && field.Type.SpecialType == SpecialType.System_String,
            IPropertySymbol property => property.IsStatic
                && property.Type.SpecialType == SpecialType.System_String,
            _ => false,
        };

    private static string? ExpectedName(string name)
    {
        if (string.Equals(name, "SystemPromptTemplate", StringComparison.OrdinalIgnoreCase))
        {
            return "SystemTemplate";
        }
        if (
            string.Equals(name, "UserPromptTemplate", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "PromptTemplate", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "UserTemplate";
        }
        foreach (var expected in new[] { "UserTemplate", "SystemTemplate" })
        {
            if (name != expected && IsSingleEdit(name, expected))
            {
                return expected;
            }
        }
        return null;
    }

    // Limit suggestions to one edit (or a transposition) to avoid guessing at
    // unrelated helper names in an authored command.
    private static bool IsSingleEdit(string value, string expected)
    {
        if (Math.Abs(value.Length - expected.Length) > 1)
        {
            return false;
        }
        if (value.Length == expected.Length)
        {
            var first = -1;
            var second = -1;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.ToUpperInvariant(value[i]) == char.ToUpperInvariant(expected[i]))
                {
                    continue;
                }
                if (first < 0)
                {
                    first = i;
                }
                else if (second < 0)
                {
                    second = i;
                }
                else
                {
                    return false;
                }
            }
            return first < 0
                || second < 0
                || (
                    second == first + 1
                    && char.ToUpperInvariant(value[first])
                        == char.ToUpperInvariant(expected[second])
                    && char.ToUpperInvariant(value[second])
                        == char.ToUpperInvariant(expected[first])
                );
        }

        var longer = value.Length > expected.Length ? value : expected;
        var shorter = value.Length > expected.Length ? expected : value;
        var skip = 0;
        for (var i = 0; i < shorter.Length; i++)
        {
            if (char.ToUpperInvariant(longer[i + skip]) == char.ToUpperInvariant(shorter[i]))
            {
                continue;
            }
            if (skip != 0)
            {
                return false;
            }
            skip = 1;
            if (char.ToUpperInvariant(longer[i + skip]) != char.ToUpperInvariant(shorter[i]))
            {
                return false;
            }
        }
        return true;
    }
}
