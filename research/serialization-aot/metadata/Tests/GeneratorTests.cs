using MetadataSpike;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>Protects the failure boundary for unsupported contract shapes.</summary>
public sealed class GeneratorTests
{
    [Test]
    public async Task UnsupportedTypeProducesDiagnostic()
    {
        const string source = """
            namespace SerializationSpikes;
            public abstract class PromptCommand<TParameters, TResult>;
            public sealed class Command : PromptCommand<System.Uri, string>;
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Negative",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGenerators(compilation);
        var diagnostics = driver.GetRunResult().Diagnostics;
        await Assert
            .That(
                diagnostics.Any(diagnostic =>
                    diagnostic.Id == "META001" && diagnostic.Severity == DiagnosticSeverity.Error
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task AuthoredPropertyMutationChangesMetadata()
    {
        const string source = """
            namespace SerializationSpikes;
            public abstract class PromptCommand<TParameters, TResult>;
            public sealed record Input(string Text);
            public sealed class Command : PromptCommand<Input, string>;
            """;
        var before = Generate(source);
        var after = Generate(source.Replace("string Text", "string Text, int Count"));
        await Assert.That(before.Contains("PropertyName = \"Count\"")).IsFalse();
        await Assert.That(after.Contains("PropertyName = \"Count\"")).IsTrue();
    }

    [Test]
    [Arguments(
        "public Input(string name) {} public Input(string name, int count) {}",
        "multiple public parameterized constructors require [JsonConstructor]"
    )]
    [Arguments(
        "[JsonConstructor] public Input(string name) {} [JsonConstructor] public Input(string name, int count) {}",
        "multiple constructors have [JsonConstructor]"
    )]
    [Arguments(
        "public Input() {} [JsonConstructor] private Input(string name) {}",
        "[JsonConstructor] must select a public constructor in this spike"
    )]
    public async Task Invalid_constructor_choices_fail_with_a_useful_diagnostic(
        string constructors,
        string reason
    )
    {
        var source = $$"""
            using System.Text.Json.Serialization;
            namespace SerializationSpikes;
            public abstract class PromptCommand<TParameters, TResult>;
            public sealed class Input
            {
                public string Name { get; init; } = "";
                public int Count { get; init; }
                {{constructors}}
            }
            public sealed class Command : PromptCommand<Input, string>;
            """;

        await Assert
            .That(
                RunGenerator(source)
                    .Diagnostics.Any(diagnostic =>
                        diagnostic.Id == "META001"
                        && diagnostic.Severity == DiagnosticSeverity.Error
                        && diagnostic.GetMessage().Contains(reason)
                    )
            )
            .IsTrue();
    }

    [Test]
    public async Task Unknown_ignore_conditions_are_diagnosed_instead_of_dropping_the_member()
    {
        const string source = """
            using System.Text.Json.Serialization;
            namespace SerializationSpikes;
            public abstract class PromptCommand<TParameters, TResult>;
            public sealed class Input
            {
                [JsonIgnore(Condition = (JsonIgnoreCondition)99)]
                public string? Note { get; init; }
            }
            public sealed class Command : PromptCommand<Input, string>;
            """;

        await Assert
            .That(
                RunGenerator(source)
                    .Diagnostics.Any(diagnostic =>
                        diagnostic.Id == "META001"
                        && diagnostic.GetMessage().Contains("unrecognized JsonIgnore condition")
                    )
            )
            .IsTrue();
    }

    private static string Generate(string source) =>
        RunGenerator(source).Results.Single().GeneratedSources.Single().SourceText.ToString();

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Mutation",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }
}
