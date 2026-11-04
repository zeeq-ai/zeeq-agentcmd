using Microsoft.CodeAnalysis;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Checks the runtime template grammar against the generated renderer's rules.</summary>
public sealed class RuntimeTemplateBindingTests
{
    [Test]
    public async Task EscapesBracesAndDoesNotReparseValues()
    {
        var values = new Dictionary<string, string?> { ["Name"] = "{Other}", ["Empty"] = null };
        await Assert
            .That(RuntimeTemplate.Render("{{{Name}}}:{Empty}", values))
            .IsEqualTo("{{Other}}:");
    }

    [Test]
    public async Task RejectsUnknownMalformedAndCaseMismatchedPlaceholders()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "value",
        };
        foreach (var template in new[] { "{name}", "{Missing}", "{Name", "Name}", "{Name{X}" })
        {
            var rejected = false;
            try
            {
                RuntimeTemplate.Render(template, values);
            }
            catch (ArgumentException)
            {
                rejected = true;
            }

            await Assert.That(rejected).IsTrue();
        }
    }

    [Test]
    public async Task RejectsOversizedTemplates()
    {
        var rejected = false;
        try
        {
            RuntimeTemplate.Render("abcd", new Dictionary<string, string?>(), 3);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        await Assert.That(rejected).IsTrue();
    }
}

/// <summary>Exercises generated overrides and diagnostics through a compiled command.</summary>
public sealed class RuntimeOverrideGeneratorTests
{
    private const string Command = """
        #nullable enable
        using Zeeq.AgentCmd;
        namespace Fixture;
        public sealed record Input(string Document, [property: RuntimeOverride] string? Locale, [property: RuntimeOverride] int Count);
        public sealed record Output(string Value);
        public sealed partial class Command : PromptCommand<Input, Output>
        {
            public const string Id = "fixture.override";
            public const string UserTemplate = "{Document} {Locale} {Count}";
        }
        """;

    [Test]
    public async Task OverridesAreAtomicTypedAndSnapshotIsSafe()
    {
        var result = Generate(Command);
        await Assert.That(result.Errors).IsEqualTo("");
        using var stream = new MemoryStream();
        var emitted = result.Compilation.Emit(stream);
        await Assert
            .That(
                string.Join(
                    "\n",
                    emitted.Diagnostics.Where(d =>
                        d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                    )
                )
            )
            .IsEqualTo("");
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var inputType = assembly.GetType("Fixture.Input")!;
        var input = Activator.CreateInstance(inputType, "caller", "en", 3)!;
        var bindingType = assembly.GetType("Fixture.Command+__RuntimeBinding")!;
        var binding = Activator.CreateInstance(bindingType, nonPublic: true)!;
        var apply = bindingType.GetMethod("TryApplyOverrides")!;
        var valid = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["Locale"] = System.Text.Json.JsonSerializer.SerializeToElement("fr"),
            ["Count"] = System.Text.Json.JsonSerializer.SerializeToElement(9),
        };
        object?[] args = [input, valid, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsTrue();
        await Assert
            .That((string?)inputType.GetProperty("Locale")!.GetValue(args[2]))
            .IsEqualTo("fr");
        await Assert.That((int)inputType.GetProperty("Count")!.GetValue(args[2])!).IsEqualTo(9);
        await Assert.That((int)inputType.GetProperty("Count")!.GetValue(input)!).IsEqualTo(3);
        valid["Document"] = System.Text.Json.JsonSerializer.SerializeToElement("forbidden");
        args = [input, valid, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsFalse();
        await Assert.That(ReferenceEquals(input, args[2])).IsTrue();
        valid.Remove("Document");
        valid["Count"] = System.Text.Json.JsonSerializer.SerializeToElement("wrong");
        args = [input, valid, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsFalse();
        await Assert.That(ReferenceEquals(input, args[2])).IsTrue();
        foreach (var invalid in new[] { "null", "2147483648", "-2147483649", "1.5" })
        {
            valid["Count"] = System.Text.Json.JsonDocument.Parse(invalid).RootElement.Clone();
            args = [input, valid, null, null];
            await Assert.That((bool)apply.Invoke(binding, args)!).IsFalse();
            await Assert.That(ReferenceEquals(input, args[2])).IsTrue();
        }
        valid["Count"] = System.Text.Json.JsonSerializer.SerializeToElement(2);
        valid["Locale"] = System.Text.Json.JsonSerializer.SerializeToElement<string?>(null);
        args = [input, valid, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsTrue();
        await Assert.That(inputType.GetProperty("Locale")!.GetValue(args[2]) is null).IsTrue();
        var frozen =
            (IReadOnlyDictionary<string, string?>)
                bindingType.GetMethod("Freeze")!.Invoke(binding, [input])!;
        var commandType = assembly.GetType("Fixture.Command")!;
        var descriptor = commandType
            .GetProperty(
                "Descriptor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            )!
            .GetValue(null)!;
        var renderer = (Delegate)descriptor.GetType().GetProperty("Render")!.GetValue(descriptor)!;
        var compiled = (string)renderer.DynamicInvoke(input)!;
        await Assert
            .That(RuntimeTemplate.Render("{Document} {Locale} {Count}", frozen))
            .IsEqualTo(compiled);
        object?[] snapshot = [input, null];
        await Assert
            .That((bool)bindingType.GetMethod("TrySnapshot")!.Invoke(binding, snapshot)!)
            .IsTrue();
        await Assert.That(ReferenceEquals(input, snapshot[1])).IsTrue();
    }

    [Test]
    public async Task ContractSignatureExcludesTemplateTextAndIncludesOverridePermission()
    {
        static string Signature(string source)
        {
            var result = Generate(source);
            using var stream = new MemoryStream();
            var emitted = result.Compilation.Emit(stream);
            if (!emitted.Success)
            {
                throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
            }
            var type = System
                .Reflection.Assembly.Load(stream.ToArray())
                .GetType("Fixture.Command+__RuntimeBinding")!;
            var binding = Activator.CreateInstance(type, nonPublic: true)!;
            return (string)type.GetProperty("ContractSignature")!.GetValue(binding)!;
        }
        var original = Signature(Command);
        var differentTemplate = Signature(
            Command.Replace(
                "{Document} {Locale} {Count}",
                "New: {Document}",
                StringComparison.Ordinal
            )
        );
        var changedPermission = Signature(
            Command.Replace(
                "[property: RuntimeOverride] int Count",
                "int Count",
                StringComparison.Ordinal
            )
        );
        await Assert.That(original).IsEqualTo(differentTemplate);
        await Assert.That(original == changedPermission).IsFalse();
    }

    [Test]
    public async Task ScalarKindsCompileAndRejectInvalidValues()
    {
        const string source = """
            #nullable enable
            using Zeeq.AgentCmd;
            namespace Fixture;
            public enum Mode : byte { First = 1, Second = 2 }
            public sealed record Input([property: RuntimeOverride] char Letter, [property: RuntimeOverride] bool Enabled, [property: RuntimeOverride] double? Ratio, [property: RuntimeOverride] Mode Mode);
            public sealed record Output(string Value);
            public sealed partial class Command : PromptCommand<Input, Output>
            {
                public const string Id = "fixture.scalars";
                public const string UserTemplate = "{Letter} {Enabled} {Ratio} {Mode}";
            }
            """;
        var result = Generate(source);
        await Assert.That(result.Errors).IsEqualTo("");
        using var stream = new MemoryStream();
        var emitted = result.Compilation.Emit(stream);
        await Assert
            .That(
                string.Join(
                    "\n",
                    emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                )
            )
            .IsEqualTo("");
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var inputType = assembly.GetType("Fixture.Input")!;
        var mode = Enum.ToObject(assembly.GetType("Fixture.Mode")!, 1);
        var input = Activator.CreateInstance(inputType, 'a', false, 1.25, mode)!;
        var type = assembly.GetType("Fixture.Command+__RuntimeBinding")!;
        var binding = Activator.CreateInstance(type, nonPublic: true)!;
        var apply = type.GetMethod("TryApplyOverrides")!;
        var values = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["Letter"] = System.Text.Json.JsonSerializer.SerializeToElement("z"),
            ["Enabled"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            ["Ratio"] = System.Text.Json.JsonSerializer.SerializeToElement(2.5),
            ["Mode"] = System.Text.Json.JsonSerializer.SerializeToElement(2),
        };
        object?[] args = [input, values, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsTrue();
        await Assert.That((char)inputType.GetProperty("Letter")!.GetValue(args[2])!).IsEqualTo('z');
        await Assert
            .That((double)inputType.GetProperty("Ratio")!.GetValue(args[2])!)
            .IsEqualTo(2.5);
        values["Letter"] = System.Text.Json.JsonSerializer.SerializeToElement("long");
        args = [input, values, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsFalse();
        values["Letter"] = System.Text.Json.JsonSerializer.SerializeToElement("z");
        values["Mode"] = System.Text.Json.JsonSerializer.SerializeToElement(256);
        args = [input, values, null, null];
        await Assert.That((bool)apply.Invoke(binding, args)!).IsFalse();
    }

    [Test]
    public async Task RejectsMutableAndDerivedState()
    {
        foreach (
            var replacement in new[]
            {
                "public sealed record Input(string Document, [property: RuntimeOverride] string? Locale, [property: RuntimeOverride] int Count) { public int Mutable { get; set; } }",
                "public record Input(string Document, [property: RuntimeOverride] string? Locale, [property: RuntimeOverride] int Count);",
                "public sealed record Input(string Document, [property: RuntimeOverride] string? Locale, [property: RuntimeOverride] int Count, System.Collections.Generic.List<string> Items);",
                "public sealed record Input(string Document, [property: RuntimeOverride] string? Locale, [property: RuntimeOverride] int Count) { public int Derived => Count + 1; }",
            }
        )
        {
            var source = Command.Replace(
                "public sealed record Input(string Document, [property: RuntimeOverride] string? Locale, [property: RuntimeOverride] int Count);",
                replacement,
                StringComparison.Ordinal
            );
            await Assert
                .That(
                    Generate(source)
                        .Errors.Contains("Runtime overrides require", StringComparison.Ordinal)
                )
                .IsTrue();
        }
    }

    private static (Microsoft.CodeAnalysis.Compilation Compilation, string Errors) Generate(
        string source
    )
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path))
            .Cast<Microsoft.CodeAnalysis.MetadataReference>()
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
            references.Add(
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(assembly.Location)
            );
        }
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "BindingFixture" + Guid.NewGuid().ToString("N"),
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: Microsoft.CodeAnalysis.NullableContextOptions.Enable
            )
        );
        Microsoft.CodeAnalysis.GeneratorDriver driver =
            Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver.Create(
                new Microsoft.CodeAnalysis.IIncrementalGenerator[]
                {
                    new PromptGenerator(),
                    new Zeeq.AgentCmd.Generators.MetadataGenerator(),
                }
            );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updated,
            out var diagnostics
        );
        var errors = string.Join(
            "\n",
            diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
        );
        return (updated, errors);
    }
}
