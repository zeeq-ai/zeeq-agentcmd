using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using ExecutionCapture;
using Microsoft.Extensions.AI;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace LlmCmd.Tests;
public class RuntimeContracts
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    [Test]
    public async Task native_closed_result_matching()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        ExecutionResult[] results = { basic, new ExecutionResult.Invalid("bad", basic.Capture, null), new ExecutionResult.Failed("error", basic.Capture, null), new ExecutionResult.Cancelled(basic.Capture, null) }; var matched = results.Select(r => r.Match(_ => "success", _ => "invalid", _ => "failed", _ => "cancelled")); await Assert.That(string.Join(",", matched) == "success,invalid,failed,cancelled").IsTrue(); 
    }
    [Test]
    public async Task basic_capture_and_formatting()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        await Assert.That(basic.Capture.FormattedPrompt == "Hello Ada, Ada: 2. {literal}").IsTrue(); await Assert.That(basic.Capture.Attempts.Count == 1 && basic.Capture.Attempts[0].Request == basic.Capture.FormattedPrompt).IsTrue(); await Assert.That(basic.Capture.ResponseInputTokens == 10 && basic.Capture.Cost.Amount == .03m).IsTrue(); await Assert.That(basic.Capture.Settings.Model == "command-model" && basic.Capture.Settings.Provider == "fake" && factories == 1).IsTrue(); 
    }
    [Test]
    public async Task default_id_hash_and_caller()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        await Assert.That(basic.Capture.Id == typeof(DemoCommand).FullName && basic.Capture.DefinitionHash == basic.Capture.EffectiveHash).IsTrue(); await Assert.That(basic.Capture.Caller.Line > 0 && basic.Capture.Caller.File.EndsWith("RuntimeContracts.cs")).IsTrue(); 
    }
    [Test]
    public async Task explicit_id_is_retained()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("A", 1, "stable.id")); await Assert.That(r.Capture.Id == "stable.id" && snapshot.LastId == "stable.id").IsTrue();
    }
    [Test]
    public async Task runtime_override_and_precedence()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        snapshot.Value = new("Changed {name} {count}", new Dictionary<string, JsonElement> { ["name"] = Json("\"Grace\"") }, new(Model: "runtime", Temperature: .5f), "rev-2"); var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 5, AllowNameOverride: true), new(Model: "explicit")); await Assert.That(r.Capture.FormattedPrompt == "Changed Grace 5" && r.Capture.Revision == "rev-2").IsTrue(); await Assert.That(r.Capture.DefinitionHash != r.Capture.EffectiveHash && r.Capture.Settings.Model == "explicit" && r.Capture.Settings.Temperature == .5f && r.Capture.Settings.MaxTokens == 128).IsTrue(); snapshot.Value = null;
    }
    [Test]
    public async Task unknown_placeholder_rejected_before_provider()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var calls = fake.Calls; snapshot.Value = new("{secret}"); await Assert.That(await executor.ExecuteAsync(new("A", 1)) is ExecutionResult.Invalid && fake.Calls == calls).IsTrue(); snapshot.Value = null;
    }
    [Test]
    public async Task wrong_json_type_rejected_before_provider()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var calls = fake.Calls; snapshot.Value = new(Parameters: new Dictionary<string, JsonElement> { ["count"] = Json("\"3\"") }); await Assert.That(await executor.ExecuteAsync(new("A", 1, AllowCountOverride: true)) is ExecutionResult.Invalid && fake.Calls == calls).IsTrue(); snapshot.Value = null;
    }
    [Test]
    public async Task unknown_json_parameter_rejected()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var calls = fake.Calls; snapshot.Value = new(Parameters: new Dictionary<string, JsonElement> { ["extra"] = Json("1") }); await Assert.That(await executor.ExecuteAsync(new("A", 1)) is ExecutionResult.Invalid && fake.Calls == calls).IsTrue(); snapshot.Value = null;
    }
    [Test]
    public async Task invalid_is_captured_once_without_model_call()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var n = collector.Records.Count; var calls = fake.Calls; snapshot.Value = new("{unknown}"); var r = (ExecutionResult.Invalid)await executor.ExecuteAsync(new("A", 1)); snapshot.Value = null; await Assert.That(collector.Records.Count == n + 1 && fake.Calls == calls && r.Capture.Attempts.Count == 0 && r.Capture.Outcome == "invalid" && r.Capture.Error!.Contains("unknown")).IsTrue();
    }
    [Test]
    public async Task invalid_name_and_settings()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var calls = fake.Calls; await Assert.That(await executor.ExecuteAsync(new(null!, 1)) is ExecutionResult.Invalid).IsTrue(); await Assert.That(await executor.ExecuteAsync(new("A", 1), new(MaxTokens: -1)) is ExecutionResult.Invalid).IsTrue(); await Assert.That(await executor.ExecuteAsync(new("A", 1), new(Temperature: float.NaN)) is ExecutionResult.Invalid).IsTrue(); await Assert.That(await executor.ExecuteAsync(new("A", 1), new(Provider: "missing")) is ExecutionResult.Invalid).IsTrue(); await Assert.That(fake.Calls == calls).IsTrue();
    }
    [Test]
    public async Task capture_preserves_originals_and_immutable_views()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        snapshot.Value = new("New {name} {count}", new Dictionary<string, JsonElement> { ["name"] = Json("\"changed\"") }, Revision: "2"); var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("original", 4, AllowNameOverride: true)); snapshot.Value = null; await Assert.That((string?)r.Capture.OriginalParameters["name"] == "original" && (string?)r.Capture.Parameters["name"] == "changed" && r.Capture.OriginalTemplate == DemoCommand.Template && r.Capture.Template == "New {name} {count}" && r.Capture.ParameterOrigins["name"] == "runtime" && r.Capture.ParameterOrigins["count"] == "caller").IsTrue(); await Assert.That(r.Capture.SchemaVersion == 1 && r.Capture.ExecutionId != Guid.Empty && r.Capture.StartedAt <= DateTimeOffset.UtcNow && r.Capture.DurationMs >= 0).IsTrue(); try { ((IDictionary<string, object?>)r.Capture.Parameters)["name"] = "mutated"; throw new Exception("dictionary writable"); } catch (NotSupportedException) {} try { ((IList<Attempt>)r.Capture.Attempts).Clear(); throw new Exception("attempts writable"); } catch (NotSupportedException) {}
    }
    [Test]
    public async Task attempt_usage_and_returned_model_are_captured()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        fake.Mode = "extra"; var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("A", 1)); fake.Mode = "ok"; var a = r.Capture.Attempts.Single(); await Assert.That(a.ResponseModel == "actual-model" && a.Usage!.InputTokenCount == 10 && a.Usage.TotalTokenCount == 20 && a.Usage.AdditionalCounts!["extra"] == 4 && r.Capture.Cost.Amount is null).IsTrue(); fake.LastUsage!.AdditionalCounts!["extra"] = 99; await Assert.That(a.Usage!.AdditionalCounts!["extra"] == 4).IsTrue();
    }
    [Test]
    public async Task cost_sums_attempts_once()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var f = new FakeClient(); using var e = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => f }, b => b.Use(async (messages, options, next, ct) => { await next.GetResponseAsync(messages, options, ct); return await next.GetResponseAsync(messages, options, ct); }, null), snapshot, collector, new FakeCost(), new("fake")); var r = (ExecutionResult.Success)await e.ExecuteAsync(new("A", 1)); await Assert.That(r.Capture.Attempts.Count == 2 && r.Capture.Cost.Amount == .06m).IsTrue();
    }
    [Test]
    public async Task unapproved_runtime_replacement_rejected()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var n = fake.Calls; snapshot.Value = new(Parameters: new Dictionary<string, JsonElement> { ["name"] = Json("\"override\"") }); var r = (ExecutionResult.Invalid)await executor.ExecuteAsync(new("original", 1)); snapshot.Value = null; await Assert.That(fake.Calls == n && r.Message.Contains("not allowed") && r.Capture.ParameterOrigins["name"] == "caller").IsTrue();
    }
    [Test]
    public async Task invalid_override_batch_is_atomic()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        snapshot.Value = new(Parameters: new Dictionary<string, JsonElement> { ["name"] = Json("\"changed\""), ["count"] = Json("\"bad\"") }); var r = (ExecutionResult.Invalid)await executor.ExecuteAsync(new("original", 1, AllowNameOverride: true, AllowCountOverride: true)); snapshot.Value = null; await Assert.That((string?)r.Capture.Parameters["name"] == "original" && r.Capture.ParameterOrigins["name"] == "caller").IsTrue();
    }
    [Test]
    public async Task snapshot_failure_captured()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        snapshot.Fail = true; var n = collector.Records.Count; var calls = fake.Calls; var r = await executor.ExecuteAsync(new("A", 1)); snapshot.Fail = false; await Assert.That(r is ExecutionResult.Failed f && f.Message == "snapshot failed" && f.Capture!.Attempts.Count == 0 && collector.Records.Count == n + 1 && fake.Calls == calls).IsTrue();
    }
    [Test]
    [NotInParallel]
    public async Task throwing_enricher_preserves_response_exactly_once()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var c = new FakeCollector(); long count = 0; using var ml = new MeterListener(); ml.InstrumentPublished = (i,l) => { if (i.Meter.Name == "ExecutionCapture.Spike") l.EnableMeasurementEvents(i); }; ml.SetMeasurementEventCallback<long>((i,v,t,s) => count += v); ml.Start(); using var e = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => new FakeClient() }, null, snapshot, c, new ThrowCost(), new("fake")); var r = (ExecutionResult.Success)await e.ExecuteAsync(new("A", 1)); await Assert.That(r.Response.Text == "answer" && r.Capture.Cost.Amount is null && r.Capture.EnrichmentWarning == "cost failed" && c.Records.Count == 1 && count == 1).IsTrue();
    }
    [Test]
    public async Task noncooperative_collector_is_bounded()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var c = new HangingCollector(); using var e = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => new FakeClient() }, null, snapshot, c, new FakeCost(), new("fake"), TimeSpan.FromMilliseconds(50)); var sw = Stopwatch.StartNew(); var r = (ExecutionResult.Success)await e.ExecuteAsync(new("A", 1)); await Assert.That(r.CollectorError == "collector timed out" && sw.Elapsed < TimeSpan.FromSeconds(2) && c.Cancelled).IsTrue(); c.Complete();
    }
    [Test]
    public async Task injected_braces_do_not_recurse()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("{count} {{x}}", 9)); await Assert.That(r.Capture.FormattedPrompt == "Hello {count} {{x}}, {count} {{x}}: 9. {literal}").IsTrue();
    }
    [Test]
    public async Task retry_and_provider_boundary_mutation()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var f = new FakeClient { FailFirst = true }; using var e = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => f }, b => { b.Use(async (messages, options, next, ct) => { try { return await next.GetResponseAsync(messages, options, ct); } catch (InvalidOperationException) { return await next.GetResponseAsync(messages, options, ct); } }, null); b.Use(async (messages, options, next, ct) => { var changed = new[] { new ChatMessage(ChatRole.User, messages.Single().Text + " [middleware]") }; options!.ModelId = "middleware-model"; return await next.GetResponseAsync(changed, options, ct); }, null); }, snapshot, collector, new FakeCost(), new("fake")); var r = (ExecutionResult.Success)await e.ExecuteAsync(new("A", 1)); await Assert.That(r.Capture.Attempts.Count == 2 && r.Capture.Attempts[0].Outcome == "failed" && r.Capture.Attempts[1].Outcome == "success").IsTrue(); await Assert.That(r.Capture.Attempts[1].Request.EndsWith(" [middleware]") && r.Capture.Attempts[1].Model == "middleware-model" && !r.Capture.FormattedPrompt.EndsWith(" [middleware]")).IsTrue(); await Assert.That(r.Capture.Cost.Amount is null).IsTrue();
    }
    [Test]
    public async Task concurrent_call_isolation()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => executor.ExecuteAsync(new($"name{i}", i), new(Model: $"model{i}")))); for (int i = 0; i < results.Length; i++) { var c = ((ExecutionResult.Success)results[i]).Capture; await Assert.That(c.Attempts.Count == 1 && c.Attempts[0].Request.Contains($"name{i}") && c.Attempts[0].Model == $"model{i}").IsTrue(); }
    }
    [Test]
    public async Task provider_failure_converted()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        fake.Mode = "fail"; var r = await executor.ExecuteAsync(new("A", 1)); await Assert.That(r is ExecutionResult.Failed f && f.Capture!.Attempts.Single().Outcome == "failed").IsTrue(); fake.Mode = "ok";
    }
    [Test]
    public async Task unrequested_cancellation_is_provider_failure()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        fake.Mode = "cancel"; var r = await executor.ExecuteAsync(new("A", 1)); await Assert.That(r is ExecutionResult.Failed f && f.Message.Contains("timeout") && f.Capture!.Attempts.Single().Outcome == "failed").IsTrue(); fake.Mode = "ok";
    }
    [Test]
    public async Task pre_cancelled_caller()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        using var cts = new CancellationTokenSource(); cts.Cancel(); var calls = fake.Calls; var r = await executor.ExecuteAsync(new("A", 1), cancellationToken: cts.Token); await Assert.That(r is ExecutionResult.Cancelled c && c.Capture!.Attempts.Count == 0 && fake.Calls == calls).IsTrue();
    }
    [Test]
    public async Task caller_cancellation_during_provider()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        fake.Mode = "wait"; using var cts = new CancellationTokenSource(); var task = executor.ExecuteAsync(new("A", 1), cancellationToken: cts.Token); await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); cts.Cancel(); var r = await task; await Assert.That(r is ExecutionResult.Cancelled c && c.Capture!.Attempts.Single().Outcome == "cancelled").IsTrue(); fake.Mode = "ok";
    }
    [Test]
    public async Task absent_usage_remains_unknown()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        fake.Mode = "no-usage"; var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("A", 1)); await Assert.That(r.Capture.ResponseInputTokens is null && r.Capture.Cost.Amount is null).IsTrue(); fake.Mode = "ok";
    }
    [Test]
    public async Task unsupported_usage_remains_unknown()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        fake.Mode = "reasoning"; var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("A", 1)); await Assert.That(r.Capture.Cost.Amount is null).IsTrue(); fake.Mode = "ok";
    }
    [Test]
    public async Task collector_failure_preserves_success()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        collector.Fail = true; var r = (ExecutionResult.Success)await executor.ExecuteAsync(new("A", 1)); await Assert.That(r.Response.Text == "answer" && r.CollectorError == "collector failed").IsTrue(); collector.Fail = false;
    }
    [Test]
    [NotInParallel]
    public async Task invalid_metric_and_collector_failure()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        long count = 0; string? outcome = null; using var ml = new MeterListener(); ml.InstrumentPublished = (i,l) => { if (i.Meter.Name == "ExecutionCapture.Spike") l.EnableMeasurementEvents(i); }; ml.SetMeasurementEventCallback<long>((i,v,t,s) => { count += v; foreach (var tag in t) if (tag.Key == "outcome") outcome = (string?)tag.Value; }); ml.Start(); collector.Fail = true; var calls = fake.Calls; var r = (ExecutionResult.Invalid)await executor.ExecuteAsync(new("A", 1), new(MaxTokens: -1)); collector.Fail = false; await Assert.That(count == 1 && outcome == "invalid" && r.CollectorError == "collector failed" && r.Capture.Attempts.Count == 0 && fake.Calls == calls).IsTrue();
    }
    [Test]
    [NotInParallel]
    public async Task activity_and_meter_bounded_tags()
    {
        var snapshot = new FakeSnapshot(); var collector = new FakeCollector(); var fake = new FakeClient(); int factories = 0;
        using var executor = new Executor(new Dictionary<string, Func<IChatClient>> { ["fake"] = () => { factories++; return fake; } }, null, snapshot, collector, new FakeCost(), new Settings("fake", "platform", .1f, 128));
        var basic = (ExecutionResult.Success)await executor.ExecuteAsync(new("Ada", 2));
        var tags = new List<KeyValuePair<string, object?>>(); long count = 0; string? expectedTrace = null, expectedSpan = null; using var al = new ActivityListener { ShouldListenTo = s => s.Name == "ExecutionCapture.Spike", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = a => { tags.AddRange(a.TagObjects); expectedTrace = a.TraceId.ToString(); expectedSpan = a.SpanId.ToString(); } }; ActivitySource.AddActivityListener(al); using var ml = new MeterListener(); ml.InstrumentPublished = (i,l) => { if (i.Meter.Name == "ExecutionCapture.Spike") l.EnableMeasurementEvents(i); }; ml.SetMeasurementEventCallback<long>((i,v,t,s) => { count += v; foreach (var tag in t) tags.Add(tag); }); ml.Start(); var traced = (ExecutionResult.Success)await executor.ExecuteAsync(new("private", 1)); await Assert.That(traced.Capture.TraceId == expectedTrace && traced.Capture.SpanId == expectedSpan && expectedTrace is not null).IsTrue(); await Assert.That(count == 1 && tags.All(t => t.Key == "llmcmd.outcome" || t.Key == "outcome") && tags.All(t => (string?)t.Value == "success")).IsTrue();
    }
}
sealed class FakeSnapshot : ISnapshotProvider { public Snapshot? Value; public bool Fail; public string? LastId; public ValueTask<Snapshot?> GetAsync(string id, CancellationToken ct) { LastId = id; if (Fail) throw new Exception("snapshot failed"); return ValueTask.FromResult(Value); } }
sealed class FakeCollector : ICollector { public bool Fail; public ConcurrentQueue<Capture> Records = new(); public ValueTask CollectAsync(Capture capture, CancellationToken ct) { if (Fail) throw new Exception("collector failed"); Records.Enqueue(capture); return ValueTask.CompletedTask; } }
sealed class FakeClient : IChatClient {
    int calls; public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); public UsageDetails? LastUsage; public int Calls => calls; public string Mode = "ok"; public bool FailFirst;
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) { var n = Interlocked.Increment(ref calls); await Task.Yield(); if (Mode == "wait") { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); } if (Mode == "cancel") throw new OperationCanceledException(); if (Mode == "fail" || (FailFirst && n == 1)) throw new InvalidOperationException("provider failed"); var usage = Mode == "no-usage" ? null : new UsageDetails { InputTokenCount = 10, OutputTokenCount = 10, TotalTokenCount = 20, ReasoningTokenCount = Mode == "reasoning" ? 2 : null, AdditionalCounts = Mode == "extra" ? new() { ["extra"] = 4 } : null }; LastUsage = usage; return new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")) { ModelId = "actual-model", Usage = usage }; }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null; public void Dispose() { }
}

sealed class ThrowCost : ICostEnricher { public Cost Calculate(IReadOnlyList<Attempt> attempts) => throw new Exception("cost failed"); }
sealed class HangingCollector : ICollector { readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Cancelled; public ValueTask CollectAsync(Capture capture, CancellationToken ct) { ct.Register(() => Cancelled = true); return new ValueTask(done.Task); } public void Complete() => done.TrySetResult(); }
