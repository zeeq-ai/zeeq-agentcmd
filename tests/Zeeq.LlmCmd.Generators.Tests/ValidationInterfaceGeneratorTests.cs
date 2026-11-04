using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Zeeq.LlmCmd.Generators;

namespace Zeeq.LlmCmd.Generators.Tests;

/// <summary>Checks that opted-in validators cannot be silently skipped by generated commands.</summary>
public sealed class ValidationInterfaceGeneratorTests
{
    [Test]
    public async Task CompilerRequiresAnImplementationWhenACommandOptsIn()
    {
        var result = Generate(
            "IInputValidator<Input>",
            "public static string? ValidateImput(Input input) => null;"
        );

        await Assert.That(result.CompilerDiagnostics.Any(d => d.Id == "CS0535")).IsTrue();
        await Assert.That(result.GeneratorDiagnostics.Any(d => d.Id == "LLMCMD001")).IsTrue();
    }

    [Test]
    public async Task PrivateMethodDoesNotImplicitlyImplementAStaticInterface()
    {
        var result = Generate(
            "IInputValidator<Input>",
            "static string? ValidateInput(Input input) => null;"
        );

        await Assert
            .That(result.CompilerDiagnostics.Any(d => d.Id is "CS0737" or "CS0535"))
            .IsTrue();
    }

    [Test]
    public async Task MissingInterfaceHookDoesNotFallBackToALegacyMethod()
    {
        var result = Generate(
            "IInputValidator<Input>",
            "static string? Validate(Input input) => null;"
        );

        await Assert.That(result.CompilerDiagnostics.Any(d => d.Id == "CS0535")).IsTrue();
        await Assert
            .That(
                result.GeneratorDiagnostics.Any(d =>
                    d.Id == "LLMCMD001"
                    && d.GetMessage().Contains("must be implemented", StringComparison.Ordinal)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task InterfacesMustUseTheCorrespondingCommandType()
    {
        foreach (
            var result in new[]
            {
                Generate(
                    "IInputValidator<Output>",
                    "public static string? ValidateInput(Output input) => null;"
                ),
                Generate(
                    "IInputValidator<Input>, IInputValidator<Output>",
                    """
                    static string? IInputValidator<Input>.ValidateInput(Input input) => null;
                    static string? IInputValidator<Output>.ValidateInput(Output input) => null;
                    """
                ),
                Generate(
                    "IResultValidator<Input>",
                    "public static string? ValidateResult(Input result) => null;"
                ),
            }
        )
        {
            await Assert
                .That(
                    result.GeneratorDiagnostics.Any(d =>
                        d.Id == "LLMCMD001"
                        && d.GetMessage()
                            .Contains("exact corresponding type", StringComparison.Ordinal)
                    )
                )
                .IsTrue();
        }
    }

    [Test]
    public async Task ExplicitAndLegacyHooksCannotCompeteForTheSamePhase()
    {
        var result = Generate(
            "IInputValidator<Input>",
            """
            static string? IInputValidator<Input>.ValidateInput(Input input) => null;
            static string? ValidateInput(Input input) => null;
            """
        );

        await Assert
            .That(
                result.GeneratorDiagnostics.Any(d =>
                    d.Id == "LLMCMD001"
                    && d.GetMessage()
                        .Contains("Ambiguous validation hooks", StringComparison.Ordinal)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task MatchingImplicitHookIsNotCountedTwice()
    {
        var result = Generate(
            "IInputValidator<Input>",
            "public static string? ValidateInput(Input input) => null;"
        );

        await Assert.That(result.GeneratorDiagnostics.Length).IsEqualTo(0);
        await Assert.That(result.CompilerDiagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task StructuralValidationRunsBeforeTheInterfaceHookExactlyOnce()
    {
        var result = Generate(
            "IInputValidator<Input>",
            """
            public static int Calls;
            public static string? ValidateInput(Input input)
            {
                Calls++;
                return input.Text == "blocked" ? "blocked" : null;
            }
            """
        );
        using var stream = new MemoryStream();
        var emitted = result.Compilation.Emit(stream);
        await Assert.That(emitted.Success).IsTrue();
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var command = assembly.GetType("Fixture.Command")!;
        var input = assembly.GetType("Fixture.Input")!;
        var descriptor = command
            .GetProperty(
                "Descriptor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            )!
            .GetValue(null)!;
        var validate = (Delegate)
            descriptor.GetType().GetProperty("ValidateInput")!.GetValue(descriptor)!;
        var invalid = Activator.CreateInstance(input, new object?[] { null })!;
        await Assert
            .That((string?)validate.DynamicInvoke(invalid))
            .IsEqualTo("A non-nullable value was null");
        await Assert.That((int)command.GetField("Calls")!.GetValue(null)!).IsEqualTo(0);

        var blocked = Activator.CreateInstance(input, "blocked")!;
        await Assert.That((string?)validate.DynamicInvoke(blocked)).IsEqualTo("blocked");
        await Assert.That((int)command.GetField("Calls")!.GetValue(null)!).IsEqualTo(1);
    }

    [Test]
    public async Task InputAndResultInterfacesCanUseTheSameType()
    {
        var result = Generate(
            "IInputValidator<Input>, IResultValidator<Input>",
            """
            static string? IInputValidator<Input>.ValidateInput(Input input) => null;
            static string? IResultValidator<Input>.ValidateResult(Input result) => null;
            """,
            resultType: "Input"
        );

        await Assert.That(result.GeneratorDiagnostics.Length).IsEqualTo(0);
        await Assert.That(result.CompilerDiagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SharedLegacyValidatorConflictsWithADifferentInterfaceHook()
    {
        var result = Generate(
            "IResultValidator<Input>",
            """
            static string? IResultValidator<Input>.ValidateResult(Input result) => null;
            static string? Validate(Input value) => null;
            """,
            resultType: "Input"
        );

        await Assert
            .That(
                result.GeneratorDiagnostics.Any(d =>
                    d.Id == "LLMCMD001"
                    && d.GetMessage()
                        .Contains("Ambiguous validation hooks", StringComparison.Ordinal)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task ValidatorInterfaceMustBeDeclaredDirectlyOnTheCommand()
    {
        var result = Generate(
            "ICompanyValidator",
            "public static string? ValidateInput(Input input) => null;",
            declarations: "public interface ICompanyValidator : IInputValidator<Input> { }"
        );

        await Assert
            .That(
                result.GeneratorDiagnostics.Any(d =>
                    d.Id == "LLMCMD001"
                    && d.GetMessage().Contains("declared directly", StringComparison.Ordinal)
                )
            )
            .IsTrue();
    }

    private static (
        Diagnostic[] GeneratorDiagnostics,
        Diagnostic[] CompilerDiagnostics,
        Compilation Compilation
    ) Generate(
        string interfaces,
        string methods,
        string resultType = "Output",
        string declarations = ""
    )
    {
        var source = $$"""
            #nullable enable
            using Zeeq.LlmCmd;
            namespace Fixture;
            public sealed record Input(string Text);
            public sealed record Output(string Value);
            {{declarations}}
            public sealed partial class Command : PromptCommand<Input, {{resultType}}>, {{interfaces}}
            {
                public const string Id = "fixture.interface-validation";
                public const string UserTemplate = "{Text}";
                {{methods}}
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToList();
        foreach (
            var assembly in new[]
            {
                typeof(PromptCommand<,>).Assembly,
                typeof(Microsoft.Extensions.AI.IChatClient).Assembly,
                typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly,
            }
        )
        {
            references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        var compilation = CSharpCompilation.Create(
            "ValidationFixture" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new IIncrementalGenerator[] { new PromptGenerator(), new MetadataGenerator() }
        );
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);
        return (
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray(),
            updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray(),
            updated
        );
    }
}
