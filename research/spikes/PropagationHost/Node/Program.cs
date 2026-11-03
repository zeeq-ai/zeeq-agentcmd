using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CommandRoot;
using HumanGrading;
using LlmCmd.ContextPropagation;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ProfileRouting;
using RabbitMQ.Client;

var builder=WebApplication.CreateBuilder(args);
var role=builder.Configuration["ROLE"] ?? throw new InvalidOperationException("ROLE missing");
var evidence="/tmp/opencode/llmcmd-propagation";
Directory.CreateDirectory(Path.Combine(evidence,role));
var connectionString=builder.Configuration.GetConnectionString("queue") ?? builder.Configuration["QUEUE_URI"]
    ?? throw new InvalidOperationException("Aspire queue reference missing");
await using var connection=await new ConnectionFactory { Uri=new Uri(connectionString) }.CreateConnectionAsync();
await using var channel=await connection.CreateChannelAsync(new CreateChannelOptions(true,true));
await channel.QueueDeclareAsync("llmcmd-propagation-work",durable:true,exclusive:false,autoDelete:false);
var gate=new SemaphoreSlim(1);
var adapter=new PropagationAdapter(); var text=new TextCarrierCodec(); var binary=new BinaryCarrierCodec();
using var source=new ActivitySource("LlmCmd.PropagationHost");
using var tracing=Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("llmcmd-propagation-"+role))
    .AddSource(source.Name,"CommandRoot.Spike",ProfileTelemetry.SourceName,"LlmCmd.ContextPropagation")
    .AddHttpClientInstrumentation().AddAspNetCoreInstrumentation()
    .AddOtlpExporter(o=> { o.Endpoint=new Uri(builder.Configuration["CAPTURE_OTLP"]!); o.Protocol=OtlpExportProtocol.Grpc; })
    .Build();
var wires=new ConcurrentQueue<WireRequest>();
var app=builder.Build();
app.MapGet("/health",()=>Results.Ok(new { role, pid=Environment.ProcessId }));
app.MapPost("/flush",()=>Results.Ok(tracing.ForceFlush()));

if(role=="edge")
{
    app.MapPost("/publish",async (PublishRequest request,CancellationToken ct)=>
    {
        using var created=source.StartActivity("llmcmd.message.create",ActivityKind.Producer);
        var context=OperationContext.FromResolvedReference(request.Context);
        created?.SetTag("llmcmd.output.draft_id",context.Reference.Draft?.DraftId.ToString());
        created?.SetTag("llmcmd.work.id",request.WorkId.ToString());
        var body=request.RawEnvelope is null ? adapter.EncodeWork(context,request.WorkId,request.SlotId,request.Payload) : Encoding.UTF8.GetBytes(request.RawEnvelope);
        IReadOnlyList<KeyValuePair<string,object>> initial=[];
        var headers=((InjectionResult<IReadOnlyList<KeyValuePair<string,object>>>.Injected)
            adapter.Inject(context,created?.Context ?? default,initial,binary,true)).Carrier;
        var properties=new BasicProperties
        {
            Persistent=true,MessageId=request.WorkId.ToString(),ContentType="application/json",
            Headers=headers.ToDictionary(p=>p.Key,p=>(object?)p.Value)
        };
        await gate.WaitAsync(ct);
        try { await channel.BasicPublishAsync("","llmcmd-propagation-work",true,properties,body,ct); }
        finally { gate.Release(); }
        return Results.Ok(new { workId=request.WorkId,pid=Environment.ProcessId,traceId=created?.TraceId.ToString(),spanId=created?.SpanId.ToString() });
    });
    app.MapPost("/forward",async (OperationContextReference reference,CancellationToken ct)=>
    {
        using var created=source.StartActivity("llmcmd.internal.forward",ActivityKind.Client);
        var headers=((InjectionResult<HeaderValues>.Injected)adapter.Inject(OperationContext.FromResolvedReference(reference),created?.Context ?? default,HeaderValues.Empty,text,true)).Carrier;
        using var client=new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false });
        using var message=new HttpRequestMessage(HttpMethod.Post,builder.Configuration["WORKER_URL"]+"/internal");
        foreach(var h in headers.Entries) message.Headers.TryAddWithoutValidation(h.Name,h.Value);
        using var response=await client.SendAsync(message,ct); var body=await response.Content.ReadAsStringAsync(ct);
        return Results.Content(body,"application/json",statusCode:(int)response.StatusCode);
    });
    app.MapMethods("/provider",["GET","POST"],(HttpRequest request)=>
    {
        wires.Enqueue(new(request.Path+request.QueryString,request.Headers.SelectMany(h=>h.Value.Select(v=>new HeaderValue(h.Key,v!))).ToArray()));
        return Results.Text(JsonSerializer.Serialize(new Invoice(12.5m,"USD")),"application/json");
    });
    app.MapGet("/redirect",(HttpRequest request)=>
    {
        wires.Enqueue(new("/redirect",request.Headers.SelectMany(h=>h.Value.Select(v=>new HeaderValue(h.Key,v!))).ToArray()));
        return Results.Redirect("/provider?redirected=true");
    });
    app.MapGet("/wire",()=>Results.Json(wires.ToArray()));
}
else
{
    var state=new WorkerState(Path.Combine(evidence,"worker"));
    var events=new EventWriter(Path.Combine(evidence,"worker","events.jsonl"));
    var provider=ExternalProviderHttp.CreateClient();
    app.Lifetime.ApplicationStopped.Register(provider.Dispose);
    var model=new HttpModel(provider,builder.Configuration["EDGE_URL"]!,source);
    var runtime=CommandRuntime.Create(()=>model,new Dictionary<string,Profile> { ["extraction"]=new("synthetic-wire-model") },state.Ledger,()=>state.Actor,events:events);
    app.Lifetime.ApplicationStopped.Register(runtime.Dispose);
    var extract=new ExtractInvoice(runtime);
    app.MapPost("/register",()=>Results.Json(state.Register()));
    app.MapGet("/events",()=>Results.Json(events.Records.ToArray()));
    app.MapPost("/drain",async (DrainRequest request,CancellationToken ct)=>
    {
        await gate.WaitAsync(ct);
        try
        {
            var delivery=await channel.BasicGetAsync("llmcmd-propagation-work",false,ct);
            if(delivery is null) return Results.NotFound(new { code="empty" });
            var body=delivery.Body.ToArray(); // Own bytes before any asynchronous handling.
            IReadOnlyList<KeyValuePair<string,object>> raw=delivery.BasicProperties.Headers?.Select(h=>new KeyValuePair<string,object>(h.Key,h.Value!)).ToArray() ?? [];
            await File.WriteAllTextAsync(Path.Combine(evidence,"last-carrier.json"),JsonSerializer.Serialize(new
            {
                body=Encoding.UTF8.GetString(body),headers=raw.Select(h=>new { key=h.Key,type=h.Value.GetType().FullName,
                    text=h.Value is byte[] b ? Encoding.UTF8.GetString(b) : h.Value as string })
            }),ct);
            ParseResult parsed;
            try { parsed=adapter.Extract(RabbitMqContextHeaders.Snapshot(delivery.BasicProperties),text,body,IngressPolicy.Component); }
            catch { parsed=new ParseResult.Malformed("broker_context_header_invalid"); }
            if(parsed is not ParseResult.Parsed valid)
            {
                await channel.BasicRejectAsync(delivery.DeliveryTag,false,ct);
                return Results.Json(new { status="rejected",reason=parsed switch { ParseResult.Malformed m=>m.Code,ParseResult.LimitExceeded l=>l.Limit,ParseResult.UnsupportedVersion=>"unsupported_version",_=>"absent" },providerCalls=model.Calls });
            }
            var incoming=valid.Incoming; var work=incoming.Work!;
            if(await state.ResolveAsync(incoming,ct) is not ContextResolution.Resolved)
            {
                await channel.BasicRejectAsync(delivery.DeliveryTag,false,ct);
                return Results.Json(new { status="rejected",reason="not_found",providerCalls=model.Calls });
            }
            // Host-specific serial claim store. A pending request remains indeterminate; this proof never retries unknown spend.
            var prior=state.Find(work.WorkId,incoming.Fingerprint!);
            if(prior.Conflict)
            {
                await channel.BasicRejectAsync(delivery.DeliveryTag,false,ct);
                return Results.Json(new { status="conflict",providerCalls=model.Calls });
            }
            if(prior.Receipt is not null)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag,false,ct);
                return Results.Json(new { status="replayed",receipt=prior.Receipt,redelivered=delivery.Redelivered,providerCalls=model.Calls });
            }
            if(prior.Pending)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag,false,true,ct);
                return Results.Json(new { status="indeterminate",providerCalls=model.Calls });
            }
            var result=await PropagationIngress.HandleAsync(incoming,state,async (context,envelope,token)=>
            {
                state.Start(work.WorkId,incoming.Fingerprint!);
                var outcome=await extract.WithParameters(new(envelope!.Payload.GetProperty("document").GetString()!)).ExecuteAsync(token);
                if(outcome is not Outcome<Invoice>.Success success || success.Delivery is not OutputDelivery.Stored)
                {
                    state.LastFailure=outcome is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidInput invalid } ? invalid.Reason : outcome.GetType().Name;
                    throw new InvalidOperationException("Proof output was not stored");
                }
                var stored=(OutputDelivery.Stored)success.Delivery;
                var receipt=new WorkReceipt(work.WorkId,incoming.Fingerprint!,success.Context!,success.Value,stored.Output.Reference,Environment.ProcessId);
                state.Commit(receipt); return receipt;
            },ct);
            if(result is IngressResult<WorkReceipt>.Handled handled)
            {
                if(request.LoseAck) await channel.BasicNackAsync(delivery.DeliveryTag,false,true,ct);
                else await channel.BasicAckAsync(delivery.DeliveryTag,false,ct);
                return Results.Json(new { status="handled",receipt=handled.Value,redelivered=delivery.Redelivered,providerCalls=model.Calls,scopeRestored=LlmCmdContext.Current is null });
            }
            await channel.BasicRejectAsync(delivery.DeliveryTag,false,ct);
            return Results.Json(new { status="rejected",reason=result.GetType().Name,detail=state.LastFailure,providerCalls=model.Calls });
        }
        finally { gate.Release(); }
    });
    app.MapPost("/internal",async (HttpRequest request,CancellationToken ct)=>
    {
        var headers=new HeaderValues(request.Headers.SelectMany(h=>h.Value.Select(v=>new HeaderValue(h.Key,v!))));
        if(adapter.Extract(headers,text) is not ParseResult.Parsed parsed) return Results.BadRequest();
        var result=await PropagationIngress.HandleAsync(parsed.Incoming,state,async (context,work,token)=>
        {
            var outcome=await extract.WithParameters(new("internal document")).ExecuteAsync(token);
            return outcome.Context;
        },ct);
        return result is IngressResult<CommandExecutionContext?>.Handled ok ? Results.Json(ok.Value) : Results.BadRequest();
    });
    app.MapPost("/egress-redirect",async (CancellationToken ct)=>
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,builder.Configuration["EDGE_URL"]+"/redirect");
        request.Headers.TryAddWithoutValidation(PropagationAdapter.ContextHeader,"must-not-leak");
        var old=Baggage.Current; Baggage.Current=Baggage.SetBaggage("llmcmd.output.draft_id","must-not-leak");
        try { using var response=await provider.SendAsync(request,ct); return Results.Json(new { status=(int)response.StatusCode }); }
        finally { Baggage.Current=old; }
    });
}
await app.RunAsync();

sealed record PublishRequest(OperationContextReference Context,Guid WorkId,JsonElement Payload,string? SlotId="header",string? RawEnvelope=null);
sealed record DrainRequest(bool LoseAck=false);
sealed record WireRequest(string Path,HeaderValue[] Headers);
sealed record WorkReceipt(Guid WorkId,string Fingerprint,CommandExecutionContext Context,Invoice Value,OutputRef Output,int ProcessId);
sealed class EventWriter(string path) : IExecutionEventSink
{
    public ConcurrentQueue<LlmEvent> Records { get; }=new();
    public async ValueTask EmitAsync(LlmEvent record,CancellationToken ct)
    {
        using var noAmbient=LlmCmdContext.Suppress();
        Records.Enqueue(record);
        await File.AppendAllTextAsync(path,JsonSerializer.Serialize(record)+"\n",ct);
    }
}
sealed class WorkerState(string directory) : IOperationContextResolver
{
    static readonly JsonSerializerOptions json=new(JsonSerializerDefaults.Web);
    public HumanLedger Ledger { get; }=new();
    public Actor Actor { get; }=new("synthetic-local","worker");
    public string? LastFailure { get; set; }
    readonly ConcurrentDictionary<Guid,OperationContextReference> registered=new();
    public OperationContextReference Register()
    {
        var reference=new OperationContextReference(Guid.NewGuid(),Ledger.CreateRun(Actor),new(Guid.NewGuid(),"document.extract",1,"definition-sha256",1,"input-revision-1"),"trial-1");
        registered[reference.ContextId]=reference; return reference;
    }
    public ValueTask<ContextResolution> ResolveAsync(UntrustedIncoming incoming,CancellationToken ct)
    {
        // Fixed synthetic actor and an exact in-memory registration are proof-only. No production grants are implemented.
        ContextResolution result=registered.TryGetValue(incoming.Reference.ContextId,out var known) && known==incoming.Reference
            && (incoming.Work?.SlotId is null or "header")
            ? new ContextResolution.Resolved(OperationContext.FromResolvedReference(known,incoming.Work?.WorkId))
            : new ContextResolution.Rejected("not_found");
        return ValueTask.FromResult(result);
    }
    string FileFor(Guid work,string suffix)=>Path.Combine(directory,work.ToString("N")+suffix);
    public (WorkReceipt? Receipt,bool Pending,bool Conflict) Find(Guid work,string fingerprint)
    {
        var receipt=FileFor(work,".receipt.json"); var pending=FileFor(work,".pending");
        if(File.Exists(receipt))
        {
            var value=JsonSerializer.Deserialize<WorkReceipt>(File.ReadAllText(receipt),json)!;
            return value.Fingerprint==fingerprint ? (value,false,false) : (null,false,true);
        }
        return File.Exists(pending) ? (null,true,File.ReadAllText(pending)!=fingerprint) : (null,false,false);
    }
    public void Start(Guid work,string fingerprint)
    {
        using var stream=new FileStream(FileFor(work,".pending"),FileMode.CreateNew,FileAccess.Write,FileShare.None);
        stream.Write(Encoding.UTF8.GetBytes(fingerprint)); stream.Flush(true);
    }
    public void Commit(WorkReceipt receipt)
    {
        var target=FileFor(receipt.WorkId,".receipt.json"); var temporary=target+".tmp";
        using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        { JsonSerializer.Serialize(stream,receipt,json); stream.Flush(true); }
        File.Move(temporary,target);
    }
}
sealed class HttpModel(HttpClient client,string edge,ActivitySource source) : IChatClient
{
    public int Calls;
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
    {
        Interlocked.Increment(ref Calls);
        using var request=new HttpRequestMessage(HttpMethod.Post,edge+"/provider") { Content=JsonContent.Create(new { synthetic=true }) };
        request.Headers.TryAddWithoutValidation(PropagationAdapter.ContextHeader,"must-not-leak");
        // Deliberately simulate private baggage from unrelated inbound instrumentation to test the boundary.
        using var incoming=source.StartActivity("synthetic.inbound.baggage");
        incoming?.AddBaggage("llmcmd.output.draft_id",LlmCmdContext.Current?.Reference.Draft?.DraftId.ToString());
        var old=Baggage.Current;
        Baggage.Current=Baggage.SetBaggage("llmcmd.output.draft_id",LlmCmdContext.Current?.Reference.Draft?.DraftId.ToString());
        Baggage.Current=Baggage.SetBaggage("public-test","ok");
        try
        {
            using var response=await client.SendAsync(request,cancellationToken); response.EnsureSuccessStatusCode();
            return new(new ChatMessage(ChatRole.Assistant,await response.Content.ReadAsStringAsync(cancellationToken)))
            { ModelId="synthetic-wire-model",ResponseId="synthetic-"+Guid.NewGuid(),Usage=new() { InputTokenCount=7,OutputTokenCount=3 } };
        }
        finally { Baggage.Current=old; }
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public object? GetService(Type type,object? key=null)=>null;
    public void Dispose() { }
}
