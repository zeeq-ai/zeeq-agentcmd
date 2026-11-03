using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace LlmCmd.PropagationHost.Tests;

[NotInParallel]
public class HostTests
{
    static readonly string Evidence="/tmp/opencode/llmcmd-propagation";
    static readonly HttpClient Edge=new() { BaseAddress=new Uri(Environment.GetEnvironmentVariable("LLMCMD_EDGE_URL") ?? throw new InvalidOperationException("Start through scripts/verify_propagation.py")) };
    static readonly HttpClient Worker=new() { BaseAddress=new Uri(Environment.GetEnvironmentVariable("LLMCMD_WORKER_URL")!) };
    static async Task<JsonElement> Post(HttpClient client,string path,object body)
    {
        // TUnit has its own ambient test activity. Simulate independent HTTP operations explicitly.
        using var operation=new Activity("independent.http.operation").SetParentId(ActivityTraceId.CreateRandom(),ActivitySpanId.CreateRandom(),ActivityTraceFlags.Recorded).Start();
        using var response=await client.PostAsJsonAsync(path,body); response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.Clone();
    }
    static async Task<JsonElement> Get(HttpClient client,string path)
    {
        using var operation=new Activity("independent.http.operation").SetParentId(ActivityTraceId.CreateRandom(),ActivitySpanId.CreateRandom(),ActivityTraceFlags.Recorded).Start();
        using var response=await client.GetAsync(path); response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.Clone();
    }
    static Task<JsonElement> Register()=>Post(Worker,"/register",new { });
    static Task<JsonElement> Publish(JsonElement context,Guid work,string? raw=null)=>Post(Edge,"/publish",new {context,workId=work,payload=new {document="synthetic document"},slotId="header",rawEnvelope=raw});
    static Guid Draft(JsonElement reference)=>reference.GetProperty("draft").GetProperty("draftId").GetGuid();

    [Test] public async Task Real_RabbitMQ_redelivery_replays_stored_receipt_with_no_second_provider_call()
    {
        var reference=await Register(); var work=Guid.NewGuid(); var produced=await Publish(reference,work);
        var first=await Post(Worker,"/drain",new {loseAck=true}); var replay=await Post(Worker,"/drain",new { });
        await File.WriteAllTextAsync(Path.Combine(Evidence,"queue-evidence.json"),JsonSerializer.Serialize(new {reference,produced,first,replay}));
        await Assert.That(first.GetProperty("status").GetString()).IsEqualTo("handled");
        await Assert.That(first.GetProperty("scopeRestored").GetBoolean()).IsTrue();
        await Assert.That(replay.GetProperty("status").GetString()).IsEqualTo("replayed");
        await Assert.That(replay.GetProperty("redelivered").GetBoolean()).IsTrue();
        await Assert.That(first.GetProperty("providerCalls").GetInt32()).IsEqualTo(replay.GetProperty("providerCalls").GetInt32());
        var receipt=first.GetProperty("receipt"); var repeated=replay.GetProperty("receipt");
        await Assert.That(receipt.GetProperty("output").GetRawText()).IsEqualTo(repeated.GetProperty("output").GetRawText());
        await Assert.That(receipt.GetProperty("processId").GetInt32()).IsNotEqualTo(produced.GetProperty("pid").GetInt32());
        await Assert.That(receipt.GetProperty("context").GetProperty("operation").GetProperty("draftId").GetGuid()).IsEqualTo(Draft(reference));
        await Assert.That(receipt.GetProperty("context").GetProperty("operation").GetProperty("workId").GetGuid()).IsEqualTo(work);
        var events=await Get(Worker,"/events"); var execution=receipt.GetProperty("context").GetProperty("executionId").GetGuid();
        var records=events.EnumerateArray().Where(e=>e.GetProperty("context").GetProperty("executionId").GetGuid()==execution).ToArray();
        await Assert.That(records.Length).IsEqualTo(5);
        await Assert.That(records.All(e=>e.GetProperty("context").GetProperty("operation").GetProperty("workId").GetGuid()==work)).IsTrue();

        await Post(Edge,"/flush",new {}); await Post(Worker,"/flush",new {});
        var sourceTrace=produced.GetProperty("traceId").GetString()!; var sourceSpan=produced.GetProperty("spanId").GetString()!;
        var deadline=DateTime.UtcNow.AddSeconds(12); List<JsonElement> spans=[];
        while(DateTime.UtcNow<deadline)
        {
            spans=ReadSpans();
            if(spans.Any(s=>Tag(s,"llmcmd.work.id")==work.ToString() && s.GetProperty("name").GetString()=="llmcmd.execute")) break;
            await Task.Delay(100);
        }
        var command=spans.Single(s=>Tag(s,"llmcmd.work.id")==work.ToString() && s.GetProperty("name").GetString()=="llmcmd.execute");
        var attempt=spans.Single(s=>Tag(s,"llmcmd.work.id")==work.ToString() && s.GetProperty("name").GetString()=="llmcmd.provider.attempt");
        var consumer=spans.Single(s=>Tag(s,"llmcmd.work.id")==work.ToString() && s.GetProperty("name").GetString()=="llmcmd.message.process");
        await Assert.That(Tag(command,"llmcmd.output.draft_id")).IsEqualTo(Draft(reference).ToString());
        await Assert.That(Tag(attempt,"llmcmd.output.draft_id")).IsEqualTo(Draft(reference).ToString());
        await Assert.That(command.GetProperty("traceId").GetString()).IsNotEqualTo(sourceTrace);
        await Assert.That(consumer.GetProperty("links").EnumerateArray().Any(l=>l.GetProperty("traceId").GetString()==sourceTrace && l.GetProperty("spanId").GetString()==sourceSpan)).IsTrue();
    }

    [Test] public async Task Malformed_and_unregistered_messages_do_not_spend_or_contaminate_next_draft()
    {
        var valid=await Register(); await Publish(valid,Guid.NewGuid(),"{\"version\":99}");
        var malformed=await Post(Worker,"/drain",new {});
        var unregistered=JsonSerializer.SerializeToElement(new { contextId=Guid.NewGuid(),runId=Guid.NewGuid(),draft=valid.GetProperty("draft"),trialId="trial-1" });
        await Publish(unregistered,Guid.NewGuid()); var rejected=await Post(Worker,"/drain",new {});
        var next=await Register(); var work=Guid.NewGuid(); await Publish(next,work); var succeeded=await Post(Worker,"/drain",new {});
        await File.WriteAllTextAsync(Path.Combine(Evidence,"isolation-evidence.json"),JsonSerializer.Serialize(new {malformed,rejected,next,succeeded}));
        await Assert.That(malformed.GetProperty("status").GetString()).IsEqualTo("rejected");
        await Assert.That(rejected.GetProperty("status").GetString()).IsEqualTo("rejected");
        await Assert.That(malformed.GetProperty("providerCalls").GetInt32()).IsEqualTo(rejected.GetProperty("providerCalls").GetInt32());
        await Assert.That(succeeded.GetProperty("providerCalls").GetInt32()).IsEqualTo(rejected.GetProperty("providerCalls").GetInt32()+1);
        await Assert.That(succeeded.GetProperty("receipt").GetProperty("context").GetProperty("operation").GetProperty("draftId").GetGuid()).IsEqualTo(Draft(next));
        await Assert.That(succeeded.GetProperty("scopeRestored").GetBoolean()).IsTrue();
    }

    [Test] public async Task Internal_HTTP_restores_context_but_external_provider_wire_has_no_private_context_and_no_redirect_follow()
    {
        var reference=await Register(); var context=await Post(Edge,"/forward",reference);
        await Assert.That(context.GetProperty("operation").GetProperty("draftId").GetGuid()).IsEqualTo(Draft(reference));
        var redirect=await Post(Worker,"/egress-redirect",new {});
        await Assert.That(redirect.GetProperty("status").GetInt32()).IsEqualTo(302);
        var wire=await Get(Edge,"/wire"); await File.WriteAllTextAsync(Path.Combine(Evidence,"http-wire-evidence.json"),wire.GetRawText());
        await Assert.That(wire.GetArrayLength()>0).IsTrue();
        foreach(var request in wire.EnumerateArray())
        {
            await Assert.That(request.GetProperty("path").GetString()!.Contains("redirected=true")).IsFalse();
            foreach(var header in request.GetProperty("headers").EnumerateArray())
            {
                var key=header.GetProperty("name").GetString()!; var value=header.GetProperty("value").GetString()!;
                await Assert.That(key.StartsWith("llmcmd-",StringComparison.OrdinalIgnoreCase)).IsFalse();
                await Assert.That(value.Contains("llmcmd",StringComparison.OrdinalIgnoreCase)).IsFalse();
                await Assert.That(value.Contains("must-not-leak",StringComparison.Ordinal)).IsFalse();
                await Assert.That(value.Contains(Draft(reference).ToString(),StringComparison.OrdinalIgnoreCase)).IsFalse();
            }
        }
    }
    static List<JsonElement> ReadSpans()
    {
        var result=new List<JsonElement>(); var path=Path.Combine(Evidence,"otel","traces.jsonl");
        if(!File.Exists(path)) return result;
        foreach(var line in File.ReadLines(path))
        {
            try
            {
                using var doc=JsonDocument.Parse(line);
                foreach(var resource in doc.RootElement.GetProperty("resourceSpans").EnumerateArray())
                foreach(var scope in resource.GetProperty("scopeSpans").EnumerateArray())
                foreach(var span in scope.GetProperty("spans").EnumerateArray()) result.Add(span.Clone());
            }
            catch(JsonException) { /* A final exporter line may still be in flight. */ }
        }
        return result;
    }
    static string? Tag(JsonElement span,string key)=>span.TryGetProperty("attributes",out var attributes)
        ? attributes.EnumerateArray().Where(a=>a.GetProperty("key").GetString()==key)
            .Select(a=>a.GetProperty("value").GetProperty("stringValue").GetString()).SingleOrDefault()
        : null;
}
