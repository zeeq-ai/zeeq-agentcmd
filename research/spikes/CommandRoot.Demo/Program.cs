using System.Text.Json;
using CommandDemo;
using CommandRoot;
using HumanGrading;
using LlmCmd.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

if(JsonSerializer.IsReflectionEnabledByDefault) throw new InvalidOperationException("Reflection serialization must stay disabled");
var actor=new Actor("demo","developer"); var ledger=new HumanLedger(); var client=new DemoClient();
using var runtime=CommandRuntime.Create(()=>client,new Dictionary<string,Profile> { ["extraction"]=new("synthetic-model") },ledger,()=>actor);
var services=new ServiceCollection().AddLlmCommandsFromCommandRoot_Demo(runtime);
using var provider=services.BuildServiceProvider();
var extract=provider.GetRequiredService<ExtractInvoice>();

var success=(Outcome<Invoice>.Success)await extract
    .WithParameters(new InvoiceParameters(Document: "Invoice for USD 12.50", Locale: "en"))
    .ExecuteAsync();
if(success.Value.Total!=12.5m || success.Value.Currency!="USD" || success.Value.Count!=3 || success.Delivery is not OutputDelivery.Stored)
    throw new InvalidOperationException("Typed execution failed");
var required=client.Schema.GetProperty("required").EnumerateArray().Select(e=>e.GetString()).ToArray();
if(!required.Contains("Total") || !required.Contains("Currency") || required.Contains("Count")) throw new InvalidOperationException("Schema disagrees with the record");
client.Response="{\"Total\":12.5}";
if(await extract.WithParameters(new("doc")).ExecuteAsync() is not Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidOutput }) throw new InvalidOperationException("Missing required field was accepted");
Console.WriteLine($"Typed result: {success.Value.Total} {success.Value.Currency}; Count={success.Value.Count}");
Console.WriteLine("Generated registration, schema, validation and storage passed; reflection serialization disabled.");

sealed class DemoClient : IChatClient
{
    public string Response="{\"Total\":12.5,\"Currency\":\"USD\"}";
    public JsonElement Schema;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
    {
        Schema=((ChatResponseFormatJson)options!.ResponseFormat!).Schema!.Value;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,Response)) { ModelId="synthetic-model",Usage=new() { InputTokenCount=7,OutputTokenCount=3 } });
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    public object? GetService(Type type,object? key=null)=>null;
    public void Dispose() { }
}
