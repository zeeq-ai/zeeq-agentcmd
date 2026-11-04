using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Zeeq.AgentCmd.Generators;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Compiles complete snippets so diagnostics cannot be explained by invalid test code.</summary>
internal static class AnalyzerHarness
{
    private const string Stubs = """
        #nullable enable
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }
        }
        namespace Microsoft.Extensions.AI
        {
            public interface IChatClient { }
        }
        namespace Zeeq.AgentCmd
        {
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.AI;
            public sealed class ProfileKey { }
            public sealed class ProfileDefaults { }
            public sealed class RetryPolicy { }
            public sealed class ExperimentRuntimeOptions { }
            public enum FailureKind { Permanent }
            public sealed class AgentCmdBuilder
            {
                public AgentCmdBuilder AddChatClient(string name, System.Func<System.IServiceProvider, IChatClient> factory, string? fixedModel = null, System.Func<System.Exception, FailureKind>? classify = null) => this;
                public AgentCmdBuilder AddChatClient(string name, object? serviceKey = null, string? fixedModel = null, System.Func<System.Exception, FailureKind>? classify = null) => this;
                public AgentCmdBuilder AddProfile(string name, string model, float? temperature = null) => this;
                public AgentCmdBuilder AddProfile(ProfileKey key, string model, float? temperature = null) => this;
                public AgentCmdBuilder AddProfile(string name, string client, string model, ProfileDefaults? defaults = null, RetryPolicy? retry = null, ProfileKey? fallback = null, System.TimeSpan? attemptTimeout = null) => this;
                public AgentCmdBuilder AddProfile(ProfileKey key, string client, string model, ProfileDefaults? defaults = null, RetryPolicy? retry = null, ProfileKey? fallback = null, System.TimeSpan? attemptTimeout = null) => this;
                public AgentCmdBuilder UseExperiments(ExperimentRuntimeOptions options) => this;
                public AgentCmdBuilder UseExperiments(System.Func<System.IServiceProvider, ExperimentRuntimeOptions> factory) => this;
            }
            public static class AgentCmdServiceCollectionExtensions
            {
                public static IServiceCollection AddAgentCmd(this IServiceCollection services, System.Action<AgentCmdBuilder> configure) => services;
            }
        }
        """;

    private const string Preamble = """
        #nullable enable
        using System;
        using Zeeq.AgentCmd;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.AI;
        public static class Fake
        {
            public static void AddChatClient(string name) { }
            public static void AddAgentCmd(System.Action<AgentCmdBuilder> configure) { }
            public static void AddProfile(string name, string client, string model) { }
        }
        public static class Example
        {
            private static IChatClient Create(IServiceProvider serviceProvider) => null!;
            private static object Take(AgentCmdBuilder b) => b;
            private static object ThrowingKey() => throw new Exception();
            private static void ConfigureMore(AgentCmdBuilder b) { }
            public static void Configure(IServiceCollection services, bool flag, string dynamicName, string dynamicModel, ProfileKey profileKey)
            {
        """;

    private const string Epilogue = """
            }
        }
        """;

    internal static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string body,
        bool useRealApi = false,
        string? stubOverride = null
    )
    {
        var source = Preamble + "\n" + body + "\n" + Epilogue;
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, path: "Host.cs") };
        if (!useRealApi)
        {
            trees.Add(CSharpSyntaxTree.ParseText(stubOverride ?? Stubs, path: "Stubs.cs"));
        }

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
        if (useRealApi)
        {
            // Explicit metadata makes this case exercise the shipped API, not the stub.
            references.Add(
                MetadataReference.CreateFromFile(typeof(AgentCmdBuilder).Assembly.Location)
            );
            references.Add(
                MetadataReference.CreateFromFile(
                    typeof(Microsoft.Extensions.AI.IChatClient).Assembly.Location
                )
            );
            references.Add(
                MetadataReference.CreateFromFile(
                    typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection)
                        .Assembly
                        .Location
                )
            );
        }

        var compilation = CSharpCompilation.Create(
            "AnalyzerFixture",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var errors = compilation
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidOperationException(
                "Fixture compilation failed:\n"
                    + string.Join("\n", errors.Select(d => d.ToString()))
            );
        }

        return await compilation
            .WithAnalyzers(
                ImmutableArray.Create<DiagnosticAnalyzer>(new ClientRegistrationAnalyzer())
            )
            .GetAnalyzerDiagnosticsAsync();
    }

    internal static string StubsWithUnknownOverload() =>
        Stubs.Replace(
            "public sealed class AgentCmdBuilder\n    {",
            "public sealed class AgentCmdBuilder\n    {\n        public AgentCmdBuilder AddChatClient(string name, int revision) => this;",
            StringComparison.Ordinal
        );

    internal static string Describe(Diagnostic diagnostic)
    {
        var location = diagnostic.Location;
        if (!location.IsInSource || location.SourceTree?.FilePath != "Host.cs")
        {
            return diagnostic.Id + ":<unexpected location>";
        }

        return diagnostic.Id + ":" + location.SourceTree.GetText().ToString(location.SourceSpan);
    }

    internal static async Task ExpectAsync(string body, params string[] expected)
    {
        var diagnostics = await AnalyzeAsync(body);
        var actual = diagnostics
            .Select(Describe)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var orderedExpected = expected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(orderedExpected, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unexpected diagnostics for:\n{body}\nExpected: {string.Join(", ", orderedExpected)}\nActual: {string.Join(", ", actual)}\nFull: {string.Join("; ", diagnostics)}"
            );
        }
    }
}
