using CommandRoot;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.Generated;

namespace LlmCmd.CommandRoot.Tests;

public class LibraryRegistrationTests
{
    [Test]
    public async Task Generated_leaf_and_root_share_runtime_ledger_and_actor()
    {
        var actor = new Actor("tenant", "reviewer");
        var client = new FakeClient("{\"Total\":12.5,\"Currency\":\"USD\"}");
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.UseChatClient(_ => client).AddProfile("extraction", "model-a").UseActor(_ => actor)
        );
        services.AddLlmCommands<Zeeq_LlmCmd_TestFixturesCommands>();
        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<CommandRuntime>();
        var ledger = provider.GetRequiredService<IHumanFeedbackLedger>();
        var root = provider.GetRequiredService<IRootCommands>();
        var leaf = provider.GetRequiredService<ExtractInvoice>();
        var run = ledger.CreateRun(actor);
        var result = await leaf.WithParameters(new("invoice")).InRun(run).ExecuteAsync();
        await Assert
            .That(result is Outcome<Invoice>.Success { Delivery: OutputDelivery.Stored })
            .IsTrue();
        await Assert
            .That(ReferenceEquals(runtime, provider.GetRequiredService<CommandRuntime>()))
            .IsTrue();
        await Assert.That(provider.GetRequiredService<Func<Actor>>()()).IsEqualTo(actor);
        // An unknown composite still reports authorization separately from descriptor lookup.
        var handle = await root.BeginAsync<Invoice>(new(run, "v1", "key"));
        var failure = (CompositeOutcome<Invoice>.ConfigurationFailure)await handle.ExecuteAsync();
        await Assert.That(failure.Problems.Contains("run_not_accessible")).IsFalse();
        await Assert.That(client.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Missing_actor_is_rejected_before_the_client_factory_runs()
    {
        var calls = 0;
        var services = new ServiceCollection();
        var rejected = false;
        try
        {
            services.AddLlmCmd(b =>
                b.UseChatClient(_ =>
                    {
                        calls++;
                        return new FakeClient("{}");
                    })
                    .AddProfile("extraction", "model-a")
            );
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(services.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Multiple_profiles_without_default_are_rejected_before_client_creation()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.UseChatClient(_ =>
                {
                    calls++;
                    return new FakeClient("{}");
                })
                .AddProfile("extraction", "model-a")
                .AddProfile("other", "model-b")
                .UseActor(_ => new("tenant", "actor"))
        );
        var rejected = false;
        try
        {
            services.AddLlmCommands<Zeeq_LlmCmd_TestFixturesCommands>();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_default_routes_unassigned_commands()
    {
        var client = new FakeClient("""{"display_name":null,"Children":[{"Note":null}]}""");
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.UseChatClient(_ => client)
                .AddProfile("extraction", "model-a")
                .AddProfile("other", "model-b")
                .UseDefaultProfile(ProfileKey.Declare("other"))
                .UseActor(_ => new("tenant", "actor"))
        );
        services.AddLlmCommands<Zeeq_LlmCmd_TestFixturesCommands>();
        await Assert.That(client.Calls).IsEqualTo(0);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<ContractExtract>();
        var result = (Outcome<ContractResult>.Success)
            await command.WithParameters(new("document")).ExecuteAsync();
        await Assert.That(result.Snapshot.Profile).IsEqualTo("other");
        await Assert.That(result.Snapshot.Model).IsEqualTo("model-b");
    }

    [Test]
    public async Task Authored_unknown_profile_is_rejected_before_client_creation()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.UseChatClient(_ =>
                {
                    calls++;
                    return new FakeClient("{}");
                })
                .AddProfile("other", "model-b")
                .UseDefaultProfile("other")
                .UseActor(_ => new("tenant", "actor"))
        );
        var rejected = false;
        try
        {
            services.AddLlmCommands<Zeeq_LlmCmd_TestFixturesCommands>();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Existing_runtime_instance_is_validated_when_catalog_is_registered()
    {
        var client = new FakeClient("{}");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["other"] = new("model") },
            new InMemoryFeedbackLedger(),
            () => new("tenant", "actor")
        );
        var services = new ServiceCollection();
        services.AddSingleton(runtime);
        var rejected = false;
        try
        {
            services.AddLlmCommands<Zeeq_LlmCmd_TestFixturesCommands>();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Opaque_runtime_factory_is_validated_on_resolution()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSingleton<CommandRuntime>(_ =>
        {
            calls++;
            return CommandRuntime.Create(
                () => new FakeClient("{}"),
                new Dictionary<string, Profile> { ["other"] = new("model") },
                new InMemoryFeedbackLedger(),
                () => new("tenant", "actor")
            );
        });
        services.AddLlmCommands<Zeeq_LlmCmd_TestFixturesCommands>();
        await Assert.That(calls).IsEqualTo(0);
        using var provider = services.BuildServiceProvider();
        var rejected = false;
        try
        {
            provider.GetRequiredService<CommandRuntime>();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }
}
