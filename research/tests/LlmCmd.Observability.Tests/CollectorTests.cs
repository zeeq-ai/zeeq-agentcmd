using System.Text.Json;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace LlmCmd.Observability.Tests;
public class CollectorTests
{
    const string DirectoryPath = "/tmp/opencode/llmcmd-otel";
    static IEnumerable<JsonElement> Objects(JsonElement item) {
        if (item.ValueKind == JsonValueKind.Object) { yield return item; foreach (var p in item.EnumerateObject()) foreach (var child in Objects(p.Value)) yield return child; }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var p in item.EnumerateArray()) foreach (var child in Objects(p)) yield return child;
    }
    static IEnumerable<JsonElement> FromFile(string path) {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path)) { JsonDocument? doc = null; try { doc = JsonDocument.Parse(line); } catch (JsonException) { } if (doc is null) continue; using (doc) foreach (var item in Objects(doc.RootElement)) yield return item.Clone(); }
    }
    static string? Value(JsonElement obj, string name) => obj.TryGetProperty(name, out var v) ? v.ToString() : null;
    static Dictionary<string,string> Attributes(JsonElement obj) => obj.GetProperty("attributes").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").EnumerateObject().Select(p => p.Value.ToString()).FirstOrDefault() ?? "");
    static async Task<JsonElement> FindSpan(string traceId, string parentId) {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline) {
            var match = FromFile(Path.Combine(DirectoryPath, "traces.jsonl")).FirstOrDefault(x => Value(x, "traceId") == traceId && Value(x, "parentSpanId") == parentId && x.TryGetProperty("attributes", out _) && x.GetRawText().Contains("gen_ai.operation.name"));
            if (match.ValueKind != JsonValueKind.Undefined) return match;
            await Task.Delay(100);
        }
        throw new Exception("GenAI span missing from collector trace output");
    }
    [Test]
    public async Task Collector_receives_real_MEAI_spans_and_metrics_with_opt_in_content() {
        var url = Environment.GetEnvironmentVariable("LLMCMD_PRODUCER_URL") ?? throw new InvalidOperationException("LLMCMD_PRODUCER_URL required; start Aspire and use its producer endpoint");
        using var client = new HttpClient { BaseAddress = new Uri(url) };
        var safePayload = JsonDocument.Parse(await (await client.PostAsync("/execute/false", null)).Content.ReadAsStringAsync()).RootElement.Clone();
        var fullPayload = JsonDocument.Parse(await (await client.PostAsync("/execute/true", null)).Content.ReadAsStringAsync()).RootElement.Clone();
        var missingPayload = JsonDocument.Parse(await (await client.PostAsync("/execute/false?omitUsage=true", null)).Content.ReadAsStringAsync()).RootElement.Clone();
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "envelope-safe.json"), safePayload.GetRawText());
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "envelope-sensitive.json"), fullPayload.GetRawText());
        await File.WriteAllTextAsync(Path.Combine(DirectoryPath, "envelope-missing-usage.json"), missingPayload.GetRawText());
        foreach (var payload in new[] { safePayload, fullPayload, missingPayload }) await Assert.That(payload.GetProperty("result").GetRawText()).IsEqualTo(payload.GetProperty("collected").GetRawText());
        var safe = safePayload.GetProperty("collected"); var full = fullPayload.GetProperty("collected"); var missing = missingPayload.GetProperty("collected");
        await Assert.That(missing.GetProperty("responseInputTokens").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(missing.GetProperty("responseOutputTokens").ValueKind).IsEqualTo(JsonValueKind.Null);
        var missingSpan = await FindSpan(missing.GetProperty("traceId").GetString()!, missing.GetProperty("spanId").GetString()!);
        await Assert.That(Attributes(missingSpan).ContainsKey("gen_ai.usage.input_tokens")).IsFalse();
        await Assert.That(Attributes(missingSpan).ContainsKey("gen_ai.usage.output_tokens")).IsFalse();
        foreach (var envelope in new[] {safe, full}) {
            await Assert.That(envelope.GetProperty("formattedPrompt").GetString()).IsEqualTo("Hello Ada, Ada: 2. {literal}");
            await Assert.That(envelope.GetProperty("originalTemplate").GetString()).IsEqualTo("Hello {name}, {name}: {count}. {{literal}}");
            await Assert.That(envelope.GetProperty("parameters").GetProperty("name").GetString()).IsEqualTo("Ada");
            await Assert.That(envelope.GetProperty("response").GetString()).IsEqualTo("fake response");
            await Assert.That(envelope.GetProperty("responseInputTokens").GetInt64()).IsEqualTo(7L);
            await Assert.That(envelope.GetProperty("responseOutputTokens").GetInt64()).IsEqualTo(3L);
        }
        var safeSpan = await FindSpan(safe.GetProperty("traceId").GetString()!, safe.GetProperty("spanId").GetString()!);
        var fullSpan = await FindSpan(full.GetProperty("traceId").GetString()!, full.GetProperty("spanId").GetString()!);
        var safeAttrs = Attributes(safeSpan); var fullAttrs = Attributes(fullSpan);
        string[] allowed = ["gen_ai.operation.name", "gen_ai.request.model", "gen_ai.provider.name", "gen_ai.request.temperature", "gen_ai.response.id", "gen_ai.response.model", "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens"];
        await Assert.That(safeAttrs.Keys.All(allowed.Contains)).IsTrue();
        await Assert.That(safeAttrs["gen_ai.operation.name"]).IsEqualTo("chat");
        await Assert.That(safeAttrs["gen_ai.request.model"]).IsEqualTo("fake-request-model");
        await Assert.That(safeAttrs["gen_ai.response.model"]).IsEqualTo("fake-response-model");
        await Assert.That(safeAttrs["gen_ai.usage.input_tokens"]).IsEqualTo("7");
        await Assert.That(safeAttrs["gen_ai.usage.output_tokens"]).IsEqualTo("3");
        await Assert.That(safeSpan.GetRawText().Contains("Hello Ada")).IsFalse();
        await Assert.That(safeSpan.GetRawText().Contains("fake response")).IsFalse();
        using var input = JsonDocument.Parse(fullAttrs["gen_ai.input.messages"]);
        using var output = JsonDocument.Parse(fullAttrs["gen_ai.output.messages"]);
        await Assert.That(input.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetString()).IsEqualTo(safe.GetProperty("formattedPrompt").GetString());
        await Assert.That(output.RootElement[0].GetProperty("parts")[0].GetProperty("content").GetString()).IsEqualTo(safe.GetProperty("response").GetString());
        var parent = FromFile(Path.Combine(DirectoryPath, "traces.jsonl")).Single(x => Value(x, "traceId") == safe.GetProperty("traceId").GetString() && Value(x, "spanId") == safe.GetProperty("spanId").GetString());
        await Assert.That(Value(parent, "name")).IsEqualTo("llmcmd.execute");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        bool ForThisSpan(JsonElement p) => p.TryGetProperty("exemplars", out var ex) && ex.EnumerateArray().Any(e => Value(e, "traceId") == safe.GetProperty("traceId").GetString());
        JsonElement[] points = []; bool durationFound = false, inputFound = false, outputFound = false;
        while (DateTime.UtcNow < deadline) {
            var all = FromFile(Path.Combine(DirectoryPath, "metrics.jsonl")).ToArray();
            var token = all.Where(x => Value(x, "name") == "gen_ai.client.token.usage");
            points = token.SelectMany(m => m.GetProperty("histogram").GetProperty("dataPoints").EnumerateArray().Select(p => p.Clone())).ToArray();
            bool Matches(JsonElement p, string kind, double expected) => Attributes(p).GetValueOrDefault("gen_ai.token.type") == kind && ForThisSpan(p) && p.GetProperty("sum").GetDouble() / double.Parse(p.GetProperty("count").GetString()!) == expected;
            inputFound = points.Any(p => Matches(p, "input", 7)); outputFound = points.Any(p => Matches(p, "output", 3));
            durationFound = all.Where(x => Value(x, "name") == "gen_ai.client.operation.duration").SelectMany(m => m.GetProperty("histogram").GetProperty("dataPoints").EnumerateArray().Select(p => p.Clone())).Any(ForThisSpan);
            if (inputFound && outputFound && durationFound) break;
            await Task.Delay(100);
        }
        await Assert.That(inputFound).IsTrue(); await Assert.That(outputFound).IsTrue(); await Assert.That(durationFound).IsTrue();
        await Assert.That(points.All(p => !p.GetRawText().Contains("Hello Ada") && !p.GetRawText().Contains("fake response"))).IsTrue();
    }
}
