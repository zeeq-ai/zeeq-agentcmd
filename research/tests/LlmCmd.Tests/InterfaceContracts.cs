using System.Text.Json;
using Demo;
using ExecutionCapture;
using Microsoft.Extensions.AI;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace LlmCmd.Tests;
public class InterfaceContracts
{
    [Test]
    public async Task Snapshot_provider_returns_revision_and_typed_parameters()
    {
        ISnapshotProvider provider = new FakeSnapshot { Value = new Snapshot("Hi {name}", new Dictionary<string, JsonElement> { ["name"] = JsonDocument.Parse("\"Grace\"").RootElement.Clone() }, new Settings(Model: "runtime"), "revision-7") };
        var snapshot = await provider.GetAsync("stable.id", CancellationToken.None);
        await Assert.That(snapshot!.Revision).IsEqualTo("revision-7");
        await Assert.That(snapshot.Template).IsEqualTo("Hi {name}");
        await Assert.That(snapshot.Parameters!["name"].GetString()).IsEqualTo("Grace");
        await Assert.That(snapshot.Settings!.Model).IsEqualTo("runtime");
    }
    [Test]
    public async Task Cost_enricher_prices_observed_attempts_and_preserves_unknown()
    {
        ICostEnricher enricher = new FakeCost();
        var usage = UsageSnapshot.From(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20 });
        var attempt = new Attempt("request", "requested", .2f, 20, "success", "actual", usage);
        var priced = enricher.Calculate(new[] { attempt, attempt });
        await Assert.That(priced.Amount).IsEqualTo(.10m);
        await Assert.That(priced.RateVersion).IsEqualTo("fake-v1");
        var unknown = enricher.Calculate(new[] { attempt, attempt with { Outcome = "failed", Usage = null } });
        await Assert.That(unknown.Amount).IsNull();
        await Assert.That(unknown.Reason).IsEqualTo("unknown or unsupported attempt usage");
    }
    [Test]
    public async Task Executor_delivers_complete_record_to_collector()
    {
        var concrete = new FakeCollector(); ICollector collector = concrete;
        ISnapshotProvider snapshots = new FakeSnapshot { Value = new Snapshot("Changed {name}: {count}", Revision: "r1") };
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => new FakeClient() }, null, snapshots, collector, new FakeCost(), new Settings("fake"));
        var result = await executor.ExecuteAsync(new DemoCommand("Ada", 4, "stable"));
        var success = (ExecutionResult.Success)result;
        await Assert.That(success.Response.Text).IsEqualTo("answer");
        await Assert.That(concrete.Records.Count).IsEqualTo(1);
        var record = concrete.Records.Single();
        await Assert.That(record.Id).IsEqualTo("stable");
        await Assert.That(record.Revision).IsEqualTo("r1");
        await Assert.That(record.FormattedPrompt).IsEqualTo("Changed Ada: 4");
        await Assert.That(record.Attempts.Single().Request).IsEqualTo("Changed Ada: 4");
        await Assert.That(record.Attempts.Single().ResponseModel).IsEqualTo("actual-model");
        await Assert.That(record.ParameterOrigins["name"]).IsEqualTo("caller");
    }
    [Test]
    public async Task Generated_command_binds_required_typed_parameters()
    {
        var atomic = SupportReply.Bind("Ada", "Where?").WithModel("local");
        var chain = SupportReply.Start().WithCustomer("Ada").WithQuestion("Where?").Build();
        await Assert.That(SupportReply.Id).IsEqualTo("support.reply");
        await Assert.That(atomic.customer).IsEqualTo("Ada");
        await Assert.That(atomic.question).IsEqualTo("Where?");
        await Assert.That(atomic.Model).IsEqualTo("local");
        await Assert.That(chain.customer).IsEqualTo("Ada");
        await Assert.That(chain.question).IsEqualTo("Where?");
    }
    [Test]
    public async Task Generated_nullable_and_optional_arguments_preserve_defaults_and_setters()
    {
        var atomic = OptionalExample.Bind(null);
        var chain = OptionalExample.Start().WithCustomer(null).Build();
        var edited = chain.WithCount(7).WithTone("formal").WithLocale(Language.French);
        await Assert.That(atomic.customer).IsNull();
        await Assert.That(atomic.count).IsEqualTo(3);
        await Assert.That(atomic.tone).IsNull();
        await Assert.That(atomic.locale).IsEqualTo(Language.English);
        await Assert.That(chain.count).IsEqualTo(3);
        await Assert.That(edited.count).IsEqualTo(7);
        await Assert.That(edited.tone).IsEqualTo("formal");
        await Assert.That(edited.locale).IsEqualTo(Language.French);
        await Assert.That(chain.count).IsEqualTo(3);
    }

}
