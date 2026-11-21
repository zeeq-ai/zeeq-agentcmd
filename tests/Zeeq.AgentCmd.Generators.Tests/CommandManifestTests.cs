using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Zeeq.AgentCmd.Generators;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Checks portable contract disclosure and export without starting a consuming application.</summary>
public sealed class CommandManifestTests
{
    private const string Simple = """
        #nullable enable
        using Zeeq.AgentCmd;
        using System.Text.Json.Serialization;
        namespace Fixture;
        public sealed record Input(string Document, [property: RuntimeOverride] string? Locale = null);
        public sealed record Output([property: JsonPropertyName("total")] decimal Total, string? Note);
        public sealed partial class Command : PromptCommand<Input, Output>
        {
            public const string Id = "invoice.extract";
            public const string Profile = "private-profile";
            public const string UserTemplate = "PRIVATE_BASELINE {Document}";
            public const string SystemTemplate = "Use {Locale}";
        }
        """;

    [Test]
    public async Task AuthoredCatalogWithoutExportRemainsCompatible()
    {
        await Assert.That(ExportCatalog<AuthoredCatalog>()).IsNull();
    }

    private static CommandManifest? ExportCatalog<TCatalog>()
        where TCatalog : IGeneratedCommandCatalog => TCatalog.ExportManifest();

    private sealed class AuthoredCatalog : IGeneratedCommandCatalog
    {
        public static IReadOnlyList<CommandRegistration> Commands => [];

        public static void Register(
            Microsoft.Extensions.DependencyInjection.IServiceCollection services
        ) { }

        public static IReadOnlyList<CommandContractError> ValidateContracts() => [];
    }

    [Test]
    public async Task ManifestMatchesExecutionIdentityAndSeparatesDisclosure()
    {
        var generated = Generate(Simple);
        var manifest = Read(generated.Source);
        var command = manifest.Content.GetProperty("commands")[0];
        await Assert.That(manifest.FormatVersion).IsEqualTo(1);
        await Assert.That(manifest.CatalogId).IsEqualTo("ManifestFixture");
        await Assert.That(manifest.Json.Contains("PRIVATE_BASELINE")).IsFalse();
        await Assert.That(manifest.Json.Contains("private-profile")).IsFalse();
        await Assert
            .That(command.GetProperty("baselineTemplates").ValueKind)
            .IsEqualTo(JsonValueKind.Null);
        await Assert
            .That(command.GetProperty("capabilities").GetProperty("templateOverrides").GetBoolean())
            .IsTrue();
        await Assert
            .That(command.GetProperty("runtimeOverrides")[0].GetProperty("name").GetString())
            .IsEqualTo("Locale");
        var disclosed = Read(generated.Source, true);
        await Assert.That(disclosed.Json.Contains("PRIVATE_BASELINE")).IsTrue();
        await Assert.That(disclosed.Json.Contains("private-profile")).IsTrue();
        await Assert.That(disclosed.Sha256 == manifest.Sha256).IsFalse();

        // Loading a compiled fixture is only a test oracle. The exporter below consumes source data.
        using var binary = new MemoryStream();
        var emit = generated.Compilation.Emit(binary);
        await Assert
            .That(
                string.Join(
                    "\n",
                    emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                )
            )
            .IsEqualTo("");
        var assembly = System.Reflection.Assembly.Load(binary.ToArray());
        var descriptor = assembly
            .GetType("Fixture.Command")!
            .GetProperty("Descriptor", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        await Assert
            .That(command.GetProperty("definitionHash").GetString())
            .IsEqualTo(
                (string)descriptor.GetType().GetProperty("DefinitionHash")!.GetValue(descriptor)!
            );
        var binding = descriptor.GetType().GetProperty("RuntimeBinding")!.GetValue(descriptor)!;
        await Assert
            .That(command.GetProperty("contractSignature").GetString())
            .IsEqualTo(
                (string)binding.GetType().GetProperty("ContractSignature")!.GetValue(binding)!
            );
        var exported = (CommandManifest)
            assembly
                .GetType("Zeeq.AgentCmd.Generated.ManifestFixtureCommands")!
                .GetMethod("ExportManifest")!
                .Invoke(null, [false])!;
        await Assert.That(exported.Json).IsEqualTo(manifest.Json);
        await Assert.That(exported.Sha256).IsEqualTo(manifest.Sha256);
    }

    [Test]
    public async Task DeclaredShapePreservesWireNamesRequirednessNullsCollectionsAndBase64()
    {
        var source = Simple.Replace(
            "public sealed record Output([property: JsonPropertyName(\"total\")] decimal Total, string? Note);",
            """
            public enum Kind : byte { First = 1, Second = 2 }
            public sealed record Line([property: JsonPropertyName("amount")] decimal Amount);
            public sealed record Output(
                [property: JsonPropertyName("line_items")] System.Collections.Generic.List<Line> Lines,
                byte[] Bytes, Kind Kind, int? Count,
                System.Collections.Generic.Dictionary<string, string> Labels,
                string? Note = null,
                [property: JsonIgnore] string? Ignored = null);
            """,
            StringComparison.Ordinal
        );
        var command = Read(Generate(source).Source).Content.GetProperty("commands")[0];
        var output = command.GetProperty("output");
        await Assert.That(output.GetProperty("status").GetString()).IsEqualTo("declared");
        var schema = output.GetProperty("schema");
        var properties = schema.GetProperty("properties");
        await Assert
            .That(
                properties
                    .GetProperty("line_items")
                    .GetProperty("items")
                    .GetProperty("properties")
                    .GetProperty("amount")
                    .GetProperty("type")
                    .GetString()
            )
            .IsEqualTo("number");
        await Assert
            .That(properties.GetProperty("Bytes").GetProperty("contentEncoding").GetString())
            .IsEqualTo("base64");
        await Assert
            .That(properties.GetProperty("Kind").GetProperty("maximum").GetInt32())
            .IsEqualTo(255);
        await Assert
            .That(
                properties
                    .GetProperty("Count")
                    .GetProperty("anyOf")[1]
                    .GetProperty("type")
                    .GetString()
            )
            .IsEqualTo("null");
        await Assert
            .That(
                properties
                    .GetProperty("Labels")
                    .GetProperty("additionalProperties")
                    .GetProperty("type")
                    .GetString()
            )
            .IsEqualTo("string");
        await Assert.That(properties.TryGetProperty("Ignored", out _)).IsFalse();
        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(v => v.GetString())
            .ToArray();
        await Assert.That(required.Contains("line_items")).IsTrue();
        await Assert.That(required.Contains("Note")).IsFalse();
    }

    [Test]
    public async Task TemplateOnlyCommandsRemainUsableAndShapeSignaturesDoNotTrackPromptEdits()
    {
        var source = Simple.Replace("[property: RuntimeOverride] ", "", StringComparison.Ordinal);
        var first = Read(Generate(source).Source).Content.GetProperty("commands")[0];
        var changed = Read(
            Generate(
                source.Replace("PRIVATE_BASELINE", "New prompt", StringComparison.Ordinal)
            ).Source
        )
            .Content.GetProperty("commands")[0];
        await Assert
            .That(first.GetProperty("capabilities").GetProperty("templateOverrides").GetBoolean())
            .IsTrue();
        await Assert
            .That(first.GetProperty("capabilities").GetProperty("parameterOverrides").GetBoolean())
            .IsFalse();
        await Assert.That(first.GetProperty("runtimeOverrides").GetArrayLength()).IsEqualTo(0);
        await Assert
            .That(first.GetProperty("input").GetProperty("signature").GetString())
            .IsEqualTo(changed.GetProperty("input").GetProperty("signature").GetString());
        await Assert
            .That(first.GetProperty("contractSignature").GetString())
            .IsEqualTo(changed.GetProperty("contractSignature").GetString());
        await Assert
            .That(
                first.GetProperty("definitionHash").GetString()
                    == changed.GetProperty("definitionHash").GetString()
            )
            .IsFalse();
        var resultChanged = Read(
            Generate(source.Replace("decimal Total", "int Total", StringComparison.Ordinal)).Source
        )
            .Content.GetProperty("commands")[0];
        await Assert
            .That(first.GetProperty("input").GetProperty("signature").GetString())
            .IsEqualTo(resultChanged.GetProperty("input").GetProperty("signature").GetString());
        await Assert
            .That(
                first.GetProperty("output").GetProperty("signature").GetString()
                    == resultChanged.GetProperty("output").GetProperty("signature").GetString()
            )
            .IsFalse();
    }

    [Test]
    public async Task SameInputsProduceIdenticalBytesAcrossCultureAndSyntaxTreeOrder()
    {
        const string other = """
            namespace Other;
            public sealed record Input(string Text);
            public sealed record Output(bool Value);
            public sealed partial class Command : Zeeq.AgentCmd.PromptCommand<Input, Output>
            {
                public const string Id = "a.other";
                public const string UserTemplate = "{Text}";
            }
            """;
        var before = Read(Generate(Simple, other).Source);
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var after = Read(Generate(other, Simple).Source);
            await Assert.That(after.Json).IsEqualTo(before.Json);
            await Assert.That(after.Sha256).IsEqualTo(before.Sha256);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
        await Assert
            .That(before.Content.GetProperty("commands")[0].GetProperty("id").GetString())
            .IsEqualTo("a.other");
    }

    [Test]
    public async Task CustomConverterHasNoInferredWireSchemaAndIsNotConstructedByExport()
    {
        var source = Simple.Replace(
            "public sealed record Output([property: JsonPropertyName(\"total\")] decimal Total, string? Note);",
            """
            [JsonConverter(typeof(WireConverter))]
            public sealed record Output(decimal Total, string? Note);
            public sealed class WireConverter : JsonConverter<Output>
            {
                public WireConverter() => throw new System.InvalidOperationException("MUST_NOT_CONSTRUCT");
                public override Output Read(ref System.Text.Json.Utf8JsonReader reader, System.Type type, System.Text.Json.JsonSerializerOptions options) => throw new System.NotSupportedException();
                public override void Write(System.Text.Json.Utf8JsonWriter writer, Output value, System.Text.Json.JsonSerializerOptions options) => throw new System.NotSupportedException();
            }
            """,
            StringComparison.Ordinal
        );
        var generated = Generate(source);
        var output = Read(generated.Source)
            .Content.GetProperty("commands")[0]
            .GetProperty("output");
        await Assert.That(output.GetProperty("status").GetString()).IsEqualTo("opaque");
        await Assert.That(output.GetProperty("schema").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(output.GetProperty("signature").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert
            .That(output.GetProperty("reasons").GetRawText().Contains("custom_converter"))
            .IsTrue();
        using var binary = new MemoryStream();
        await Assert.That(generated.Compilation.Emit(binary).Success).IsTrue();
        var assembly = System.Reflection.Assembly.Load(binary.ToArray());
        var exported = (CommandManifest)
            assembly
                .GetType("Zeeq.AgentCmd.Generated.ManifestFixtureCommands")!
                .GetMethod("ExportManifest")!
                .Invoke(null, [false])!;
        await Assert.That(exported.Json).IsEqualTo(Read(generated.Source).Json);
    }

    [Test]
    public async Task AuthoredContextIsOpaqueAndDoesNotGuessNamingPolicy()
    {
        var source =
            Simple.Replace(
                "public sealed partial class Command",
                "[PromptCommand(\"invoice.extract\", JsonContext = typeof(AuthoredContext))]\npublic sealed partial class Command",
                StringComparison.Ordinal
            )
            + """

                [JsonSerializable(typeof(Input))]
                [JsonSerializable(typeof(Output))]
                public sealed class AuthoredContext : JsonSerializerContext
                {
                    public AuthoredContext() : base(null) { }
                    public static AuthoredContext Default => throw new System.InvalidOperationException("MUST_NOT_RUN_CONTEXT");
                    protected override System.Text.Json.JsonSerializerOptions? GeneratedSerializerOptions => null;
                    public override System.Text.Json.Serialization.Metadata.JsonTypeInfo? GetTypeInfo(System.Type type) => null;
                }
                """;
        var command = Read(Generate(source).Source).Content.GetProperty("commands")[0];
        await Assert
            .That(command.GetProperty("input").GetProperty("status").GetString())
            .IsEqualTo("opaque");
        await Assert
            .That(command.GetProperty("output").GetProperty("status").GetString())
            .IsEqualTo("opaque");
        await Assert
            .That(command.GetProperty("placeholders")[0].GetProperty("jsonName").ValueKind)
            .IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task RecursiveShapeIsExplicitlyOpaque()
    {
        var source = Simple.Replace(
            "decimal Total, string? Note",
            "decimal Total, Output? Next",
            StringComparison.Ordinal
        );
        var shape = Read(Generate(source).Source)
            .Content.GetProperty("commands")[0]
            .GetProperty("output");
        await Assert.That(shape.GetProperty("status").GetString()).IsEqualTo("opaque");
        await Assert
            .That(shape.GetProperty("reasons").GetRawText().Contains("recursive_contract"))
            .IsTrue();
    }

    [Test]
    public async Task CompositionOnlyCatalogExportsWholeShapeAndSlots()
    {
        // The pinned analyzer-host Roslyn predates extension blocks. The SDK-built
        // ManifestExport fixture separately compiles the full generated composition.
        var generated = Generate(
            false,
            """
            namespace Fixture;
            [Zeeq.AgentCmd.CompositeOutput("invoice.whole")]
            public sealed partial record Whole(string Vendor, decimal Total);
            """
        );
        var manifest = Read(generated.Source);
        await Assert.That(manifest.Content.GetProperty("commands").GetArrayLength()).IsEqualTo(0);
        var whole = manifest.Content.GetProperty("compositions")[0];
        await Assert.That(whole.GetProperty("id").GetString()).IsEqualTo("invoice.whole");
        await Assert
            .That(whole.GetProperty("output").GetProperty("status").GetString())
            .IsEqualTo("declared");
        await Assert.That(whole.GetProperty("slots").GetArrayLength()).IsEqualTo(2);
    }

    [Test]
    public async Task ExportScriptWritesExactBytesAndDigestWithoutApplicationExecution()
    {
        var generated = Generate(
            Simple
                + "\npublic static class Application { public static void Main() => throw new System.Exception(\"DO_NOT_RUN\"); }"
        );
        var directory = ArtifactDirectory();
        var source = Path.Combine(directory, "AgentCmd.Manifest.g.cs");
        await File.WriteAllTextAsync(source, generated.Source);
        var output = Path.Combine(directory, "manifest.json");
        await Assert.That(await Export(source, output)).IsEqualTo(0);
        await Assert
            .That(await File.ReadAllTextAsync(output))
            .IsEqualTo(Read(generated.Source).Json);
        var bytes = await File.ReadAllBytesAsync(output);
        await Assert
            .That((await File.ReadAllTextAsync(output + ".sha256")).Trim())
            .IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await Assert.That(await Export(source, output, true)).IsEqualTo(0);
        await Assert
            .That(await File.ReadAllTextAsync(output))
            .IsEqualTo(Read(generated.Source, true).Json);
    }

    [Test]
    public async Task ExportRejectsCorruptionAndDuplicateMarkersWithoutOverwritingOutput()
    {
        var generated = Generate(Simple).Source;
        var directory = ArtifactDirectory();
        var source = Path.Combine(directory, "AgentCmd.Manifest.g.cs");
        var output = Path.Combine(directory, "preserved.json");
        await File.WriteAllTextAsync(output, "unchanged");
        var marker = Regex
            .Match(generated, "^// agentcmd-manifest:structural:.*$", RegexOptions.Multiline)
            .Value;
        foreach (
            var invalid in new[]
            {
                generated + "\n" + marker,
                generated.Replace(
                    marker,
                    marker[..(marker.LastIndexOf(':') + 1)]
                        + Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")),
                    StringComparison.Ordinal
                ),
            }
        )
        {
            await File.WriteAllTextAsync(source, invalid);
            await Assert.That(await Export(source, output)).IsEqualTo(1);
            await Assert.That(await File.ReadAllTextAsync(output)).IsEqualTo("unchanged");
        }
    }

    private static CommandManifest Read(string source, bool disclose = false)
    {
        var policy = disclose ? "baseline-templates" : "structural";
        var match = Regex.Match(
            source,
            "^// agentcmd-manifest:" + policy + ":([0-9a-f]{64}):([A-Za-z0-9+/=]+)$",
            RegexOptions.Multiline
        );
        var result = CommandManifest.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[2].Value))
        );
        if (result.Sha256 != match.Groups[1].Value)
        {
            throw new InvalidOperationException("Manifest digest mismatch.");
        }
        return result;
    }

    private static (Compilation Compilation, string Source) Generate(params string[] source) =>
        Generate(true, source);

    private static (Compilation Compilation, string Source) Generate(
        bool checkGeneratedCompilation,
        params string[] source
    )
    {
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
            "ManifestFixture",
            source.Select(text =>
                CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview))
            ),
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new IIncrementalGenerator[] { new PromptGenerator(), new MetadataGenerator() }.Select(
                g => g.AsSourceGenerator()
            ),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview)
        );
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var updated,
            out var diagnostics
        );
        var errors = diagnostics
            .Concat((checkGeneratedCompilation ? updated : compilation).GetDiagnostics())
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidOperationException(
                string.Join("\n", errors.Select(e => e.ToString()))
            );
        }
        return (
            updated,
            driver
                .GetRunResult()
                .Results.SelectMany(r => r.GeneratedSources)
                .Single(s => s.HintName == "AgentCmd.Manifest.g.cs")
                .SourceText.ToString()
        );
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))
        )
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string ArtifactDirectory()
    {
        var directory = Path.Combine(
            RepositoryRoot(),
            "artifacts",
            "control-plane",
            "manifest",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<int> Export(string source, string output, bool disclose = false)
    {
        var start = new ProcessStartInfo("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(
            Path.Combine(RepositoryRoot(), "scripts", "export-command-manifest.py")
        );
        start.ArgumentList.Add("--generated-file");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        if (disclose)
        {
            start.ArgumentList.Add("--include-baseline-templates");
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await File.WriteAllTextAsync(output + ".log", await stdout + await stderr);
        return process.ExitCode;
    }
}
