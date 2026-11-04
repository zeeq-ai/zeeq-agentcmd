using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.OpenTelemetry;
using ExtractInvoice = CommandRoot.ExtractInvoice;
using Invoice = CommandRoot.Invoice;
using Profiles = CommandRoot.Profiles;

namespace AgentCmd.ContextPropagation.Tests;

[NotInParallel]
public class PropagationTests
{
    static readonly TextCarrierCodec Text = new();
    static readonly PropagationAdapter Adapter = new();
    static readonly JsonElement Payload = JsonSerializer.SerializeToElement(
        new { document = "synthetic" }
    );

    static OperationContext Context(Guid? run = null) =>
        OperationContext.FromResolvedReference(
            new(
                Guid.NewGuid(),
                run ?? Guid.NewGuid(),
                new(
                    Guid.NewGuid(),
                    "document.extract",
                    1,
                    "definition-sha256",
                    1,
                    "input-revision-1"
                ),
                "trial-1"
            )
        );

    static HeaderValues Headers(OperationContext context) =>
        (
            (InjectionResult<HeaderValues>.Injected)
                Adapter.Inject(
                    context,
                    new(
                        ActivityTraceId.CreateRandom(),
                        ActivitySpanId.CreateRandom(),
                        ActivityTraceFlags.Recorded
                    ),
                    HeaderValues.Empty,
                    Text,
                    true
                )
        ).Carrier;

    static ParseResult Parse(
        ReadOnlyMemory<byte> work,
        HeaderValues? headers = null,
        IngressPolicy policy = IngressPolicy.Component
    ) => Adapter.Extract(headers ?? HeaderValues.Empty, Text, work, policy);

    [Test]
    public async Task Private_header_round_trips_context_and_remote_trace()
    {
        var context = Context();
        var result = (ParseResult.Parsed)Adapter.Extract(Headers(context), Text);
        await Assert.That(result.Incoming.Reference).IsEqualTo(context.Reference);
        await Assert.That(result.Incoming.Producer.IsRemote).IsTrue();
        await Assert.That(result.Incoming.Producer.TraceId == default).IsFalse();
        await Assert.That(AgentCmdContext.Current).IsNull();
    }

    [Test]
    public async Task Work_envelope_pins_identity_and_keeps_owned_payload_after_parse()
    {
        var context = Context();
        var id = Guid.NewGuid();
        var raw = Adapter.EncodeWork(context, id, "header", Payload);
        var a = ((ParseResult.Parsed)Parse(raw)).Incoming;
        var b = ((ParseResult.Parsed)Parse(raw)).Incoming;
        await Assert.That(a.Work!.WorkId).IsEqualTo(id);
        await Assert.That(a.Work.SlotId).IsEqualTo("header");
        await Assert
            .That(a.Work.Payload.GetProperty("document").GetString())
            .IsEqualTo("synthetic");
        await Assert.That(a.Fingerprint).IsEqualTo(b.Fingerprint);
        var changed = Adapter.EncodeWork(
            context,
            id,
            "header",
            JsonSerializer.SerializeToElement(new { document = "changed" })
        );
        await Assert
            .That(((ParseResult.Parsed)Parse(changed)).Incoming.Fingerprint)
            .IsNotEqualTo(a.Fingerprint);
    }

    [Test]
    public async Task Required_work_never_comes_from_baggage_or_private_header()
    {
        var headers = new HeaderValues(
            Headers(Context()).Entries.Append(new("baggage", "agentcmd.output.draft_id=pretend"))
        );
        await Assert
            .That(
                Adapter.Extract(headers, Text, policy: IngressPolicy.Component)
                    is ParseResult.Malformed { Code: "work_envelope_required" }
            )
            .IsTrue();
        await Assert
            .That(
                Adapter.Extract(
                    new HeaderValues([new("baggage", "agentcmd.output.draft_id=pretend")]),
                    Text
                ) is ParseResult.Absent
            )
            .IsTrue();
    }

    [Test]
    public async Task Optional_conflicting_malformed_or_duplicate_context_cannot_veto_valid_work()
    {
        var work = Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload);
        foreach (
            var headers in new[]
            {
                Headers(Context()),
                new HeaderValues([new(PropagationAdapter.ContextHeader, "not base64")]),
                new HeaderValues([
                    new(PropagationAdapter.ContextHeader, "a"),
                    new("AGENTCMD-CONTEXT", "b"),
                ]),
            }
        )
        {
            var parsed = (ParseResult.Parsed)Parse(work, headers);
            await Assert
                .That(parsed.Incoming.Diagnostics.Contains("optional_context_ignored"))
                .IsTrue();
        }
    }

    [Test]
    public async Task Baggage_duplicates_are_diagnostic_only_and_never_install_ambient_values()
    {
        var before = OpenTelemetry.Baggage.Current;
        var headers = new HeaderValues([
            new("baggage", "agentcmd.output.draft_id=first"),
            new("baggage", "agentcmd.output.draft_id=second"),
        ]);
        var result = (ParseResult.Parsed)Parse(
            Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload),
            headers
        );
        await Assert.That(result.Incoming.Diagnostics.Contains("baggage_ignored")).IsTrue();
        await Assert.That(OpenTelemetry.Baggage.Current).IsEqualTo(before);
    }

    [Test]
    public async Task Authoritative_duplicate_json_fields_are_rejected_recursively()
    {
        var original = Encoding.UTF8.GetString(
            Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload)
        );
        foreach (
            var raw in new[]
            {
                original.Replace("\"version\":1", "\"version\":1,\"version\":1"),
                original.Replace("\"generation\":1", "\"generation\":1,\"generation\":2"),
                original.Replace(
                    "\"document\":\"synthetic\"",
                    "\"document\":\"synthetic\",\"document\":\"other\""
                ),
            }
        )
        {
            await Assert
                .That(
                    Parse(Encoding.UTF8.GetBytes(raw))
                        is ParseResult.Malformed { Code: "duplicate_envelope_field" }
                )
                .IsTrue();
        }
    }

    [Test]
    public async Task Duplicate_private_headers_reject_auxiliary_context_instead_of_last_wins()
    {
        var headers = new HeaderValues(
            Headers(Context())
                .Entries.Concat(
                    Headers(Context())
                        .Entries.Where(e => e.Name == PropagationAdapter.ContextHeader)
                )
        );
        await Assert
            .That(
                Adapter.Extract(headers, Text)
                    is ParseResult.Malformed { Code: "duplicate_context_header" }
            )
            .IsTrue();
    }

    [Test]
    public async Task Unsupported_major_and_unknown_additive_fields_are_distinct()
    {
        var raw = Encoding.UTF8.GetString(
            Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload)
        );
        await Assert
            .That(
                Parse(Encoding.UTF8.GetBytes(raw.Replace("\"version\":1", "\"version\":2")))
                    is ParseResult.UnsupportedVersion { Version: 2 }
            )
            .IsTrue();
        await Assert
            .That(
                Parse(Encoding.UTF8.GetBytes(raw.Insert(1, "\"futureMinorField\":true,")))
                    is ParseResult.Parsed
            )
            .IsTrue();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("null")]
    [Arguments("{\"version\":1}")]
    [Arguments("{\"version\":\"1\"}")]
    [Arguments("{\"version\":1,\"context\":null}")]
    public async Task Invalid_envelope_shapes_return_closed_rejection(string raw) =>
        await Assert.That(Parse(Encoding.UTF8.GetBytes(raw)) is ParseResult.Malformed).IsTrue();

    [Test]
    public async Task Every_input_limit_has_a_closed_result()
    {
        var work = Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload);
        await Assert
            .That(
                new PropagationAdapter(new(MaxEnvelopeBytes: 8)).Extract(
                    HeaderValues.Empty,
                    Text,
                    work
                )
                    is ParseResult.LimitExceeded { Limit: "envelope_bytes" }
            )
            .IsTrue();
        await Assert
            .That(
                new PropagationAdapter(new(MaxEntries: 1)).Extract(
                    new HeaderValues([new("a", "1"), new("b", "2")]),
                    Text
                )
                    is ParseResult.LimitExceeded { Limit: "header_count" }
            )
            .IsTrue();
        await Assert
            .That(
                new PropagationAdapter(new(MaxValueBytes: 1)).Extract(
                    new HeaderValues([new("a", "é")]),
                    Text
                )
                    is ParseResult.LimitExceeded { Limit: "header_value_bytes" }
            )
            .IsTrue();
        await Assert
            .That(
                new PropagationAdapter(new(MaxBytes: 2)).Extract(
                    new HeaderValues([new("ab", "c")]),
                    Text
                )
                    is ParseResult.LimitExceeded { Limit: "header_bytes" }
            )
            .IsTrue();
    }

    [Test]
    public async Task Binary_codec_copies_values_and_rejects_unsupported_types_and_bad_utf8()
    {
        var codec = new BinaryCarrierCodec();
        var context = Context();
        IReadOnlyList<KeyValuePair<string, object>> carrier = [];
        var wire = (
            (InjectionResult<IReadOnlyList<KeyValuePair<string, object>>>.Injected)
                Adapter.Inject(context, default, carrier, codec, true)
        ).Carrier;
        await Assert
            .That(((ParseResult.Parsed)Adapter.Extract(wire, codec)).Incoming.Reference)
            .IsEqualTo(context.Reference);
        foreach (object value in new object[] { new byte[] { 0xC3, 0x28 }, 42, new object() })
        {
            await Assert
                .That(
                    Adapter.Extract(
                        (IReadOnlyList<KeyValuePair<string, object>>)
                            new[] { new KeyValuePair<string, object>("x", value) },
                        codec
                    ) is ParseResult.Malformed
                )
                .IsTrue();
        }
    }

    [Test]
    public async Task Injection_is_internal_only_preserves_foreign_headers_and_does_not_mutate_input()
    {
        var original = new HeaderValues([
            new("foreign", "kept"),
            new(PropagationAdapter.ContextHeader, "old"),
        ]);
        await Assert
            .That(
                Adapter.Inject(Context(), default, original, Text, false)
                    is InjectionResult<HeaderValues>.Rejected
            )
            .IsTrue();
        var injected = (
            (InjectionResult<HeaderValues>.Injected)
                Adapter.Inject(Context(), default, original, Text, true)
        ).Carrier;
        await Assert.That(injected.GetAll("foreign").Single()).IsEqualTo("kept");
        await Assert.That(injected.GetAll(PropagationAdapter.ContextHeader).Count()).IsEqualTo(1);
        await Assert
            .That(original.GetAll(PropagationAdapter.ContextHeader).Single())
            .IsEqualTo("old");
    }

    [Test]
    public async Task Nested_suppressed_and_out_of_order_scopes_restore_without_cross_flow_revocation()
    {
        var a = Context();
        var b = Context();
        var outer = AgentCmdContext.BeginScope(a);
        var inner = AgentCmdContext.BeginScope(b);
        outer.Dispose();
        await Assert.That(AgentCmdContext.Current).IsEqualTo(b);
        inner.Dispose();
        await Assert.That(AgentCmdContext.Current).IsEqualTo(a);
        using (AgentCmdContext.Suppress())
        {
            await Assert.That(AgentCmdContext.Current).IsNull();
        }

        outer.Dispose();
        outer.Dispose();
        await Assert.That(AgentCmdContext.Current).IsNull();
    }

    [Test]
    public async Task Siblings_isolate_nested_scopes_and_parent_restores_after_await()
    {
        var a = Context();
        using var scope = AgentCmdContext.BeginScope(a);
        var values = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(async _ =>
                {
                    await Task.Yield();
                    var child = Context();
                    using (AgentCmdContext.BeginScope(child))
                    {
                        await Task.Yield();
                        if (AgentCmdContext.Current != child)
                        {
                            throw new Exception("Sibling context leaked");
                        }
                    }
                    return AgentCmdContext.Current;
                })
        );
        await Assert.That(values.All(v => v == a)).IsTrue();
        await Assert.That(AgentCmdContext.Current).IsEqualTo(a);
    }

    [Test]
    public async Task Async_helper_cannot_set_its_callers_scope()
    {
        async Task<IDisposable> Incorrect()
        {
            await Task.Yield();
            return AgentCmdContext.BeginScope(Context());
        }
        using var wrong = await Incorrect();
        await Assert.That(AgentCmdContext.Current).IsNull();
    }

    [Test]
    public async Task Detached_child_retains_immutable_context_after_parent_disposal()
    {
        var a = Context();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<OperationContext?> child;
        using (AgentCmdContext.BeginScope(a))
        {
            child = Task.Run(async () =>
            {
                await release.Task;
                return AgentCmdContext.Current;
            });
        }

        release.SetResult();
        await Assert.That(await child).IsEqualTo(a);
        await Assert.That(AgentCmdContext.Current).IsNull();
    }

    [Test]
    public async Task Suppressed_execution_flow_does_not_inherit_context()
    {
        using var scope = AgentCmdContext.BeginScope(Context());
        Task<CapturedContext> child;
        using (System.Threading.ExecutionContext.SuppressFlow())
        {
            child = Task.Run(() => AgentCmdContext.Capture());
        }

        await Assert.That((await child).Reference).IsNull();
        await Assert.That(AgentCmdContext.Current is not null).IsTrue();
    }

    sealed class Resolver(Func<UntrustedIncoming, ContextResolution> resolve)
        : IOperationContextResolver
    {
        public ValueTask<ContextResolution> ResolveAsync(
            UntrustedIncoming incoming,
            CancellationToken ct
        ) => ValueTask.FromResult(resolve(incoming));
    }

    [Test]
    public async Task Ingress_resolves_then_scopes_the_handler_and_restores_on_exception_or_cancellation()
    {
        var source = Context();
        var incoming = (
            (ParseResult.Parsed)Parse(Adapter.EncodeWork(source, Guid.NewGuid(), "header", Payload))
        ).Incoming;
        var resolver = new Resolver(i => new ContextResolution.Resolved(
            OperationContext.FromResolvedReference(i.Reference, i.Work!.WorkId)
        ));
        var outer = Context();
        using var parent = AgentCmdContext.BeginScope(outer);
        var ok = await PropagationIngress.HandleAsync(
            incoming,
            resolver,
            async (ctx, work, ct) =>
            {
                await Task.Yield();
                return AgentCmdContext.Current == ctx && ctx.WorkId == work!.WorkId;
            }
        );
        await Assert.That(ok is IngressResult<bool>.Handled { Value: true }).IsTrue();
        var fail = await PropagationIngress.HandleAsync<int>(
            incoming,
            resolver,
            (ctx, work, ct) => throw new InvalidOperationException()
        );
        await Assert.That(fail is IngressResult<int>.Failed).IsTrue();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancel = await PropagationIngress.HandleAsync<int>(
            incoming,
            resolver,
            (ctx, work, ct) => ValueTask.FromCanceled<int>(ct),
            cancelled.Token
        );
        await Assert.That(cancel is IngressResult<int>.Cancelled).IsTrue();
        await Assert.That(AgentCmdContext.Current).IsEqualTo(outer);
    }

    [Test]
    public async Task Resolver_rejection_unavailable_and_mismatched_identity_never_run_handler()
    {
        var incoming = (
            (ParseResult.Parsed)Parse(
                Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload)
            )
        ).Incoming;
        int calls = 0;
        foreach (
            var result in new ContextResolution[]
            {
                new ContextResolution.Rejected("not_found"),
                new ContextResolution.Unavailable(),
                new ContextResolution.Resolved(Context()),
            }
        )
        {
            var handled = await PropagationIngress.HandleAsync(
                incoming,
                new Resolver(_ => result),
                (c, w, t) =>
                {
                    calls++;
                    return ValueTask.FromResult(true);
                }
            );
            await Assert.That(handled is not IngressResult<bool>.Handled).IsTrue();
        }
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task Invalid_optional_trace_does_not_reject_authoritative_work()
    {
        var raw = Adapter.EncodeWork(Context(), Guid.NewGuid(), "header", Payload);
        var parsed = (ParseResult.Parsed)Parse(
            raw,
            new HeaderValues([new("traceparent", "bad-trace")])
        );
        await Assert.That(parsed.Incoming.Producer.TraceId == default).IsTrue();
    }

    [Test]
    public async Task Ordinary_foreign_HTTP_token_names_are_not_rejected()
    {
        var headers = new HeaderValues(
            Headers(Context()).Entries.Append(new("x.foreign_header", "kept"))
        );
        await Assert.That(Adapter.Extract(headers, Text) is ParseResult.Parsed).IsTrue();
    }

    sealed class Events : IExecutionEventSink
    {
        public ConcurrentQueue<LlmEvent> Records { get; } = new();

        public async ValueTask EmitAsync(LlmEvent record, CancellationToken ct)
        {
            await Task.Yield();
            using var suppressed = AgentCmdContext.Suppress();
            Records.Enqueue(record);
        }
    }

    sealed class Client(Func<Task>? wait = null) : IChatClient
    {
        public int Calls;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            if (wait is not null)
            {
                await wait();
            }

            return new(new ChatMessage(ChatRole.Assistant, "{\"Total\":12,\"Currency\":\"USD\"}"))
            {
                Usage = new() { InputTokenCount = 7, OutputTokenCount = 3 },
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type t, object? key = null) => null;

        public void Dispose() { }
    }

    sealed class Fixture : IDisposable
    {
        public InMemoryFeedbackLedger Ledger { get; } = new();
        public Actor Actor { get; } = new("synthetic", "worker");
        public Events Events { get; } = new();
        public ConcurrentQueue<ExecutionOutcomeRecord> Records { get; } = new();
        public CommandRuntime Runtime { get; }
        public Client Client { get; }
        public ExtractInvoice Command { get; }
        public OperationContext Context { get; }

        public Fixture(bool failActor = false, Func<Task>? wait = null)
        {
            Context = PropagationTests.Context(Ledger.CreateRun(Actor));
            Client = new(wait);
            Runtime = CommandRuntime.Create(
                () => Client,
                new Dictionary<string, Profile> { ["extraction"] = new("synthetic-model") },
                Ledger,
                () => failActor ? throw new InvalidOperationException() : Actor,
                sink: Records.Enqueue,
                events: Events
            );
            Command = new(Runtime);
        }

        public void Dispose() => Runtime.Dispose();
    }

    [Test]
    public async Task Terminal_captures_late_ambient_once_and_all_emitters_keep_it_after_scope_disposal()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var f = new Fixture(wait: () => release.Task);
        var built = f.Command.WithParameters(new("doc"));
        Task<Outcome<Invoice>> pending;
        using (AgentCmdContext.BeginScope(f.Context))
        {
            pending = built.ExecuteAsync();
        }

        release.SetResult();
        var outcome = (Outcome<Invoice>.Success)await pending;
        await Assert.That(outcome.RunId).IsEqualTo(f.Context.Reference.RunId);
        await Assert
            .That(outcome.Context!.Operation.DraftId)
            .IsEqualTo(f.Context.Reference.Draft!.DraftId);
        await Assert.That(f.Records.Single().Context).IsEqualTo(outcome.Context);
        await Assert
            .That(
                f.Events.Records.OfType<ExecutionEvent>()
                    .Select(e => e.Kind)
                    .SequenceEqual(
                        new[]
                        {
                            "execution.started",
                            "attempt.started",
                            "attempt.finished",
                            "output.delivery",
                            "execution.finished",
                        }
                    )
            )
            .IsTrue();
        await Assert
            .That(f.Events.Records.All(e => e.Operation == outcome.Context.Operation))
            .IsTrue();
    }

    [Test]
    public async Task Explicit_context_replaces_ambient_whole_and_suppression_creates_an_unassociated_run()
    {
        using var f = new Fixture();
        var other = Context(f.Ledger.CreateRun(f.Actor));
        using var ambient = AgentCmdContext.BeginScope(f.Context);
        var explicitResult = (Outcome<Invoice>.Success)
            await f.Command.WithParameters(new("doc")).InContext(other).ExecuteAsync();
        await Assert.That(explicitResult.Context!.Operation.Reference).IsEqualTo(other.Reference);
        await Assert
            .That(explicitResult.Context.Operation.Source)
            .IsEqualTo(ContextSource.Explicit);
        await Assert.That(explicitResult.Context.Operation.AmbientOverridden).IsTrue();
        var suppressed = (Outcome<Invoice>.Success)
            await f.Command.WithParameters(new("doc")).WithoutContext().ExecuteAsync();
        await Assert.That(suppressed.Context!.Operation.Reference).IsNull();
        await Assert.That(suppressed.RunId).IsNotEqualTo(f.Context.Reference.RunId);
    }

    [Test]
    [Arguments("actor")]
    [Arguments("input")]
    [Arguments("configure")]
    [Arguments("selector")]
    [Arguments("null_context")]
    [Arguments("cancelled")]
    [Arguments("disposed")]
    public async Task Early_failures_keep_draft_outside_snapshot_and_never_send_model_request(
        string mode
    )
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Zeeq.AgentCmd",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var f = new Fixture(failActor: mode == "actor");
        using var ambient = AgentCmdContext.BeginScope(f.Context);
        var call = f.Command.WithParameters(new(mode == "input" ? " " : "doc"));
        if (mode == "configure")
        {
            call = call.Configure(_ => throw new InvalidOperationException());
        }

        if (mode == "selector")
        {
            call = call.WithProfile<Profiles>(_ => throw new InvalidOperationException());
        }

        if (mode == "null_context")
        {
            call = call.InContext(null!);
        }

        if (mode == "disposed")
        {
            f.Runtime.Dispose();
        }

        using var cancel = new CancellationTokenSource();
        if (mode == "cancelled")
        {
            cancel.Cancel();
        }

        var result = await call.ExecuteAsync(cancel.Token);
        await Assert.That(result is not Outcome<Invoice>.Success).IsTrue();
        await Assert.That(f.Client.Calls).IsEqualTo(0);
        await Assert
            .That(result.Context!.Operation.DraftId)
            .IsEqualTo(f.Context.Reference.Draft!.DraftId);
        if (mode == "cancelled")
        {
            await Assert.That(f.Records.Single().Snapshot is not null).IsTrue();
        }
        else
        {
            await Assert.That(f.Records.Single().Snapshot).IsNull();
        }

        await Assert.That(f.Records.Single().Context).IsEqualTo(result.Context);
        await Assert
            .That(f.Events.Records.All(e => e.Operation == result.Context.Operation))
            .IsTrue();
        await Assert
            .That(
                spans
                    .Single(s => s.OperationName == "agentcmd.execute")
                    .GetTagItem("agentcmd.output.draft_id")
                    ?.ToString()
            )
            .IsEqualTo(f.Context.Reference.Draft!.DraftId.ToString());
        await Assert.That(spans.Any(s => s.OperationName == "agentcmd.provider.attempt")).IsFalse();
    }

    [Test]
    public async Task Routing_retry_and_fallback_share_captured_context_with_unique_attempt_ids()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("synthetic", "worker");
        var context = Context(ledger.CreateRun(actor));
        var events = new Events();
        var first = ProfileKey.Declare("first");
        var second = ProfileKey.Declare("second");
        var failing = new FailingClient();
        using var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    new[]
                    {
                        new ExecutionProfile(
                            first,
                            "a",
                            "model-a",
                            Retry: new(1, TimeSpan.Zero, false),
                            Fallback: second
                        ),
                        new ExecutionProfile(second, "b", "model-b"),
                    },
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["a"] = new(() => failing),
                        ["b"] = new(() => new Client()),
                    }
                )
        ).Router;
        using var runtime = CommandRuntime.Create(router, ledger, () => actor, events: events);
        using var scope = AgentCmdContext.BeginScope(context);
        var result = (Outcome<Invoice>.Success)
            await new ExtractInvoice(runtime)
                .WithParameters(new("doc"))
                .WithProfile(first)
                .ExecuteAsync();
        var attempts = result.Snapshot.Routing!.Attempts;
        await Assert.That(attempts.Count).IsEqualTo(3);
        await Assert.That(attempts.All(a => a.Context == result.Context)).IsTrue();
        await Assert.That(attempts.Select(a => a.AttemptId).Distinct().Count()).IsEqualTo(3);
        await Assert
            .That(events.Records.OfType<ExecutionEvent>().Count(e => e.Kind == "attempt.started"))
            .IsEqualTo(3);
        await Assert
            .That(events.Records.All(e => e.Operation == result.Context.Operation))
            .IsTrue();
    }

    sealed class FailingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> m,
            ChatOptions? o = null,
            CancellationToken c = default
        ) =>
            Task.FromException<ChatResponse>(
                new HttpRequestException(
                    "synthetic",
                    null,
                    System.Net.HttpStatusCode.ServiceUnavailable
                )
            );

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> m,
            ChatOptions? o = null,
            CancellationToken c = default
        ) => throw new NotSupportedException();

        public object? GetService(Type t, object? k = null) => null;

        public void Dispose() { }
    }

    sealed class ThrowingSink : IExecutionEventSink
    {
        public ValueTask EmitAsync(LlmEvent record, CancellationToken ct) =>
            throw new InvalidOperationException("synthetic sink failure");
    }

    [Test]
    public async Task Shared_event_contract_preserves_root_and_leaf_identity_after_delivery_and_JSON_roundtrip()
    {
        var operation = AgentCmdContext.Capture(Context());
        var command = new CommandExecutionContext(
            Guid.NewGuid(),
            "extract",
            1,
            "hash",
            operation,
            "header",
            Guid.NewGuid()
        );
        var composition = new CompositionEventContext(
            operation,
            "document.extract",
            "key",
            OutboxSequence: 7
        );
        LlmEvent[] records =
        [
            new ExecutionEvent("execution.finished", command, Outcome: "Success"),
            new CompositionEvent("root.completed", composition, Outcome: "Published"),
        ];
        var sink = new Events();
        foreach (var record in records)
        {
            await ExecutionEvents.EmitSafelyAsync(sink, record);
        }

        await Assert.That(sink.Records.All(e => e.Operation == operation)).IsTrue();

        var wire = System.Text.Json.JsonSerializer.Serialize(records);
        var restored = System.Text.Json.JsonSerializer.Deserialize<LlmEvent[]>(wire)!;
        await Assert.That(restored.SequenceEqual(records)).IsTrue();
        // A closed switch makes a future event kind a compiler obligation for
        // consumers. A default interface method would silently drop root events.
        var kinds = restored.Select(e =>
            e switch
            {
                ExecutionEvent execution => execution.Kind,
                CompositionEvent root => root.Kind,
            }
        );
        await Assert
            .That(kinds.SequenceEqual(new[] { "execution.finished", "root.completed" }))
            .IsTrue();
        await Assert.That(((CompositionEvent)restored[1]).Context.ExecutionId).IsNull();
        await Assert
            .That(((ExecutionEvent)restored[0]).Context.ExecutionId)
            .IsEqualTo(command.ExecutionId);
    }

    [Test]
    public async Task Throwing_async_observer_cannot_replace_a_stored_model_success()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("synthetic", "worker");
        var context = Context(ledger.CreateRun(actor));
        using var runtime = CommandRuntime.Create(
            () => new Client(),
            new Dictionary<string, Profile> { ["extraction"] = new("synthetic") },
            ledger,
            () => actor,
            events: new ThrowingSink()
        );
        var result = await new ExtractInvoice(runtime)
            .WithParameters(new("doc"))
            .InContext(context)
            .ExecuteAsync();
        await Assert
            .That(result is Outcome<Invoice>.Success { Delivery: OutputDelivery.Stored })
            .IsTrue();
        await Assert.That(result.Context!.Operation.Reference).IsEqualTo(context.Reference);
    }
}
