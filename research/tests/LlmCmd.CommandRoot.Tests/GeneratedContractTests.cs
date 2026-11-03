using System.Text.Json;
using CommandRoot;
using HumanGrading;
using LlmCmd.Generated;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace LlmCmd.CommandRoot.Tests;

public class GeneratedContractTests
{
    const string Valid = """{"display_name":null,"Children":[{"Note":null}]}""";
    static (FakeClient Client, CommandRuntime Runtime) Setup(string response = Valid, float? profileTemperature = .4f)
    {
        var client = new FakeClient(response);
        var actor = new Actor("contract", "reviewer");
        var runtime = CommandRuntime.Create(() => client,
            new Dictionary<string, Profile> { ["test"] = new("model", profileTemperature) },
            new HumanLedger(), () => actor);
        return (client, runtime);
    }

    [Test]
    public async Task Ordinary_records_have_matching_schema_projection_and_typed_results()
    {
        var f = Setup(); using var runtime = f.Runtime;
        var result = (Outcome<ContractResult>.Success)await new ContractExtract(runtime)
            .WithParameters(new("invoice", null, "private")).ExecuteAsync();
        await Assert.That(result.Value.Label).IsNull();
        await Assert.That(result.Value.Children.Length).IsEqualTo(1);
        await Assert.That(result.Value.Children[0].Note).IsNull();
        await Assert.That(result.Value.Count).IsEqualTo(7);
        await Assert.That(result.Snapshot.PromptId).IsEqualTo("contract.extract");
        await Assert.That(result.Snapshot.Revision).IsEqualTo(1);
        await Assert.That(result.Snapshot.FormattedPrompt).IsEqualTo("Read invoice ()");
        var doc = result.Snapshot.Parameters.Single(p => p.Name == "Document");
        await Assert.That(doc.JsonName).IsEqualTo("document_text");
        await Assert.That(doc.Value.GetString()).IsEqualTo("invoice");
        await Assert.That(result.Snapshot.Parameters.Single(p => p.Name == "Locale").Value.ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(result.Snapshot.Parameters.Any(p => p.Name == "InternalNote")).IsFalse();
        var schema = f.Client.Format!.Schema!.Value;
        var properties = schema.GetProperty("properties");
        await Assert.That(properties.TryGetProperty("display_name", out var label)).IsTrue();
        await Assert.That(label.GetRawText().Contains("null")).IsTrue();
        await Assert.That(properties.TryGetProperty("Label", out _)).IsFalse();
        await Assert.That(properties.TryGetProperty("InternalValue", out _)).IsFalse();
        var required = schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        await Assert.That(required.Contains("display_name")).IsTrue();
        await Assert.That(required.Contains("Children")).IsTrue();
        await Assert.That(required.Contains("Count")).IsFalse();
    }

    [Test]
    [Arguments("""{"Children":[{"Note":null}]}""")]
    [Arguments("""{"display_name":null,"Children":[{}]}""")]
    [Arguments("""{"display_name":null,"Children":null}""")]
    public async Task Missing_required_or_null_nonnullable_result_is_rejected(string response)
    {
        var f = Setup(response); using var runtime = f.Runtime;
        var result = await new ContractExtract(runtime).WithParameters(new("invoice")).ExecuteAsync();
        await Assert.That(result is Outcome<ContractResult>.Failed { Failure: ExecutionFailure.InvalidOutput }).IsTrue();
        await Assert.That(f.Client.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Optional_constructor_defaults_are_used_and_projected()
    {
        var f = Setup("""{"display_name":"a","Children":[],"Count":0}"""); using var runtime = f.Runtime;
        var result = (Outcome<ContractResult>.Success)await new ContractExtract(runtime).WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(result.Value.Count).IsEqualTo(0);
        await Assert.That(result.Snapshot.Parameters.Single(p => p.Name == "Locale").Value.GetString()).IsEqualTo("en");
    }

    [Test]
    public async Task Generated_catalog_activates_commands_with_host_runtime()
    {
        var f = Setup(); using var runtime = f.Runtime;
        var services = new ServiceCollection(); services.AddSingleton(runtime); services.AddLlmCommands<CommandRootCommands>();
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<ContractExtract>();
        var result = (Outcome<ContractResult>.Success)await command.WithParameters(new("registered")).ExecuteAsync();
        await Assert.That(result.Snapshot.PromptId).IsEqualTo("contract.extract");
        await Assert.That(CommandRootCommands.Commands.Any(x => x.Id == "contract.extract" && x.CommandType == typeof(ContractExtract))).IsTrue();
    }

    [Test]
    public async Task Defaults_hook_yields_to_profile_and_explicit_call_settings()
    {
        var f = Setup(profileTemperature: null); using var runtime = f.Runtime;
        var command = new ContractDefaultsExtract(runtime).WithParameters(new("doc"));
        var defaults = (Outcome<ContractResult>.Success)await command.ExecuteAsync();
        var explicitCall = (Outcome<ContractResult>.Success)await command.WithTemperature(.1f).ExecuteAsync();
        await Assert.That(defaults.Snapshot.Temperature).IsEqualTo(.2f);
        await Assert.That(explicitCall.Snapshot.Temperature).IsEqualTo(.1f);
        var g = Setup(profileTemperature: .4f); using var otherRuntime = g.Runtime;
        var profile = (Outcome<ContractResult>.Success)await new ContractDefaultsExtract(otherRuntime).WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(profile.Snapshot.Temperature).IsEqualTo(.4f);
    }

    [Test]
    public async Task Throwing_defaults_hook_is_a_per_execution_failure_before_provider_call()
    {
        var f = Setup(); using var runtime = f.Runtime;
        var result = (ExecutionFailure.HookFailure)((Outcome<ContractResult>.Failed)await new ContractThrowDefaultsExtract(runtime).WithParameters(new("doc")).ExecuteAsync()).Failure;
        await Assert.That(result.Phase).IsEqualTo("ConfigureDefaults");
        await Assert.That(result.ExecutionId).IsNotEqualTo(Guid.Empty);
        await Assert.That(f.Client.Calls).IsEqualTo(0);
    }
}
