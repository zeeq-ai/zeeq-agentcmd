using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Zeeq.AgentCmd.Generators;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Checks authored command declarations before they become executable prompts.</summary>
public sealed class PromptDeclarationGeneratorTests
{
    private const string NoSystem = """
        const string Id = "fixture.templates";
        const string UserTemplate = "Read {Text}";
        """;
    private const string Valid = NoSystem + "\nconst string SystemTemplate = \"\";";

    [Test]
    public async Task RequiredIdAndUserTemplateMustBeConstant()
    {
        foreach (
            var (members, expected) in new[]
            {
                (
                    "static readonly string Id = \"fixture\"; const string UserTemplate = \"{Text}\"; const string SystemTemplate = \"\";",
                    "Id must be"
                ),
                (
                    "const string Id = \"fixture\"; static readonly string UserTemplate = \"{Text}\"; const string SystemTemplate = \"\";",
                    "UserTemplate must be"
                ),
                (
                    "const string Id = \"fixture\"; const string? UserTemplate = null; const string SystemTemplate = \"\";",
                    "UserTemplate must be"
                ),
                (
                    "const string UserTemplate = \"{Text}\"; const string SystemTemplate = \"\";",
                    "stable const string Id"
                ),
            }
        )
        {
            var result = Generate(members);
            await Assert
                .That(
                    result.Errors.Any(d =>
                        d.Id == "AGENTCMD001" && d.GetMessage().Contains(expected)
                    )
                )
                .IsTrue();
        }
    }

    [Test]
    public async Task ExplicitlyDeclaredOptionalSettingsCannotBeSilentlyIgnored()
    {
        foreach (
            var (member, expected) in new[]
            {
                ("static readonly string SystemTemplate = \"system\";", "SystemTemplate must be"),
                ("static string SystemTemplate => \"system\";", "SystemTemplate must be"),
                ("const int SystemTemplate = 1;", "SystemTemplate must be"),
                ("const string? SystemTemplate = null;", "SystemTemplate must be"),
                ("static readonly string Profile = \"fast\";", "Profile must be"),
                ("const int Profile = 1;", "Profile must be"),
                ("const string? Profile = null;", "Profile must be"),
                ("static readonly int Revision = 2;", "Revision must be"),
                ("const string Revision = \"2\";", "Revision must be"),
                ("static int Revision => 2;", "Revision must be"),
            }
        )
        {
            var result = Generate(
                (expected == "SystemTemplate must be" ? NoSystem : Valid) + "\n" + member
            );
            await Assert
                .That(
                    result.Errors.Any(d =>
                        d.Id == "AGENTCMD001" && d.GetMessage().Contains(expected)
                    )
                )
                .IsTrue();
        }
    }

    [Test]
    public async Task AttributeIdDoesNotMaskAnInvalidAuthoredId()
    {
        var result = Generate(
            "static readonly string Id = \"fixture.templates\"; const string UserTemplate = \"{Text}\"; const string SystemTemplate = \"\";",
            "[PromptCommand(\"fixture.templates\")]"
        );

        await Assert.That(result.Errors.Any(d => d.GetMessage().Contains("Id must be"))).IsTrue();
    }

    [Test]
    public async Task ExplicitAttributeMetadataMustAgreeWithAuthoredConstants()
    {
        var members = Valid + "\nconst int Revision = 2; const string Profile = \"fast\";";
        var conflicting = Generate(
            members,
            "[PromptCommand(\"other\", Revision = 3, Profile = \"slow\")]"
        );
        foreach (var name in new[] { "Id", "Revision", "Profile" })
        {
            await Assert
                .That(
                    conflicting.Errors.Any(d =>
                        d.GetMessage().Contains(name + " and [PromptCommand]")
                    )
                )
                .IsTrue();
        }

        var matching = Generate(
            members,
            "[PromptCommand(\"fixture.templates\", Revision = 2, Profile = \"fast\")]"
        );
        await Assert.That(matching.Errors.Length).IsEqualTo(0);

        // Unspecified named arguments do not override the authored constants.
        var omitted = Generate(members, "[PromptCommand(\"fixture.templates\")]");
        await Assert.That(omitted.Errors.Length).IsEqualTo(0);
    }

    [Test]
    public async Task NullAttributeProfileIsAnError()
    {
        var result = Generate(Valid, "[PromptCommand(\"fixture.templates\", Profile = null)]");
        await Assert
            .That(result.Errors.Any(d => d.GetMessage().Contains("Profile must be a non-null")))
            .IsTrue();
    }

    [Test]
    public async Task BothTemplatesRenderFromTheSameTypedInput()
    {
        var result = Generate(NoSystem + "\nconst string SystemTemplate = \"Use {Locale}\";");
        await Assert.That(result.Errors.Length).IsEqualTo(0);
        using var stream = new MemoryStream();
        var emitted = result.Compilation.Emit(stream);
        await Assert.That(emitted.Success).IsTrue();
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var input = Activator.CreateInstance(assembly.GetType("Fixture.Input")!, "invoice", "fr")!;
        var command = assembly.GetType("Fixture.Command")!;
        var descriptor = command
            .GetProperty(
                "Descriptor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            )!
            .GetValue(null)!;
        var render = (Delegate)descriptor.GetType().GetProperty("Render")!.GetValue(descriptor)!;
        var renderSystem = (Delegate)
            descriptor.GetType().GetProperty("RenderSystem")!.GetValue(descriptor)!;
        await Assert.That((string)render.DynamicInvoke(input)!).IsEqualTo("Read invoice");
        await Assert.That((string)renderSystem.DynamicInvoke(input)!).IsEqualTo("Use fr");
    }

    [Test]
    public async Task MissingUserTemplateFailsButSystemTemplateIsOptional()
    {
        var missingSystem = Generate(NoSystem);
        await Assert.That(missingSystem.Errors.Length).IsEqualTo(0);
        var missingUser = Generate(
            "const string Id = \"fixture\"; const string SystemTemplate = \"system\";"
        );
        await Assert
            .That(
                missingUser.Errors.Any(d =>
                    d.Id == "AGENTCMD001" && d.GetMessage().Contains("UserTemplate must be")
                )
            )
            .IsTrue();
        var explicitEmpty = Generate(Valid);
        await Assert.That(explicitEmpty.Errors.Length).IsEqualTo(0);
    }

    [Test]
    public async Task BlankUserTemplatesFailWhileWhitespaceSystemTemplatesRemainValid()
    {
        foreach (var blank in new[] { "", "   ", "\\t", "\u2003" })
        {
            var result = Generate(
                "const string Id = \"fixture\"; const string UserTemplate = \"" + blank + "\";"
            );
            await Assert
                .That(result.Errors.Any(d => d.GetMessage().Contains("non-whitespace text")))
                .IsTrue();
        }

        var system = Generate(NoSystem + "\nconst string SystemTemplate = \"   \";");
        await Assert.That(system.Errors.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidSystemPlaceholderFailsGeneration()
    {
        foreach (var text in new[] { "{Unknown}", "{Text.Length}", "{Locale" })
        {
            var result = Generate(NoSystem + "\nconst string SystemTemplate = \"" + text + "\";");
            await Assert
                .That(result.Errors.Any(d => d.GetMessage().Contains("Invalid template")))
                .IsTrue();
        }
    }

    private static (Compilation Compilation, Diagnostic[] Errors) Generate(
        string members,
        string attribute = ""
    )
    {
        var source = $$"""
            #nullable enable
            using Zeeq.AgentCmd;
            namespace Fixture;
            public sealed record Input(string Text, string Locale);
            public sealed record Output(string Value);
            {{attribute}}
            public sealed partial class Command : PromptCommand<Input, Output>
            {
                {{members}}
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
            "PromptDeclarationFixture" + Guid.NewGuid().ToString("N"),
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
        return (updated, diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
    }
}
