using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LlmCmd.Observability.Tests;

public class ProfileRoutingTests
{
    const string Evidence = "/tmp/opencode/llmcmd-otel";
    static IEnumerable<JsonElement> Objects(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object) { yield return item; foreach (var p in item.EnumerateObject()) foreach (var c in Objects(p.Value)) yield return c; }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var p in item.EnumerateArray()) foreach (var c in Objects(p)) yield return c;
    }
    static IEnumerable<JsonElement> Read(string file)
    {
        foreach (var line in File.ReadLines(Path.Combine(Evidence, file)))
        {
            JsonDocument? doc = null; try { doc = JsonDocument.Parse(line); } catch (JsonException) { }
            if (doc is null) continue;
            using (doc) foreach (var item in Objects(doc.RootElement)) yield return item.Clone();
        }
    }
    static string? Value(JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v.ToString() : null;
    static Dictionary<string, string> Attributes(JsonElement obj) => obj.GetProperty("attributes").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").EnumerateObject().Select(v => v.Value.ToString()).FirstOrDefault() ?? "");
    static async Task<JsonElement> Execute(HttpClient client, string trace, bool fail)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/profiles/openai?failPrimary={fail.ToString().ToLowerInvariant()}");
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        using var response = await client.SendAsync(request); var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        using var json = JsonDocument.Parse(body); return json.RootElement.Clone();
    }
    [Test]
    public async Task Collector_receives_profile_fallback_event_attempt_spans_and_bounded_metric_labels()
    {
        var url = Environment.GetEnvironmentVariable("LLMCMD_PRODUCER_URL") ?? throw new InvalidOperationException("LLMCMD_PRODUCER_URL required");
        using var client = new HttpClient { BaseAddress = new Uri(url) };
        var trace = ActivityTraceId.CreateRandom().ToString(); var healthyTrace = ActivityTraceId.CreateRandom().ToString();
        var fallback = await Execute(client, trace, true); var healthy = await Execute(client, healthyTrace, false);
        await File.WriteAllTextAsync(Path.Combine(Evidence, "fallback-envelopes.json"), JsonSerializer.Serialize(new { fallback, healthy }, new JsonSerializerOptions { WriteIndented = true }));
        var route = fallback.GetProperty("snapshot").GetProperty("routing");
        await Assert.That(route.GetProperty("requestedProfile").GetString()).IsEqualTo("extraction.openai");
        await Assert.That(route.GetProperty("servedProfile").GetString()).IsEqualTo("extraction.anthropic");
        await Assert.That(route.GetProperty("attempts").GetArrayLength()).IsEqualTo(3);
        await Assert.That(healthy.GetProperty("snapshot").GetProperty("routing").GetProperty("attempts").GetArrayLength()).IsEqualTo(1);
        await Assert.That(fallback.GetProperty("origin").GetProperty("traceId").GetString()).IsEqualTo(trace);
        JsonElement[] spans = []; JsonElement[] fallbackPoints = []; JsonElement[] durationPoints = [];
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            spans = Read("traces.jsonl").Where(x => (Value(x, "traceId") == trace || Value(x, "traceId") == healthyTrace) && x.TryGetProperty("spanId", out _) && x.TryGetProperty("name", out _)).ToArray();
            var all = Read("metrics.jsonl").ToArray();
            fallbackPoints = all.Where(x => Value(x, "name") == "llmcmd.profile.fallbacks").SelectMany(x => x.GetProperty("sum").GetProperty("dataPoints").EnumerateArray().Select(p => p.Clone())).Where(p => Attributes(p).GetValueOrDefault("llmcmd.profile.from") == "extraction.openai").ToArray();
            durationPoints = all.Where(x => Value(x, "name") == "llmcmd.provider.attempt.duration").SelectMany(x => x.GetProperty("histogram").GetProperty("dataPoints").EnumerateArray().Select(p => p.Clone())).Where(p => p.TryGetProperty("exemplars", out var e) && e.EnumerateArray().Any(v => Value(v, "traceId") == trace)).ToArray();
            if (spans.Count(s => Value(s, "name") == "llmcmd.provider.attempt" && Value(s, "traceId") == trace) == 3 && spans.Any(s => Value(s, "name") == "llmcmd.execute" && Value(s, "traceId") == healthyTrace) && fallbackPoints.Length > 0 && durationPoints.Length > 0) break;
            await Task.Delay(100);
        }
        var root = spans.Single(s => Value(s, "name") == "llmcmd.execute" && Value(s, "traceId") == trace);
        var attempts = spans.Where(s => Value(s, "name") == "llmcmd.provider.attempt" && Value(s, "traceId") == trace).ToArray();
        await Assert.That(attempts.Length).IsEqualTo(3);
        await Assert.That(attempts.All(a => Value(a, "parentSpanId") == Value(root, "spanId"))).IsTrue();
        await Assert.That(attempts.Count(a => Attributes(a)["gen_ai.provider.name"] == "openai")).IsEqualTo(2);
        await Assert.That(attempts.Count(a => Attributes(a)["gen_ai.provider.name"] == "anthropic")).IsEqualTo(1);
        await Assert.That(attempts.Count(a => a.TryGetProperty("status", out var s) && Value(s, "code") == "2")).IsEqualTo(2);
        var events = root.GetProperty("events").EnumerateArray().Where(e => Value(e, "name") == "llmcmd.profile.fallback").ToArray();
        await Assert.That(events.Length).IsEqualTo(1);
        await Assert.That(Attributes(events[0])["llmcmd.profile.to"]).IsEqualTo("extraction.anthropic");
        await Assert.That(Attributes(events[0])["llmcmd.fallback.reason"]).IsEqualTo("Transient");
        await Assert.That(Attributes(root)["llmcmd.execution.id"]).IsEqualTo(fallback.GetProperty("executionId").GetString());
        await Assert.That(Attributes(root)["llmcmd.run.id"]).IsEqualTo(fallback.GetProperty("runId").GetString());
        var normal = spans.Single(s => Value(s, "name") == "llmcmd.execute" && Value(s, "traceId") == healthyTrace);
        await Assert.That(normal.TryGetProperty("events", out var n) && n.EnumerateArray().Any(e => Value(e, "name") == "llmcmd.profile.fallback")).IsFalse();
        await Assert.That(fallbackPoints.Length > 0).IsTrue();
        await Assert.That(fallbackPoints.All(p => Value(p, "asInt") == "1")).IsTrue(); // Repeated cumulative exports are not summed.
        string[] allowed = ["llmcmd.profile.requested", "llmcmd.profile.from", "llmcmd.profile.to", "llmcmd.fallback.reason"];
        await Assert.That(fallbackPoints.All(p => Attributes(p).Keys.All(allowed.Contains))).IsTrue();
        await Assert.That(durationPoints.Length > 0).IsTrue();
        var modelSpans = spans.Where(s => Value(s, "traceId") == trace && s.TryGetProperty("attributes", out _) && Attributes(s).ContainsKey("gen_ai.operation.name")).ToArray();
        await Assert.That(modelSpans.Length).IsEqualTo(3);
        await Assert.That(modelSpans.All(s => attempts.Any(a => Value(a, "spanId") == Value(s, "parentSpanId")))).IsTrue();
        await File.WriteAllTextAsync(Path.Combine(Evidence, "fallback-evidence.json"), JsonSerializer.Serialize(new { fallback, healthy, root, attempts, events, fallbackPoints, durationPoints }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
