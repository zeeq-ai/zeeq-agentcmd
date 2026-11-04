using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Zeeq.AgentCmd.Generators;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Checks automatic metadata discovery and its explicit rejection boundary.</summary>
public sealed class MetadataGeneratorTests
{
    private const string Stubs = """
        #nullable enable
        namespace Zeeq.AgentCmd
        {
            public abstract class PromptCommand<TP, TR> { }
            public static class GeneratedJsonConverterMetadata
            {
                public static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> AttributeDeclared<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> value) => value;
            }
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
            public sealed class CompositeOutputAttribute : System.Attribute
            {
                public System.Type? JsonContext { get; set; }
            }
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class PromptCommandAttribute : System.Attribute
            {
                public System.Type? JsonContext { get; set; }
            }
        }
        """;

    [Test]
    public async Task DiscoversCommandAndCompositeAndCompilesIgnoredConstructorMember()
    {
        const string source = """
            #nullable enable
            using System.Text.Json.Serialization;
            namespace Test;
            public record Input(string Document, [property: JsonIgnore] string? Internal = null);
            public record Result(string Value);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            [Zeeq.AgentCmd.CompositeOutput]
            public record Whole(Result Result);
            """;
        var result = Generate(source);
        await Assert
            .That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(result.Output.Contains("global::Test.Input")).IsTrue();
        await Assert.That(result.Output.Contains("global::Test.Whole")).IsTrue();
        await Assert.That(result.Output.Contains("JsonIgnoreCondition.Always")).IsTrue();
        await Assert.That(result.CompileErrors.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UnrelatedSameNamedAttributesDoNotChangeMetadata()
    {
        const string source = """
            using System;
            namespace Acme
            {
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class JsonDerivedTypeAttribute : Attribute
                {
                    public JsonDerivedTypeAttribute(Type type, string tag) { }
                }
                [AttributeUsage(AttributeTargets.Class)]
                public sealed class JsonPolymorphicAttribute : Attribute
                {
                    public string? TypeDiscriminatorPropertyName { get; set; }
                }
                [AttributeUsage(AttributeTargets.Property)]
                public sealed class JsonPropertyNameAttribute : Attribute
                {
                    public JsonPropertyNameAttribute(string name) { }
                }
            }
            namespace Test
            {
                public record Input(string Document);
                [Acme.JsonDerivedType(typeof(Unrelated), "fake")]
                [Acme.JsonPolymorphic(TypeDiscriminatorPropertyName = "$fake")]
                public record Result([property: Acme.JsonPropertyName("wrong")] string Value);
                public record Unrelated(string Secret);
                public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            }
            """;
        var result = Generate(source);
        await Assert
            .That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(result.CompileErrors.Length).IsEqualTo(0);
        await Assert.That(result.Output.Contains("global::Test.Unrelated")).IsFalse();
        await Assert.That(result.Output.Contains("$fake")).IsFalse();
        await Assert.That(result.Output.Contains("JsonPropertyName = \"wrong\"")).IsFalse();
    }

    [Test]
    public async Task UnsupportedDiscriminatorFormsProduceDiagnosticsWithoutCrashing()
    {
        const string source = """
            using System.Text.Json.Serialization;
            namespace Test;
            public record Input(string Document);
            [JsonDerivedType(typeof(Derived))]
            public abstract record MissingTag;
            public sealed record Derived : MissingTag;
            [JsonDerivedType(typeof(NumberDerived), 7)]
            public abstract record NumericTag;
            public sealed record NumberDerived : NumericTag;
            public record Result(MissingTag First, NumericTag Second);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var result = Generate(source);
        await Assert.That(result.Diagnostics.Count(d => d.Id == "AGENTMETA001")).IsEqualTo(2);
        await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse();
        await Assert
            .That(result.Output.Contains("CreateObjectInfo<global::Test.MissingTag>"))
            .IsFalse();
        await Assert
            .That(result.Output.Contains("CreateObjectInfo<global::Test.NumericTag>"))
            .IsFalse();
    }

    [Test]
    public async Task RejectsUnknownAttributesAndUnsupportedFrameworkTypes()
    {
        const string source = """
            using System.Text.Json.Serialization;
            namespace Test;
            public record Input(string Document);
            public record Result([property: JsonPropertyOrder(2)] string Value, System.Uri Link);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var result = Generate(source);
        await Assert.That(result.Diagnostics.Count(d => d.Id == "AGENTMETA001") >= 2).IsTrue();
    }

    [Test]
    public async Task RejectsNullJsonNameAndUnsupportedIgnoreConditions()
    {
        const string source = """
            #nullable enable
            using System.Text.Json.Serialization;
            namespace Test;
            public record Input(string Document);
            public record BadName([property: JsonPropertyName(null!)] string Value);
            public record ReadIgnored([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)] string Value);
            public record WriteIgnored([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)] string Value);
            public record Result(BadName First, ReadIgnored Second, WriteIgnored Third);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var result = Generate(source);
        await Assert.That(result.Diagnostics.Count(d => d.Id == "AGENTMETA001")).IsEqualTo(3);
        await Assert.That(result.Diagnostics.Any(d => d.Id == "CS8785")).IsFalse();
        await Assert
            .That(result.Output.Contains("CreateObjectInfo<global::Test.BadName>"))
            .IsFalse();
        await Assert
            .That(result.Output.Contains("CreateObjectInfo<global::Test.ReadIgnored>"))
            .IsFalse();
        await Assert
            .That(result.Output.Contains("CreateObjectInfo<global::Test.WriteIgnored>"))
            .IsFalse();
    }

    [Test]
    public async Task ExplicitContextBypassesUnsupportedGraph()
    {
        const string source = """
            namespace Test;
            public record Input(string Document);
            public record Result(System.Uri Link);
            public class Context { }
            [Zeeq.AgentCmd.PromptCommand(JsonContext = typeof(Context))]
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var result = Generate(source);
        await Assert.That(result.Diagnostics.Any(d => d.Id == "AGENTMETA001")).IsFalse();
        await Assert.That(result.Output).IsEqualTo("");
    }

    [Test]
    public async Task RejectsConflictingNullableGenericContracts()
    {
        const string source = """
            #nullable enable
            namespace Test;
            public record Input(string Document);
            public record Box<T>(T Value);
            public record Result(Box<string> First, Box<string?> Second);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var result = Generate(source);
        await Assert
            .That(
                result.Diagnostics.Any(d =>
                    d.Id == "AGENTMETA001" && d.GetMessage().Contains("conflicting nullable")
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task ConstructorSelectionAndIgnoreNeverFollowStj()
    {
        const string source = """
            #nullable enable
            using System.Text.Json.Serialization;
            namespace Test;
            public record Input(string Document);
            public sealed class Result
            {
                public Result() { Name = "wrong"; }
                [JsonConstructor] public Result(string name) { Name = name; }
                public string Name { get; }
                [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Note { get; init; }
            }
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var compilation = Compile(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        using var stream = new MemoryStream();
        var emitted = updated.Emit(stream);
        await Assert.That(emitted.Success).IsTrue();
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var resolver = (System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)
            Activator.CreateInstance(
                assembly.GetType("Zeeq.AgentCmd.Generated.AgentCmdJsonResolver")!,
                nonPublic: true
            )!;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            DefaultIgnoreCondition = System
                .Text
                .Json
                .Serialization
                .JsonIgnoreCondition
                .WhenWritingNull,
        };
        var info = options.GetTypeInfo(assembly.GetType("Test.Result")!);
        var result = System.Text.Json.JsonSerializer.Deserialize("{\"Name\":\"chosen\"}", info)!;
        var json = System.Text.Json.JsonSerializer.Serialize(result, info);
        await Assert.That(json.Contains("\"Name\":\"chosen\"")).IsTrue();
        await Assert.That(json.Contains("\"Note\":null")).IsTrue();
    }

    [Test]
    public async Task ConcreteConverterIsGeneratedForNestedValue()
    {
        const string source = """
            #nullable enable
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            namespace Test;
            public record Input(string Document);
            [JsonConverter(typeof(CodeConverter))] public readonly record struct Code(string Value);
            public sealed class CodeConverter : JsonConverter<Code>
            {
                private string? first;
                public override Code Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString()!);
                public override void Write(Utf8JsonWriter writer, Code value, JsonSerializerOptions options)
                {
                    first ??= options.PropertyNamingPolicy?.ConvertName(value.Value) ?? value.Value;
                    writer.WriteStringValue(first);
                }
            }
            public record Result(Code Code);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var result = Generate(source);
        await Assert
            .That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(result.CompileErrors.Length).IsEqualTo(0);
        await Assert
            .That(result.Output.Contains("GeneratedJsonConverterMetadata.AttributeDeclared"))
            .IsTrue();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(source), out var updated, out _);
        using var stream = new MemoryStream();
        await Assert.That(updated.Emit(stream).Success).IsTrue();
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var resolverType = assembly.GetType("Zeeq.AgentCmd.Generated.AgentCmdJsonResolver")!;
        var resolver1 = (System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)
            Activator.CreateInstance(resolverType, nonPublic: true)!;
        var resolver2 = (System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)
            Activator.CreateInstance(resolverType, nonPublic: true)!;
        var options1 = new System.Text.Json.JsonSerializerOptions
        {
            TypeInfoResolver = resolver1,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
        var options2 = new System.Text.Json.JsonSerializerOptions
        {
            TypeInfoResolver = resolver2,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseUpper,
        };
        var codeType = assembly.GetType("Test.Code")!;
        await Assert
            .That(
                ReferenceEquals(
                    options1.GetTypeInfo(codeType).Converter,
                    options2.GetTypeInfo(codeType).Converter
                )
            )
            .IsFalse();
        var value = Activator.CreateInstance(codeType, "HelloWorld")!;
        await Assert
            .That(System.Text.Json.JsonSerializer.Serialize(value, options1.GetTypeInfo(codeType)))
            .IsEqualTo("\"helloWorld\"");
        await Assert
            .That(System.Text.Json.JsonSerializer.Serialize(value, options2.GetTypeInfo(codeType)))
            .IsEqualTo("\"HELLO_WORLD\"");
    }

    [Test]
    public async Task ByteArrayUsesStjBase64Converter()
    {
        const string source = """
            namespace Test;
            public record Input(string Document);
            public record Result(byte[] Data);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(Compile(source), out var updated, out _);
        using var stream = new MemoryStream();
        await Assert.That(updated.Emit(stream).Success).IsTrue();
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var resolver = (System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)
            Activator.CreateInstance(
                assembly.GetType("Zeeq.AgentCmd.Generated.AgentCmdJsonResolver")!,
                nonPublic: true
            )!;
        var options = new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = resolver };
        var info = options.GetTypeInfo(typeof(byte[]));
        var json = System.Text.Json.JsonSerializer.Serialize(new byte[] { 1, 2 }, info);
        await Assert.That(json).IsEqualTo("\"AQI=\"");
    }

    [Test]
    public async Task DtoMutationChangesGeneratedMetadata()
    {
        const string source = """
            namespace Test;
            public record Input(string Document);
            public record Result(string Value);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var before = Generate(source);
        var after = Generate(
            source.Replace("string Value", "string Value, int Count", StringComparison.Ordinal)
        );
        await Assert.That(before.Output.Contains("PropertyName = \"Count\"")).IsFalse();
        await Assert.That(after.Output.Contains("PropertyName = \"Count\"")).IsTrue();
        await Assert.That(after.CompileErrors.Length).IsEqualTo(0);
    }

    [Test]
    public async Task UnrelatedTypeDoesNotChangeGeneratedOutput()
    {
        const string source = """
            namespace Test;
            public record Input(string Document);
            public record Result(string Value);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, Result> { }
            """;
        var before = Generate(source);
        var after = Generate(source + "\npublic sealed class Unrelated { }");
        await Assert.That(after.Output).IsEqualTo(before.Output);
    }

    [Test]
    public async Task PublicDtoFromReferencedAssemblyCompiles()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var external = CSharpCompilation.Create(
            "ExternalDtos",
            [
                CSharpSyntaxTree.ParseText(
                    "namespace External; public sealed record Data(string Value);"
                ),
            ],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        await Assert.That(external.Emit(stream).Success).IsTrue();
        var extra = MetadataReference.CreateFromImage(stream.ToArray());
        const string source = """
            namespace Test;
            public record Input(string Document);
            public sealed class Command : Zeeq.AgentCmd.PromptCommand<Input, External.Data> { }
            """;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(
            Compile(source, extra),
            out var updated,
            out _
        );
        await Assert
            .That(
                driver.GetRunResult().Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
            )
            .IsFalse();
        await Assert
            .That(updated.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            .IsFalse();
    }

    private static CSharpCompilation Compile(string source, MetadataReference? extra = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        if (extra is not null)
        {
            references.Add(extra);
        }

        return CSharpCompilation.Create(
            "MetadataFixture" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
    }

    private static (string Output, Diagnostic[] Diagnostics, Diagnostic[] CompileErrors) Generate(
        string source
    )
    {
        var compilation = Compile(source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var run = driver.GetRunResult();
        return (
            string.Join(
                "\n",
                run.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString())
            ),
            run.Diagnostics.ToArray(),
            updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray()
        );
    }
}
