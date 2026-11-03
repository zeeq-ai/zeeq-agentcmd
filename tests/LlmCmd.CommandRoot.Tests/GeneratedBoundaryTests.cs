using CommandRoot;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.CommandRoot.Tests;

public class GeneratedBoundaryTests
{
    [Test]
    public async Task Nonnullable_settable_reference_is_required_in_schema_and_deserialization()
    {
        var client = new FakeClient("{\"Name\":\"ok\"}");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["only"] = new("fake") },
            new InMemoryFeedbackLedger(),
            () => new Actor("t", "s")
        );
        var command = new ExtractSetter(runtime).WithParameters(new("doc"));
        var good = (Outcome<SetterResult>.Success)await command.ExecuteAsync();
        await Assert.That(good.Value.Name).IsEqualTo("ok");
        await Assert.That(good.Value.Note).IsNull();
        var required = client
            .Format!.Schema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
        await Assert.That(required.Contains("Name")).IsTrue();
        await Assert.That(required.Contains("Note")).IsFalse();
        client.Response = "{}";
        await Assert
            .That(
                await command.ExecuteAsync()
                    is Outcome<SetterResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
        client.Response = "{\"Name\":null}";
        await Assert
            .That(
                await command.ExecuteAsync()
                    is Outcome<SetterResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
    }

    [Test]
    public async Task Explicit_serializer_policy_can_reject_unknown_wire_fields()
    {
        using var bad = Runtime("{\"Name\":\"ok\",\"Extra\":42}");
        await Assert
            .That(
                await new ExtractStrict(bad).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<StrictResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
        using var good = Runtime("{\"Name\":\"ok\"}");
        await Assert
            .That(
                await new ExtractStrict(good).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<StrictResult>.Success
            )
            .IsTrue();
    }

    [Test]
    public async Task Contract_configuration_failure_precedes_render_projection_and_provider_work()
    {
        var client = new FakeClient("{}");
        var records = new List<ExecutionOutcomeRecord>();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["only"] = new() },
            new InMemoryFeedbackLedger(),
            () => new Actor("t", "s"),
            sink: records.Add
        );
        var result = (ExecutionFailure.ConfigurationFailure)
            (
                (Outcome<Invoice>.Failed)
                    await new BrokenContractProbe(runtime).WithParameters(new("doc")).ExecuteAsync()
            ).Failure;
        await Assert.That(result.Reason).IsEqualTo("JSON contract invalid");
        await Assert.That(result.Snapshot).IsNotNull();
        await Assert.That(client.Calls).IsEqualTo(0);
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Outcome).IsEqualTo("configuration_failure");
    }

    [Test]
    public async Task Known_polymorphic_members_are_validated_in_the_derived_contract()
    {
        using var bad = Runtime("""{"Part":{"$type":"derived","Children":[null]}}""");
        await Assert
            .That(
                await new ExtractPolymorphic(bad).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<PolymorphicResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
        using var good = Runtime("""{"Part":{"$type":"derived","Children":[{"Text":"ok"}]}}""");
        var result = (Outcome<PolymorphicResult>.Success)
            await new ExtractPolymorphic(good).WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(((DerivedGraph)result.Value.Part).Children[0].Text).IsEqualTo("ok");
    }

    [Test]
    public async Task Closed_generic_reference_nullability_is_included_in_the_JSON_contract()
    {
        using var bad = Runtime("""{"Box":{"Value":null}}""");
        await Assert
            .That(
                await new ExtractBox(bad).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<BoxResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
        using var good = Runtime("""{"Box":{"Value":"ok"}}""");
        var result = (Outcome<BoxResult>.Success)
            await new ExtractBox(good).WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(result.Value.Box.Value).IsEqualTo("ok");
    }

    static CommandRuntime Runtime(string json) =>
        CommandRuntime.Create(
            () => new FakeClient(json),
            new Dictionary<string, Profile> { ["only"] = new("fake") },
            new InMemoryFeedbackLedger(),
            () => new Actor("test", "worker")
        );

    [Test]
    public async Task Nonnullable_array_elements_are_validated_after_deserialization()
    {
        using var runtime = Runtime("""{"display_name":null,"Children":[null]}""");
        var result = await new ContractExtract(runtime).WithParameters(new("doc")).ExecuteAsync();
        await Assert
            .That(
                result is Outcome<ContractResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
    }

    [Test]
    public async Task Nonnullable_dictionary_values_are_validated_and_nullable_values_are_allowed()
    {
        using var bad = Runtime("""{"Values":{"x":null},"NullableValues":{}}""");
        await Assert
            .That(
                await new ExtractGraph(bad).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<GraphResult>.Failed { Failure: ExecutionFailure.InvalidOutput }
            )
            .IsTrue();
        using var good = Runtime("""{"Values":{"x":{"Text":"ok"}},"NullableValues":{"x":null}}""");
        var result = (Outcome<GraphResult>.Success)
            await new ExtractGraph(good).WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(result.Value.Values["x"].Text).IsEqualTo("ok");
        await Assert.That(result.Value.NullableValues["x"]).IsNull();
    }

    [Test]
    public async Task CSharp_required_members_need_no_JsonRequired_annotation()
    {
        using var bad = Runtime("{}");
        await Assert
            .That(
                await new ExtractRequiredMember(bad).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<RequiredMemberResult>.Failed
                    {
                        Failure: ExecutionFailure.InvalidOutput
                    }
            )
            .IsTrue();
        using var good = Runtime("""{"Name":"ok"}""");
        var result = (Outcome<RequiredMemberResult>.Success)
            await new ExtractRequiredMember(good).WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(result.Value.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Missing_host_default_with_multiple_profiles_is_a_closed_no_attempt_failure()
    {
        var client = new FakeClient("{}");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["a"] = new(), ["b"] = new() },
            new InMemoryFeedbackLedger(),
            () => new Actor("t", "s")
        );
        await Assert
            .That(
                await new ContractExtract(runtime).WithParameters(new("doc")).ExecuteAsync()
                    is Outcome<ContractResult>.Failed { Failure: ExecutionFailure.InvalidInput }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_host_default_and_call_override_select_settings_without_command_metadata()
    {
        var client = new FakeClient("""{"display_name":null,"Children":[]}""");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["a"] = new("model-a"), ["b"] = new("model-b") },
            new InMemoryFeedbackLedger(),
            () => new Actor("t", "s"),
            defaultProfile: ProfileKey.Declare("b")
        );
        var root = new ContractExtract(runtime).WithParameters(new("doc"));
        var host = (Outcome<ContractResult>.Success)await root.ExecuteAsync();
        var call = (Outcome<ContractResult>.Success)
            await root.WithProfile(ProfileKey.Declare("a")).ExecuteAsync();
        await Assert.That(host.Snapshot.Model).IsEqualTo("model-b");
        await Assert.That(call.Snapshot.Model).IsEqualTo("model-a");
    }

    [Test]
    public async Task Input_nonnullable_contract_failure_has_no_provider_attempt()
    {
        var client = new FakeClient("{}");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["only"] = new() },
            new InMemoryFeedbackLedger(),
            () => new Actor("t", "s")
        );
        await Assert
            .That(
                await new ContractExtract(runtime).WithParameters(new(null!)).ExecuteAsync()
                    is Outcome<ContractResult>.Failed { Failure: ExecutionFailure.InvalidInput }
            )
            .IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);
    }

    sealed class FailedClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken ct = default
        ) =>
            Task.FromException<ChatResponse>(
                new HttpRequestException("fake", null, System.Net.HttpStatusCode.ServiceUnavailable)
            );

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public object? GetService(Type type, object? key = null) => null;

        public void Dispose() { }
    }

    [Test]
    public async Task Fallback_profile_temperature_wins_over_hook_default_but_call_patch_wins_over_both()
    {
        var a = ProfileKey.Declare("a");
        var b = ProfileKey.Declare("b");
        using var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    new[]
                    {
                        new ExecutionProfile(a, "a", "first", Fallback: b),
                        new ExecutionProfile(b, "b", "second", .8f),
                    },
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["a"] = new(() => new FailedClient()),
                        ["b"] = new(() =>
                            new FakeClient("""{"display_name":null,"Children":[]}""")
                        ),
                    }
                )
        ).Router;
        using var runtime = CommandRuntime.Create(
            router,
            new InMemoryFeedbackLedger(),
            () => new Actor("t", "s"),
            defaultProfile: a
        );
        var root = new ContractDefaultsExtract(runtime).WithParameters(new("doc"));
        var defaults = (Outcome<ContractResult>.Success)await root.ExecuteAsync();
        var call = (Outcome<ContractResult>.Success)
            await root.Configure(p => p.Temperature = .1f).ExecuteAsync();
        await Assert.That(defaults.Snapshot.Routing!.Attempts[0].Temperature).IsEqualTo(.2f);
        await Assert.That(defaults.Snapshot.Routing.Attempts[1].Temperature).IsEqualTo(.8f);
        await Assert.That(call.Snapshot.Routing!.Attempts.All(x => x.Temperature == .1f)).IsTrue();
    }
}

sealed class BrokenContractProbe : PromptCommand<InvoiceParameters, Invoice>
{
    static readonly CommandJsonContract<InvoiceParameters, Invoice> Contract = new(
        () => TrustedProbeJson.Default,
        _ => throw new InvalidOperationException("private contract details")
    );

    public BrokenContractProbe(CommandRuntime runtime)
        : base(
            runtime,
            new(
                "contract.invalid",
                1,
                "",
                "",
                "",
                "hash",
                "contract_invalid",
                new(() => Contract.Schema),
                null,
                _ => throw new Exception("render must not run"),
                _ => throw new Exception("render must not run"),
                _ => throw new Exception("project must not run"),
                _ => null,
                _ => null,
                Contract
            )
        ) { }
}
