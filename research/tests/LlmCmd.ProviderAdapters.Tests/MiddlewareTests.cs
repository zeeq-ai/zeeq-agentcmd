using System.Text.Json;
using Microsoft.Extensions.AI;
using ProviderAdapters;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LlmCmd.ProviderAdapters.Tests;

public class MiddlewareTests
{
    static IChatClient Build(ScriptedClient inner, ProviderRegistration reg, IExtractionSink sink, ProviderExtractionOptions? options = null, ProviderAdapterRegistry? registry = null) =>
        new ChatClientBuilder(inner).UseProviderExtraction(new ProviderExtractor(registry ?? Fx.Registry(), options), reg, sink).Build();

    static readonly ChatMessage[] Hello = [new(ChatRole.User, "hello")];

    [Test]
    public async Task Response_is_returned_unchanged_and_the_collector_sees_one_snapshot()
    {
        var (response, _, _) = Fx.Load("anthropic-cache-ttl");
        var inner = new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) };
        var sink = new CaptureSink();
        var got = await Build(inner, Fx.Anthropic, sink).GetResponseAsync(Hello);
        await Assert.That(ReferenceEquals(got, response)).IsTrue();
        await Assert.That(got.RawRepresentation is JsonElement).IsTrue();
        await Assert.That(sink.Results.Count).IsEqualTo(1);
        var s = sink.Results[0].Snapshot;
        await Assert.That(Fx.Status(sink.Results[0])).IsEqualTo("Extracted");
        await Assert.That(s.Usage.Input).IsEqualTo(110L); await Assert.That(s.Usage.ValueOf(Dim.CacheWrite5m)).IsEqualTo(12L);
        await Assert.That(s.IsStreaming).IsFalse(); await Assert.That(s.End).IsEqualTo(AttemptEnd.Completed);
        await Assert.That(s.Registration.ClientRegistrationId).IsEqualTo("anthropic-main");
        await Assert.That(s.AdapterId).IsEqualTo("anthropic.messages");
        await Assert.That(s.MeaiVersion.StartsWith("10.11.0")).IsTrue();
        await Assert.That(s.AttemptId.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task A_failing_sink_never_loses_the_response()
    {
        var (response, _, _) = Fx.Load("openai-chat-basic");
        var failures = new List<string>();
        var client = Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, new ThrowingSink(), new ProviderExtractionOptions { OnFailure = (stage, _) => failures.Add(stage) });
        var got = await client.GetResponseAsync(Hello);
        await Assert.That(ReferenceEquals(got, response)).IsTrue();
        await Assert.That(failures.ToArray()).IsEquivalentTo(new[] { "sink" });
    }

    [Test]
    public async Task A_failing_adapter_never_loses_the_response_and_the_snapshot_keeps_the_baseline()
    {
        var (response, _, _) = Fx.Load("openai-chat-basic");
        var sink = new CaptureSink();
        var client = Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, sink, registry: new ProviderAdapterRegistry([new ThrowingAdapter()]));
        var got = await client.GetResponseAsync(Hello);
        await Assert.That(ReferenceEquals(got, response)).IsTrue();
        await Assert.That(Fx.Status(sink.Results.Single())).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(sink.Results.Single()).Contains("adapter-failed")).IsTrue();
        await Assert.That(sink.Results.Single().Snapshot.Usage.Input).IsEqualTo(100L);
    }

    [Test]
    public async Task Raw_content_is_off_by_default_and_never_serialized()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("SECRET-ANSWER"), new TextReasoningContent("SECRET-THOUGHT"), new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["q"] = "SECRET-ARG" })]))
        { Usage = new() { InputTokenCount = 1, OutputTokenCount = 2, TotalTokenCount = 3 }, RawRepresentation = Fx.Json("""{"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""") };
        var sink = new CaptureSink();
        await Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, sink).GetResponseAsync(Hello);
        var s = sink.Results.Single().Snapshot;
        await Assert.That(s.RawContent).IsNull();
        await Assert.That(s.Content).IsEqualTo(new ContentShape(1, 1, 1, 0));
        var json = JsonSerializer.Serialize(s);
        await Assert.That(json.Contains("SECRET")).IsFalse();
        await Assert.That(json.Contains("synthetic")).IsFalse();
    }

    [Test]
    public async Task Raw_content_opt_in_is_bounded_and_never_includes_tool_arguments()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new TextContent("0123456789"), new TextReasoningContent("abc"), new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["q"] = "SECRET-ARG" })]))
        { Usage = new() { InputTokenCount = 1 } };
        var sink = new CaptureSink();
        var options = new ProviderExtractionOptions { RawContent = new RawContentPolicy(true, 4) };
        await Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, sink, options).GetResponseAsync(Hello);
        var raw = sink.Results.Single().Snapshot.RawContent!;
        await Assert.That(raw.Count).IsEqualTo(3);
        await Assert.That(raw[0]).IsEqualTo(new CapturedContent("text", "0123", true));
        await Assert.That(raw[1]).IsEqualTo(new CapturedContent("reasoning", "abc", false));
        await Assert.That(raw[2].Kind).IsEqualTo("tool_call");
        await Assert.That(JsonSerializer.Serialize(sink.Results.Single().Snapshot).Contains("SECRET-ARG")).IsFalse();
    }

    [Test]
    public async Task Failed_and_caller_cancelled_calls_publish_one_attempt_each_with_unknown_usage_and_rethrow()
    {
        var sink = new CaptureSink();
        var failing = Build(new ScriptedClient { Respond = (_, _, _) => throw new HttpRequestException("boom") }, Fx.OpenAi, sink);
        await Assert.That(async () => await failing.GetResponseAsync(Hello)).Throws<HttpRequestException>();
        var cancelling = Build(new ScriptedClient { Respond = (_, _, _) => throw new OperationCanceledException() }, Fx.OpenAi, sink);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.That(async () => await cancelling.GetResponseAsync(Hello, null, cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(sink.Results.Select(r => r.Snapshot.End).ToArray()).IsEquivalentTo(new[] { AttemptEnd.Failed, AttemptEnd.Cancelled });
        await Assert.That(sink.Results.All(r => r.Snapshot.Usage.Input is null)).IsTrue();
    }

    [Test]
    public async Task Cancellation_exception_from_a_live_token_such_as_an_sdk_timeout_is_a_failure()
    {
        var sink = new CaptureSink();
        var client = Build(new ScriptedClient { Respond = (_, _, _) => throw new TaskCanceledException("http timeout") }, Fx.OpenAi, sink);
        await Assert.That(async () => await client.GetResponseAsync(Hello)).Throws<TaskCanceledException>();
        await Assert.That(sink.Results.Single().Snapshot.End).IsEqualTo(AttemptEnd.Failed);
    }

    [Test]
    public async Task A_throwing_failure_observer_cannot_change_the_response_for_sink_or_adapter_defects()
    {
        var (response, _, _) = Fx.Load("openai-chat-basic");
        var observed = 0;
        var options = new ProviderExtractionOptions { OnFailure = (_, _) => { observed++; throw new InvalidOperationException("observer defect"); } };
        var sinkDown = Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, new ThrowingSink(), options);
        await Assert.That(ReferenceEquals(await sinkDown.GetResponseAsync(Hello), response)).IsTrue();
        var capture = new CaptureSink();
        var adapterDown = Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, capture, options, new ProviderAdapterRegistry([new ThrowingAdapter()]));
        await Assert.That(ReferenceEquals(await adapterDown.GetResponseAsync(Hello), response)).IsTrue();
        await Assert.That(observed).IsEqualTo(2);
        await Assert.That(Fx.Codes(capture.Results.Single()).Contains("adapter-failed")).IsTrue();
        var both = Build(new ScriptedClient { Respond = (_, _, _) => Task.FromResult(response) }, Fx.OpenAi, new ThrowingSink(), options, new ProviderAdapterRegistry([new ThrowingAdapter()]));
        await Assert.That(ReferenceEquals(await both.GetResponseAsync(Hello), response)).IsTrue();
        await Assert.That(observed).IsEqualTo(4);
    }

    [Test]
    public async Task Concurrent_attempts_have_isolated_state_and_distinct_ids()
    {
        var sink = new CaptureSink();
        var inner = new ScriptedClient
        {
            Respond = async (messages, _, _) =>
            {
                var n = int.Parse(messages.Last().Text); await Task.Delay(n == 1 ? 30 : 1);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")) { Usage = new() { InputTokenCount = n * 100, OutputTokenCount = n, TotalTokenCount = n * 101 } };
            }
        };
        var client = Build(inner, Fx.OpenAi, sink);
        await Task.WhenAll(client.GetResponseAsync([new ChatMessage(ChatRole.User, "1")]), client.GetResponseAsync([new ChatMessage(ChatRole.User, "2")]));
        await Assert.That(sink.Results.Select(r => r.Snapshot.AttemptId).Distinct().Count()).IsEqualTo(2);
        await Assert.That(sink.Results.Select(r => r.Snapshot.Usage.Input!.Value).Order().ToArray()).IsEquivalentTo(new long[] { 100, 200 });
        await Assert.That(sink.Results.All(r => r.Snapshot.Usage.Total == r.Snapshot.Usage.Input + r.Snapshot.Usage.Output)).IsTrue();
    }

    [Test]
    public async Task Tool_loop_counts_each_provider_attempt_once_and_not_the_outer_aggregate()
    {
        var calls = 0;
        var inner = new ScriptedClient
        {
            Respond = (_, _, _) =>
            {
                var first = Interlocked.Increment(ref calls) == 1;
                var message = first ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "Echo", new Dictionary<string, object?>())]) : new ChatMessage(ChatRole.Assistant, "done");
                return Task.FromResult(new ChatResponse(message)
                {
                    FinishReason = first ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
                    Usage = first ? new() { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 } : new() { InputTokenCount = 20, OutputTokenCount = 8, TotalTokenCount = 28 },
                });
            }
        };
        var sink = new CaptureSink();
        var client = new ChatClientBuilder(inner).UseFunctionInvocation().UseProviderExtraction(Fx.Extractor(), Fx.OpenAi, sink).Build();
        var response = await client.GetResponseAsync(Hello, new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "Echo")] });
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(sink.Results.Count).IsEqualTo(2);
        await Assert.That(sink.Results.Select(r => r.Snapshot.Usage.Input!.Value).ToArray()).IsEquivalentTo(new long[] { 10, 20 });
        await Assert.That(sink.Results.Select(r => r.Snapshot.FinishReason!).ToArray()).IsEquivalentTo(new[] { "tool_calls", "stop" });
        await Assert.That(sink.Results.Select(r => r.Snapshot.AttemptId).Distinct().Count()).IsEqualTo(2);
        await Assert.That(sink.Results.Sum(r => r.Snapshot.Usage.Total!.Value)).IsEqualTo(43L);
        await Assert.That(response.Usage?.TotalTokenCount).IsEqualTo(43L); // outer aggregate equals the children; only the children are recorded
    }
}

public class StreamingTests
{
    static readonly ChatMessage[] Hello = [new(ChatRole.User, "hello")];
    static ChatResponseUpdate Text(string t) => new(ChatRole.Assistant, t) { ResponseId = "r1", ModelId = "synthetic-model" };
    static ChatResponseUpdate UsageOnly(long input, long output, long total) =>
        new() { Role = ChatRole.Assistant, Contents = [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = total })] };

    static IChatClient Build(ScriptedClient inner, ProviderRegistration reg, IExtractionSink sink, ProviderAdapterRegistry? registry = null, ProviderExtractionOptions? options = null) =>
        new ChatClientBuilder(inner).UseProviderExtraction(new ProviderExtractor(registry ?? Fx.Registry(), options), reg, sink).Build();

    static async Task<List<ChatResponseUpdate>> Drain(IChatClient client, CancellationToken ct = default)
    {
        var seen = new List<ChatResponseUpdate>();
        await foreach (var u in client.GetStreamingResponseAsync(Hello, null, ct)) seen.Add(u);
        return seen;
    }

    [Test]
    public async Task OpenAi_layout_with_a_final_usage_only_update_is_captured_and_updates_pass_through()
    {
        var updates = new[] { Text("he"), Text("llo"), new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop }, UsageOnly(100, 40, 140) };
        var sink = new CaptureSink();
        var seen = await Drain(Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates(updates) }, Fx.OpenAi, sink));
        await Assert.That(seen.Count).IsEqualTo(4);
        await Assert.That(seen.Zip(updates).All(p => ReferenceEquals(p.First, p.Second))).IsTrue();
        var r = sink.Results.Single(); var s = r.Snapshot;
        await Assert.That(s.IsStreaming).IsTrue(); await Assert.That(s.End).IsEqualTo(AttemptEnd.Completed);
        await Assert.That(s.Usage.Input).IsEqualTo(100L); await Assert.That(s.Usage.Total).IsEqualTo(140L);
        await Assert.That(s.ResponseId).IsEqualTo("r1"); await Assert.That(s.ModelId).IsEqualTo("synthetic-model"); await Assert.That(s.FinishReason).IsEqualTo("stop");
        await Assert.That(s.Content.TextItems).IsEqualTo(2);
        // Honest coverage: streaming reads normalized UsageContent only, so a stream is never reported as fully extracted.
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(r).Contains("stream-native-not-extracted")).IsTrue();
    }

    [Test]
    public async Task DeepSeek_layout_with_usage_on_the_last_choice_bearing_update_is_captured()
    {
        var last = Text("end"); last.Contents.Add(new UsageContent(new UsageDetails { InputTokenCount = 100, OutputTokenCount = 40, TotalTokenCount = 140, CachedInputTokenCount = 60 }));
        var sink = new CaptureSink();
        await Drain(Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([Text("a"), last]) }, Fx.DeepSeek, sink));
        var s = sink.Results.Single().Snapshot;
        await Assert.That(s.Usage.Input).IsEqualTo(100L); await Assert.That(s.Usage.ValueOf(Dim.Cached)).IsEqualTo(60L);
        await Assert.That(s.AdapterId).IsEqualTo("deepseek.chat-completions");
        await Assert.That(s.Content.TextItems).IsEqualTo(2);
    }

    [Test]
    public async Task Cumulative_frames_replace_under_a_declared_rule_and_are_never_summed()
    {
        var sink = new CaptureSink();
        await Drain(Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([UsageOnly(10, 5, 15), UsageOnly(10, 9, 19)]) }, Fx.Anthropic, sink));
        var r = sink.Results.Single();
        await Assert.That(r.Snapshot.Usage.Output).IsEqualTo(9L); // not 14
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(10L);
        await Assert.That(r.Snapshot.Diagnostics.Single(d => d.Code == "stream-usage-replaced").Severity).IsEqualTo(Severity.Info);
    }

    [Test]
    public async Task Undeclared_semantics_keep_the_latest_frame_and_flag_ambiguity()
    {
        var sink = new CaptureSink();
        var reg = new ProviderRegistration("acme-gateway", "acme-1", ApiKind.ChatCompletions);
        await Drain(Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([UsageOnly(10, 5, 15), UsageOnly(10, 9, 19)]) }, reg, sink));
        var r = sink.Results.Single();
        await Assert.That(r.Snapshot.Usage.Output).IsEqualTo(9L);
        await Assert.That(Fx.Codes(r).Contains("stream-usage-frames-ambiguous")).IsTrue();
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
    }

    [Test]
    public async Task Early_dispose_finalizes_once_as_abandoned_without_draining_the_stream()
    {
        var sink = new CaptureSink(); var pulled = 0;
        async IAsyncEnumerable<ChatResponseUpdate> Source()
        {
            foreach (var u in new[] { Text("a"), Text("b"), UsageOnly(100, 40, 140) }) { pulled++; await Task.Yield(); yield return u; }
        }
        var client = Build(new ScriptedClient { Stream = _ => Source() }, Fx.OpenAi, sink);
        await foreach (var _ in client.GetStreamingResponseAsync(Hello)) break;
        var r = sink.Results.Single();
        await Assert.That(pulled).IsEqualTo(1);
        await Assert.That(r.Snapshot.End).IsEqualTo(AttemptEnd.Abandoned);
        await Assert.That(r.Snapshot.Usage.Input).IsNull();
        await Assert.That(Fx.Codes(r).Contains("attempt-abandoned")).IsTrue();
    }

    [Test]
    public async Task Mid_stream_failure_rethrows_and_keeps_observed_partial_usage()
    {
        var sink = new CaptureSink();
        var client = Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([UsageOnly(10, 5, 15), Text("a")], new IOException("reset")) }, Fx.OpenAi, sink);
        await Assert.That(async () => await Drain(client)).Throws<IOException>();
        var r = sink.Results.Single();
        await Assert.That(r.Snapshot.End).IsEqualTo(AttemptEnd.Failed);
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(10L);
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
    }

    [Test]
    public async Task Caller_cancellation_is_cancelled_but_a_live_token_exception_is_failed()
    {
        var sink = new CaptureSink();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var cancelled = Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([Text("a")], new OperationCanceledException()) }, Fx.OpenAi, sink);
        await Assert.That(async () => await Drain(cancelled, cts.Token)).Throws<OperationCanceledException>();
        var timeout = Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([Text("a")], new TaskCanceledException("sdk timeout")) }, Fx.OpenAi, sink);
        await Assert.That(async () => await Drain(timeout)).Throws<TaskCanceledException>();
        await Assert.That(sink.Results.Select(r => r.Snapshot.End).ToArray()).IsEquivalentTo(new[] { AttemptEnd.Cancelled, AttemptEnd.Failed });
    }

    [Test]
    public async Task Dispose_failure_still_publishes_once_as_failed_and_keeps_the_provider_exception()
    {
        var sink = new CaptureSink();
        var client = Build(new ScriptedClient { Stream = _ => new DisposeThrowsStream([Text("a"), UsageOnly(10, 5, 15)]) }, Fx.OpenAi, sink);
        await Assert.That(async () => await Drain(client)).Throws<InvalidOperationException>();
        var r = sink.Results.Single();
        await Assert.That(r.Snapshot.End).IsEqualTo(AttemptEnd.Failed);
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(10L);
        // Early dispose that also fails to dispose the provider stream.
        var early = Build(new ScriptedClient { Stream = _ => new DisposeThrowsStream([Text("a"), Text("b")]) }, Fx.OpenAi, sink);
        await Assert.That(async () => { await foreach (var _ in early.GetStreamingResponseAsync(Hello)) break; }).Throws<InvalidOperationException>();
        await Assert.That(sink.Results.Count).IsEqualTo(2);
        await Assert.That(sink.Results[1].Snapshot.End).IsEqualTo(AttemptEnd.Failed);
    }

    [Test]
    public async Task Raw_content_policy_comes_from_the_extractor_options_for_streamed_and_complete_attempts()
    {
        var options = new ProviderExtractionOptions { RawContent = new RawContentPolicy(true, 3) };
        var sink = new CaptureSink();
        await Drain(Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([Text("abcdef"), UsageOnly(1, 2, 3)]) }, Fx.OpenAi, sink, options: options));
        await Assert.That(sink.Results.Single().Snapshot.RawContent!.Single()).IsEqualTo(new CapturedContent("text", "abc", true));
        var off = new CaptureSink();
        await Drain(Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([Text("abcdef")]) }, Fx.OpenAi, off));
        await Assert.That(off.Results.Single().Snapshot.RawContent).IsNull();
    }

    [Test]
    public async Task Failing_sink_does_not_break_the_stream()
    {
        var failures = new List<string>();
        var client = Build(new ScriptedClient { Stream = _ => ScriptedClient.Updates([Text("a"), UsageOnly(1, 2, 3)]) }, Fx.OpenAi, new ThrowingSink(), options: new ProviderExtractionOptions { OnFailure = (s, _) => failures.Add(s) });
        var seen = await Drain(client);
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(failures.ToArray()).IsEquivalentTo(new[] { "sink" });
    }
}

sealed class DisposeThrowsStream(IEnumerable<ChatResponseUpdate> updates) : IAsyncEnumerable<ChatResponseUpdate>
{
    public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(updates.GetEnumerator());
    sealed class Enumerator(IEnumerator<ChatResponseUpdate> inner) : IAsyncEnumerator<ChatResponseUpdate>
    {
        public ChatResponseUpdate Current => inner.Current;
        public ValueTask<bool> MoveNextAsync() => new(inner.MoveNext());
        public ValueTask DisposeAsync() => throw new InvalidOperationException("provider dispose failed");
    }
}
