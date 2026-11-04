using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Zeeq.AgentCmd.Generators;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Checks conservative suggestions for prompt declarations that would be ignored.</summary>
public sealed class TemplateDeclarationAnalyzerTests
{
    [Test]
    public async Task WarnsOnLikelyTemplateTyposAndCommonAlternativeNames()
    {
        foreach (
            var (declaration, expected) in new[]
            {
                ("const string SystemTemplte = \"system\";", "SystemTemplate"),
                ("static readonly string systemTemplate = \"system\";", "SystemTemplate"),
                ("static string SystemTempltae => \"system\";", "SystemTemplate"),
                ("const string SystemPromptTemplate = \"system\";", "SystemTemplate"),
                ("const string UserPromptTemplate = \"user\";", "UserTemplate"),
                ("const string PromptTemplate = \"user\";", "UserTemplate"),
                ("const string UserTemplte = \"user\";", "UserTemplate"),
            }
        )
        {
            var diagnostics = await Analyze(
                "public sealed partial class Command : PromptCommand<object, object> { "
                    + declaration
                    + " }"
            );
            await Assert.That(diagnostics.Length).IsEqualTo(1);
            await Assert.That(diagnostics[0].Id).IsEqualTo("AGENTCMD002");
            await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
            await Assert.That(diagnostics[0].GetMessage().Contains(expected)).IsTrue();
        }
    }

    [Test]
    public async Task IgnoresUnrelatedMembersAndHelpersWhenTheRealTemplateExists()
    {
        var diagnostics = await Analyze(
            """
            public sealed partial class Command : PromptCommand<object, object>
            {
                const string UserTemplate = "user";
                const string SystemTemplate = "system";
                const string SystemTemplates = "helper";
                static string CompletelyUnrelated = "helper";
                string SystemTemplte = "instance data";
            }
            public sealed class Other
            {
                const string SystemTemplte = "unrelated class";
            }
            """
        );

        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task PartialCommandProducesOneDiagnosticAtTheAuthoredMember()
    {
        var diagnostics = await Analyze(
            """
            public sealed partial class Command : PromptCommand<object, object>
            {
                static string SystemTemplte => "system";
            }
            public sealed partial class Command { }
            """
        );

        await Assert.That(diagnostics.Length).IsEqualTo(1);
        await Assert
            .That(
                diagnostics[0]
                    .Location.SourceTree!.GetText()
                    .ToString(diagnostics[0].Location.SourceSpan)
            )
            .IsEqualTo("SystemTemplte");
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string declarations)
    {
        var source = $$"""
            #nullable enable
            using Zeeq.AgentCmd;
            namespace Zeeq.AgentCmd
            {
                public abstract class PromptCommand<TParameters, TResult> { }
            }
            namespace Fixture
            {
                {{declarations}}
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "TemplateAnalyzerFixture",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        return await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new TemplateDeclarationAnalyzer())
            )
            .GetAnalyzerDiagnosticsAsync();
    }
}
