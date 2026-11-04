using System.Text.Json;
using System.Text.Json.Serialization;
using CommandRoot;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public sealed record SettingsPart(string Value);

public sealed record SettingsWhole(SettingsPart Header);

public sealed record SettingsParameters(string Text);

[JsonSerializable(typeof(SettingsWhole)), JsonSerializable(typeof(SettingsParameters))]
internal partial class SettingsJson : JsonSerializerContext;

sealed class SettingsCommand : PromptCommand<SettingsParameters, SettingsPart>
{
    public SettingsCommand(CommandRuntime runtime, Action<ChatSettingsPatch>? defaults = null)
        : base(
            runtime,
            new(
                "settings.command",
                1,
                "",
                "",
                "{Text}",
                "definition",
                "settings_command",
                new(() =>
                {
                    using var j = JsonDocument.Parse("{}");
                    return j.RootElement.Clone();
                }),
                SettingsJson.Default.SettingsPart,
                p => p.Text,
                _ => "",
                _ => [],
                _ => null,
                _ => null,
                ConfigureDefaults: defaults
            )
        ) { }
}

public class CompositionSettingsTests
{
    static readonly PartSlot<SettingsWhole, SettingsPart> Header = PartSlot<
        SettingsWhole,
        SettingsPart
    >.Declare("header", "Header");

    static RootCommands Roots(InMemoryFeedbackLedger ledger, Actor actor) =>
        new(
            ledger,
            () => actor,
            new InMemoryCompositionStore(),
            new CompositionRegistry([
                new CompositeDescriptor<SettingsWhole>(
                    "settings.whole",
                    1,
                    "whole-v1",
                    SettingsJson.Default.SettingsWhole,
                    [Header.Descriptor]
                ),
            ])
        );

    static async Task<CompositeOutcome<SettingsWhole>> Execute(
        RootCommands roots,
        Guid run,
        CommandRuntime runtime,
        string key = "settings"
    )
    {
        var root = await roots.BeginAsync<SettingsWhole>(new(run, "input-v1", key));
        root.WithPartFrom(Header, new SettingsCommand(runtime).WithParameters(new("document")));
        return await root.ExecuteAsync();
    }

    [Test]
    [Arguments("model")]
    [Arguments("temperature")]
    [Arguments("default_profile")]
    public async Task Remapping_a_profile_cannot_silently_replay_the_old_route(string change)
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "worker");
        var run = ledger.CreateRun(actor);
        var roots = Roots(ledger, actor);
        var firstClient = new FakeClient("""{"Value":"first"}""");
        var nextClient = new FakeClient("""{"Value":"next"}""");
        using var first = CommandRuntime.Create(
            () => firstClient,
            new Dictionary<string, Profile> { ["extract"] = new("model-a", .2f) },
            ledger,
            () => actor
        );
        using var next = CommandRuntime.Create(
            () => nextClient,
            new Dictionary<string, Profile>
            {
                [change == "default_profile" ? "renamed" : "extract"] = new(
                    change == "model" ? "model-b" : "model-a",
                    change == "temperature" ? .3f : .2f
                ),
            },
            ledger,
            () => actor
        );
        await Assert
            .That(await Execute(roots, run, first) is CompositeOutcome<SettingsWhole>.Published)
            .IsTrue();
        await Assert
            .That(await Execute(roots, run, next) is CompositeOutcome<SettingsWhole>.Conflict)
            .IsTrue();
        await Assert.That(firstClient.Calls).IsEqualTo(1);
        await Assert.That(nextClient.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Changing_an_unused_fallback_is_a_different_requested_route()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "worker");
        var run = ledger.CreateRun(actor);
        var roots = Roots(ledger, actor);
        var firstClient = new FakeClient("""{"Value":"first"}""");
        var nextClient = new FakeClient("""{"Value":"next"}""");
        var primary = ProfileKey.Declare("primary");
        var fallback = ProfileKey.Declare("fallback");
        ProfileRouter Router(FakeClient client, string fallbackModel) =>
            (
                (RouterBuildResult.Ready)
                    ProfileRouter.TryCreate(
                        [
                            new ExecutionProfile(
                                primary,
                                "provider",
                                "model-a",
                                Fallback: fallback
                            ),
                            new ExecutionProfile(fallback, "provider", fallbackModel),
                        ],
                        new Dictionary<string, ProviderRegistration>
                        {
                            ["provider"] = new(() => client),
                        }
                    )
            ).Router;
        using var firstRouter = Router(firstClient, "fallback-a");
        using var nextRouter = Router(nextClient, "fallback-b");
        using var first = CommandRuntime.Create(
            firstRouter,
            ledger,
            () => actor,
            defaultProfile: primary
        );
        using var next = CommandRuntime.Create(
            nextRouter,
            ledger,
            () => actor,
            defaultProfile: primary
        );
        await Assert
            .That(await Execute(roots, run, first) is CompositeOutcome<SettingsWhole>.Published)
            .IsTrue();
        await Assert
            .That(await Execute(roots, run, next) is CompositeOutcome<SettingsWhole>.Conflict)
            .IsTrue();
        await Assert.That(firstClient.Calls).IsEqualTo(1);
        await Assert.That(nextClient.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Defaults_run_once_and_the_value_frozen_at_registration_is_what_executes()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "worker");
        var run = ledger.CreateRun(actor);
        var roots = Roots(ledger, actor);
        var client = new FakeClient("""{"Value":"stored"}""");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extract"] = new("model-a") },
            ledger,
            () => actor
        );
        var temperature = .2f;
        var callbacks = 0;
        var call = new SettingsCommand(
            runtime,
            p =>
            {
                callbacks++;
                p.Temperature = temperature;
            }
        ).WithParameters(new("document"));
        var root = await roots.BeginAsync<SettingsWhole>(new(run, "input-v1", "freeze"));
        root.WithPartFrom(Header, call);
        temperature = .9f;
        var result = await root.ExecuteAsync();
        await Assert.That(result is CompositeOutcome<SettingsWhole>.Published).IsTrue();
        await Assert.That(client.Temperature).IsEqualTo(.2f);
        await Assert.That(callbacks).IsEqualTo(1);
    }
}
