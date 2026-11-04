using Microsoft.CodeAnalysis;

namespace Zeeq.AgentCmd.Generators.Tests;

/// <summary>Exercises only evidence the analyzer can prove inside each setup callback.</summary>
public sealed class ClientRegistrationAnalyzerTests
{
    [Test]
    public async Task ExperimentSetupDoesNotHideClientDiagnostics()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known")
                .UseExperiments(new ExperimentRuntimeOptions())
                .AddProfile("p", "missing", "model"));
            """,
            "AGENTCLIENT001:\"missing\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known")
                .UseExperiments(_ => new ExperimentRuntimeOptions())
                .AddProfile("p", "missing", "model"));
            """,
            "AGENTCLIENT001:\"missing\""
        );
    }

    [Test]
    public async Task ValidFluentAndStatementsAllowForwardDeclarationsAndBothClientOverloads()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddProfile("draft", "fast", "m")
                .AddChatClient("fast", Create, fixedModel: "m")
                .AddChatClient("backup", serviceKey: "host-key")
                .AddProfile("review", "backup", "other"));
            """
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => {
                b.AddProfile(model: "m", client: "fast", name: "draft");
                b.AddChatClient(fixedModel: "m", name: "fast", factory: Create);
            });
            """
        );
    }

    [Test]
    public async Task ReportsClientMisspellingCaseSensitiveAndArgumentLocations()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast")
                .AddProfile("draft", "Fast", "m"));
            """,
            "AGENTCLIENT001:\"Fast\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => {
                b.AddProfile(model: "m", name: "draft", client: "missing");
                b.AddChatClient("present");
            });
            """,
            "AGENTCLIENT001:\"missing\""
        );
    }

    [Test]
    public async Task UsesCompilerConstantsAndReportsTheirExpressionLocation()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            const string prefix = "fa";
            services.AddAgentCmd(b => b.AddProfile("p", prefix + "st", "m")
                .AddChatClient(nameof(Example))
                .AddChatClient(prefix + "st"));
            """
        );
        await AnalyzerHarness.ExpectAsync(
            """
            const string missing = "not-there";
            services.AddAgentCmd(b => b.AddChatClient(nameof(Example))
                .AddProfile("p", missing, "m"));
            """,
            "AGENTCLIENT001:missing"
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("one")
                .AddProfile("p", nameof(Example), "m"));
            """,
            "AGENTCLIENT001:nameof(Example)"
        );
    }

    [Test]
    public async Task ReportsDuplicateClientNamesAndNotDifferentlyCasedNames()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast", Create)
                .AddChatClient("fast", serviceKey: "other"));
            """,
            "AGENTCLIENT002:\"fast\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast").AddChatClient("Fast"));
            """
        );
    }

    [Test]
    public async Task DuplicateDiagnosticPointsToSecondRegistration()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast").AddChatClient("fast"));
            """
        );
        await Assert.That(diagnostics.Length).IsEqualTo(1);
        var diagnostic = diagnostics[0];
        var source = diagnostic.Location.SourceTree!.GetText().ToString();
        await Assert.That(diagnostic.Id).IsEqualTo("AGENTCLIENT002");
        await Assert
            .That(diagnostic.Location.SourceSpan.Start)
            .IsEqualTo(source.LastIndexOf("\"fast\"", StringComparison.Ordinal));
    }

    [Test]
    public async Task ReportsOnlyProvenFixedModelConflicts()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddProfile("p", "fast", "other")
                .AddChatClient("fast", fixedModel: "bound"));
            """,
            "AGENTCLIENT003:\"other\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast", fixedModel: dynamicModel)
                .AddProfile("p", "fast", "other"));
            """
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast", fixedModel: "bound")
                .AddProfile("p", "fast", dynamicModel));
            """
        );
    }

    [Test]
    public async Task SeparateSetupsDoNotShareRegistrations()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(a => a.AddChatClient("first"));
            services.AddAgentCmd(b => b.AddProfile("p", "first", "m").AddChatClient("second"));
            """,
            "AGENTCLIENT001:\"first\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(a => a.AddChatClient("same"));
            services.AddAgentCmd(b => b.AddChatClient("same"));
            """
        );
    }

    [Test]
    public async Task LegacyProfileOverloadsAreNotClientRoutes()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast").AddProfile("p", "model"));
            """
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("fast").AddProfile(profileKey, "model", 0.1f));
            """
        );
    }

    [Test]
    public async Task UnknownControlFlowAndBuilderEscapeSuppressSpeculativeErrors()
    {
        string[] unknown =
        [
            "services.AddAgentCmd(b => { b.AddProfile(\"p\", \"missing\", \"m\"); b.AddChatClient(dynamicName); });",
            "services.AddAgentCmd(b => { b.AddProfile(\"p\", \"missing\", \"m\"); if (flag) b.AddChatClient(\"missing\"); });",
            "services.AddAgentCmd(b => { b.AddProfile(\"p\", \"missing\", \"m\"); ConfigureMore(b); });",
            "services.AddAgentCmd(b => { b.AddProfile(\"p\", \"missing\", \"m\"); for (int i = 0; i < 1; i++) b.AddChatClient(\"missing\"); });",
            "services.AddAgentCmd(b => { var alias = b; b.AddProfile(\"p\", \"missing\", \"m\"); alias.AddChatClient(\"missing\"); });",
            "services.AddAgentCmd(b => { if (flag) return; b.AddProfile(\"p\", \"missing\", \"m\"); });",
            "services.AddAgentCmd(b => { if (flag) throw new Exception(); b.AddProfile(\"p\", \"missing\", \"m\"); });",
            "services.AddAgentCmd(b => { b.AddChatClient(\"other\", _ => { GC.KeepAlive(b); return null!; }); b.AddProfile(\"p\", \"missing\", \"m\"); });",
            "services.AddAgentCmd(b => b.AddChatClient(\"other\", serviceKey: Take(b)).AddProfile(\"p\", \"missing\", \"m\"));",
        ];
        foreach (var body in unknown)
        {
            await AnalyzerHarness.ExpectAsync(body);
        }
    }

    [Test]
    public async Task BuilderNameofIsAConstantAndDoesNotEscape()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient(nameof(b)).AddProfile("p", nameof(b), "m"));
            """
        );
    }

    [Test]
    public async Task EmptyClientBeforeRouteDoesNotInventReachableError()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("").AddProfile("p", "missing", "m"));
            """
        );
    }

    [Test]
    public async Task DuplicateStopsBeforeLaterMissingRoute()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("a").AddChatClient("a").AddProfile("p", "missing", "m"));
            """,
            "AGENTCLIENT002:\"a\""
        );
    }

    [Test]
    public async Task ThrowingKeyDoesNotHideDeclaredTopology()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("a", serviceKey: ThrowingKey()).AddProfile("p", "missing", "m"));
            """,
            "AGENTCLIENT001:\"missing\""
        );
    }

    [Test]
    public async Task DynamicModelDoesNotHideKnownRegistrationErrors()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known", fixedModel: dynamicModel)
                .AddProfile("p", "missing", dynamicModel));
            """,
            "AGENTCLIENT001:\"missing\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("same", fixedModel: dynamicModel)
                .AddChatClient("same"));
            """,
            "AGENTCLIENT002:\"same\""
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known", fixedModel: dynamicModel)
                .AddProfile("p", "known", "model"));
            """
        );
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known", fixedModel: "bound")
                .AddProfile("p", "known", dynamicModel));
            """
        );
    }

    [Test]
    public async Task DynamicRouteDoesNotHideAnUnrelatedKnownMissingClient()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known")
                .AddProfile("dynamic", dynamicName, "model")
                .AddProfile("static", "missing", "model"));
            """,
            "AGENTCLIENT001:\"missing\""
        );
    }

    [Test]
    public async Task UnrelatedMethodsWithMatchingNamesAreIgnored()
    {
        await AnalyzerHarness.ExpectAsync(
            """
            Fake.AddChatClient("a");
            Fake.AddProfile("p", "missing", "m");
            Fake.AddAgentCmd(b => b.AddProfile("p", "missing", "m"));
            services.AddAgentCmd(b => b.AddChatClient("a").AddProfile("p", "a", "m"));
            """
        );
    }

    [Test]
    public async Task RealLibraryMetadataProducesTheSameDiagnostic()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known").AddProfile("p", "missing", "m"));
            """,
            useRealApi: true
        );
        await Assert
            .That(diagnostics.Select(AnalyzerHarness.Describe).ToArray())
            .IsEquivalentTo(new[] { "AGENTCLIENT001:\"missing\"" });
    }

    [Test]
    public async Task RealMetadataHonorsKeyedFactoryTypedAndLegacyOverloads()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known")
                .AddProfile("legacy", "model")
                .AddProfile(profileKey, "missing", "model"));
            services.AddAgentCmd(b => b.AddChatClient("bound", _ => (Microsoft.Extensions.AI.IChatClient)null!, fixedModel: "x")
                .AddProfile(profileKey, "bound", "y"));
            """,
            useRealApi: true
        );
        await Assert
            .That(diagnostics.Select(AnalyzerHarness.Describe).OrderBy(value => value).ToArray())
            .IsEquivalentTo(new[] { "AGENTCLIENT001:\"missing\"", "AGENTCLIENT003:\"y\"" });
    }

    [Test]
    public async Task ProfileKeyHelpersDoNotHideTopology()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known")
                .AddProfile(ProfileKey.Declare("p"), "missing", "model", fallback: ProfileKey.Declare("fallback")));
            services.AddAgentCmd(b => b.AddChatClient("bound", fixedModel: "x")
                .AddProfile(ProfileKey.Declare("p"), "bound", "y"));
            """,
            useRealApi: true
        );
        // The analyzer checks topology if argument evaluation completes; it does not prove helper purity.
        await Assert
            .That(diagnostics.Select(AnalyzerHarness.Describe).ToArray())
            .IsEquivalentTo(new[] { "AGENTCLIENT001:\"missing\"", "AGENTCLIENT003:\"y\"" });
    }

    [Test]
    public async Task UnknownSameNameOverloadSuppressesTheSetup()
    {
        var diagnostics = await AnalyzerHarness.AnalyzeAsync(
            """
            services.AddAgentCmd(b => b.AddChatClient("known", 42).AddProfile("p", "missing", "model"));
            """,
            stubOverride: AnalyzerHarness.StubsWithUnknownOverload()
        );
        await Assert.That(diagnostics.Length).IsEqualTo(0);
    }

    [Test]
    public async Task HarnessRejectsMalformedApiStubs()
    {
        try
        {
            await AnalyzerHarness.AnalyzeAsync(
                "services.AddAgentCmd(b => b.AddChatClient(\"a\"));",
                stubOverride: "namespace Zeeq.AgentCmd { public class Broken {"
            );
            throw new InvalidOperationException("Malformed API stubs were accepted.");
        }
        catch (InvalidOperationException error)
            when (error.Message.StartsWith("Fixture compilation failed:", StringComparison.Ordinal))
        {
            await Assert.That(error.Message.Contains("CS", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task HarnessRejectsInvalidFixtures()
    {
        try
        {
            await AnalyzerHarness.AnalyzeAsync("services.NoSuchMethod();");
            throw new InvalidOperationException("Invalid test source was accepted.");
        }
        catch (InvalidOperationException error)
            when (error.Message.StartsWith("Fixture compilation failed:", StringComparison.Ordinal))
        {
            await Assert.That(error.Message.Contains("CS1061", StringComparison.Ordinal)).IsTrue();
        }
    }
}
