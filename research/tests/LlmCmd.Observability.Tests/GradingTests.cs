using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace LlmCmd.Observability.Tests;
public class GradingTests
{
    const string Evidence = "/tmp/opencode/llmcmd-otel";
    static IEnumerable<JsonElement> Objects(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) { yield return value; foreach (var property in value.EnumerateObject()) foreach (var child in Objects(property.Value)) yield return child; }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) foreach (var child in Objects(item)) yield return child;
    }
    static IEnumerable<JsonElement> TraceObjects() {
        var path = Path.Combine(Evidence,"traces.jsonl"); if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path)) { JsonDocument? d=null; try { d=JsonDocument.Parse(line); } catch (JsonException) { } if (d is null) continue; using (d) foreach (var x in Objects(d.RootElement)) yield return x.Clone(); }
    }
    static string? Value(JsonElement value,string key) => value.TryGetProperty(key,out var p) ? p.ToString() : null;
    static Dictionary<string,string> Attrs(JsonElement obj) => obj.GetProperty("attributes").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").EnumerateObject().Select(p => p.Value.ToString()).FirstOrDefault() ?? "");
    static async Task<JsonElement> Post(HttpClient client,string path, object? body, string trace) {
        using var request = new HttpRequestMessage(HttpMethod.Post,path);
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{trace}-{ActivitySpanId.CreateRandom()}-01");
        if (body is not null) request.Content=JsonContent.Create(body);
        using var response=await client.SendAsync(request); var text=await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new Exception($"HTTP {(int)response.StatusCode}: {text}");
        using var document=JsonDocument.Parse(text); return document.RootElement.Clone();
    }
    static async Task<JsonElement> LinkedSpan(string trace, string originalTrace, string originalSpan, string run) {
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline) {
            var s=TraceObjects().FirstOrDefault(x => Value(x,"traceId") == trace && x.TryGetProperty("links",out var links) && links.EnumerateArray().Any(l => Value(l,"traceId") == originalTrace && Value(l,"spanId") == originalSpan) && x.GetRawText().Contains(run,StringComparison.OrdinalIgnoreCase));
            if (s.ValueKind != JsonValueKind.Undefined) return s; await Task.Delay(100);
        }
        throw new Exception("Linked follow-up span not present in collector output");
    }
    [Test]
    public async Task Separate_requests_link_correction_and_grade_to_original_extraction() {
        string url=Environment.GetEnvironmentVariable("LLMCMD_PRODUCER_URL") ?? throw new InvalidOperationException("LLMCMD_PRODUCER_URL required");
        using var client=new HttpClient { BaseAddress=new Uri(url) };
        string extractionTrace=ActivityTraceId.CreateRandom().ToString(), correctionTrace=ActivityTraceId.CreateRandom().ToString(), gradeTrace=ActivityTraceId.CreateRandom().ToString();
        var extracted=await Post(client,"/runs/extract",null,extractionTrace);
        var output=extracted.GetProperty("output"); var reference=output.GetProperty("reference"); var execution=extracted.GetProperty("execution");
        string run=reference.GetProperty("runId").GetString()!, originTrace=execution.GetProperty("traceId").GetString()!, originSpan=execution.GetProperty("spanId").GetString()!;
        await Assert.That(originTrace).IsEqualTo(extractionTrace);
        await Assert.That(output.GetProperty("value").GetProperty("items")[0].GetProperty("quantity").GetInt32()).IsEqualTo(2);
        await Assert.That(reference.GetProperty("executionId").GetString()).IsEqualTo(execution.GetProperty("executionId").GetString());
        var corrected=await Post(client,"/runs/corrections",new { eventId=Guid.NewGuid(), target=reference, pointer="/items/0/quantity", expectedValue=2, replacement=5 },correctionTrace);
        var changed=corrected.GetProperty("output");
        await Assert.That(changed.GetProperty("value").GetProperty("items")[0].GetProperty("quantity").GetInt32()).IsEqualTo(5);
        await Assert.That(changed.GetProperty("reference").GetProperty("runId").GetString()).IsEqualTo(run);
        await Assert.That(changed.GetProperty("reference").GetProperty("versionId").GetString()).IsNotEqualTo(reference.GetProperty("versionId").GetString());
        await Assert.That(changed.GetProperty("parentVersionId").GetString()).IsEqualTo(reference.GetProperty("versionId").GetString());
        await Assert.That(output.GetProperty("value").GetProperty("items")[0].GetProperty("quantity").GetInt32()).IsEqualTo(2);
        var grade=await Post(client,"/runs/grades",new { eventId=Guid.NewGuid(), target=reference, pointer="/items/0/quantity", expectedValue=2, score=0.75, rationale="review" },gradeTrace);
        await Assert.That(grade.GetProperty("event").GetProperty("target").GetProperty("versionId").GetString()).IsEqualTo(reference.GetProperty("versionId").GetString());
        await Assert.That(grade.GetProperty("event").GetProperty("pointer").GetString()).IsEqualTo("/items/0/quantity");
        await Assert.That(grade.GetProperty("event").GetProperty("score").GetDouble()).IsEqualTo(0.75);
        using var conflict = await client.PostAsJsonAsync("/runs/grades", new { eventId = Guid.NewGuid(), target = reference, pointer = "/items/0/quantity", expectedValue = 999, score = 0.5 });
        await Assert.That((int)conflict.StatusCode).IsEqualTo(409);
        var correctionSpan=await LinkedSpan(correctionTrace,originTrace,originSpan,run);
        var gradeSpan=await LinkedSpan(gradeTrace,originTrace,originSpan,run);
        await Assert.That(correctionTrace).IsNotEqualTo(originTrace); await Assert.That(gradeTrace).IsNotEqualTo(originTrace); await Assert.That(gradeTrace).IsNotEqualTo(correctionTrace);
        var gradeAttrs=Attrs(gradeSpan); var correctionAttrs=Attrs(correctionSpan);
        await Assert.That(Value(gradeSpan,"name")).IsEqualTo("llmcmd.human.grade"); await Assert.That(Value(correctionSpan,"name")).IsEqualTo("llmcmd.human.correction");
        await Assert.That(gradeAttrs["llmcmd.feedback.pointer"]).IsEqualTo("/items/0/quantity"); await Assert.That(correctionAttrs["llmcmd.feedback.pointer"]).IsEqualTo("/items/0/quantity");
        await Assert.That(gradeAttrs["llmcmd.output.version_id"]).IsEqualTo(reference.GetProperty("versionId").GetString());
        await Assert.That(gradeAttrs["llmcmd.execution.id"]).IsEqualTo(reference.GetProperty("executionId").GetString());
        var evaluation=gradeSpan.GetProperty("events").EnumerateArray().Single(e => Value(e,"name") == "gen_ai.evaluation.result");
        await Assert.That(Attrs(evaluation)["gen_ai.evaluation.score.value"]).IsEqualTo("0.75");
        await File.WriteAllTextAsync(Path.Combine(Evidence,"grading-evidence.json"),JsonSerializer.Serialize(new { extracted,corrected,grade,correctionSpan,gradeSpan },new JsonSerializerOptions { WriteIndented=true }));
    }
}
