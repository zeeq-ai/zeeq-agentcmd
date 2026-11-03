# Propagation adapter research: HTTP and queues

Date: 2026-11-01. Status: reviewed design plus an executable propagation spike after the user's go signal. Research involved the expert researcher, Principal Engineer II and DMTS. The original eight isolated tests characterized OpenTelemetry.Api 1.20.1. The subsequent [context module](../../research/spikes/ContextPropagation/README.md) implements local scopes, explicit overrides, bounded carriers and execution/attempt/emitter context. The [Aspire reference proof](../../research/spikes/PropagationHost/README.md) adds actual RabbitMQ and separate producer/worker processes. Model content remains synthetic; no hosted model or telemetry vendor was contacted. This does not implement the full production composition, grant or recovery design below.

## Recommendation

Keep LlmCmd's AsyncLocal as the default local operation context. A propagation adapter carries a small versioned reference across the transport; the receiver validates and authorizes it, then synchronously opens a fresh local scope around the handler. Explicit invocation context remains an override. No .NET scope/frame, actor identity, credential, grant, prompt or parameter object travels in the propagation reference.

Use two independent contracts:

- Required durable work context for component jobs: versioned operation/draft reference, exact slot/generation, stable WorkId/ClaimKey, declared definition/input/dependency references and business payload. Persist these with the queued message/outbox.
- Optional observational context: traceparent/tracestate and allowlisted diagnostic baggage. Losing telemetry does not erase or change work membership.

The adapter is independent of provider adapters and evaluation sinks. It restores the context that the executor later captures into all their normalized records. Do not build a broker × provider × collector matrix.

## Layering and small public seam

1. Carrier codec: thin transport-specific conversion, preserving all raw occurrences until validation. HTTP strings/repeated lines and binary/object message headers have different representations. Decode documented byte values strictly; reject unsupported representations instead of calling arbitrary ToString. Copy borrowed payload/header data before it outlives the broker callback.
2. Parser: pure, bounded, version-aware extraction into an untrusted ContextRef. Suggested closed outcomes: Absent, Parsed, UnsupportedVersion, Malformed and LimitExceeded. Validate count/byte/value limits and duplicate critical fields. JSON and baggage duplicate handling must not silently pick a winner.
3. Endpoint policy: Component, Auxiliary or None; selects the authoritative source and defines optional telemetry behavior. It does no I/O.
4. Async resolver: authenticates the current worker independently, resolves the durable target and checks grants, schema/generation, work identity, input/dependencies and current lease/fence. Parsing never grants authority or attaches a component.

An optional OpenTelemetry binding composes TraceContextPropagator and BaggagePropagator for the observational part. Broker/framework bindings provide header conversion and handler interception. Register policy/propagator instances once through DI; do not change process-global defaults per message or tenant. Concrete interface names and wire encoding are proposals, not published APIs.

Illustrative core contracts:

```csharp
interface ICarrierCodec<TCarrier>
{
    IReadOnlyList<string> GetAll(TCarrier carrier, string key);
    void Set(TCarrier carrier, string key, string value);
}

interface IContextResolver
{
    ValueTask<ResolveResult> ResolveAsync(
        UntrustedContextRef reference, WorkerIdentity worker,
        IngressPolicy policy, CancellationToken ct);
}
```

Injection should construct and validate a new bounded header snapshot before writing it to the transport, so failure cannot leave a partially mutated message. Keep broker acknowledgement outside the pure parser/propagator.

## Authoritative source and baggage policy

For component work, the persisted WorkEnvelope is authoritative after resolution/authorization. Missing or invalid required work context rejects before inference. Optional baggage cannot retarget the work or block a valid authorized envelope: ignore contradictory diagnostic baggage and record a bounded diagnostic. A malformed optional telemetry carrier is not the same failure as a malformed required work envelope.

For auxiliary endpoints, the operation reference is optional. Baggage hints may be enabled explicitly, validated and resolved, but never establish component membership. With neither a reference nor opted-in hints, the call has no remote draft association. A handler's explicit InContext override still controls its ordinary calls. A component's explicit slot must match the declared WorkRef; an override cannot silently retarget the queued component job.

Do not place private LlmCmd references in global Baggage.Current or Activity.Baggage by default. Use LlmCmd's own context and an explicit codec for configured internal destinations. A dedicated private carrier/header is the recommended HTTP default; the exact versioned encoding remains to be specified. Baggage ingestion is off by default and read-only for diagnostics. If interoperable baggage output is offered later, it needs an explicit internal destination policy and final-wire tests.

This refines the earlier “allowlisted baggage can carry correlation IDs” idea. The issue is not W3C capability: it is that unrelated installed HTTP instrumentation can propagate global baggage to external requests. Intended telemetry exporters still receive approved DraftId/WorkId fields in their records; blocking private propagation headers does not remove those fields from OTLP payloads.

## Per-message lifecycle

Capture the producer's effective context and stable work intent when enqueueing or writing its outbox, not later when the outbox worker runs under another ambient scope. Keep the immutable references in the persisted envelope. Native broker delivery IDs remain transport diagnostics.

At the receiver:

```csharp
// Illustrative framework wrapper, not implemented public API.
var parsed = parser.Parse(delivery.Envelope, delivery.Headers);
var resolved = await resolver.ResolveAsync(parsed.Reference, worker, policy, ct);

using (LlmCmdContext.BeginScope(resolved.Context)) // synchronous after async resolution
{
    await next.HandleAsync(delivery, resolved.Work, ct);
    await journal.EnsureTerminalAsync(resolved.Work, ct);
}

await transport.AcknowledgeAsync(delivery, ct);
```

Real code branches on closed parser/resolver/handler outcomes before reaching the successful path above. An async method that merely returns an IDisposable scope is unsuitable: its AsyncLocal write need not populate its caller. A framework filter may instead enter the scope and await the next handler inside that same filter.

Use one scope and work claim per message, including batched deliveries. Do not make one draft the ambient context for an entire batch. The wrapper awaits all owned work and restores ambient changes in finally. Existing transport frameworks retain ownership of their receive, tracing and settlement lifecycle.

Acknowledge after the message's required durable terminal/attachment/outbox transition, not after telemetry flush and not after all sibling aggregate slots finish. Permanent invalid input can be durably rejected then parked/dead-lettered/acknowledged according to endpoint policy; transient resolution/storage failure can be retried without model work. The parser itself does not decide broker settlement. Unsupported major versions must not create an unbounded hot retry loop; deployment/parking policy owns recovery.

## Idempotency and recovery

WorkId/ClaimKey is assigned once per intended job and persisted by the producer/outbox. Do not derive it from a delivery tag, consumer retry counter, TraceId or a broker-generated per-delivery identifier. A transport MessageId may mirror WorkId only when the application's persisted identity contract explicitly guarantees that stability.

Known completed work replays its stored receipt after lost acknowledgement, without a new model request. A live claim/lease defers competing work. Recovery from an expired claim whose provider request may already have run uses an explicit policy, a fenced lease epoch and a new linked execution attempt; the old spend remains unknown until reconciled. Reject a stale worker's later attachment. Intentional regeneration uses a new work intent, not a delivery retry.

At-least-once delivery plus idempotent attachment does not provide exactly-once inference or billing. Include failed and abandoned executions in spend coverage. Define a work-wide retry/deadline budget separately from per-command profile retries/fallback so redelivery cannot reset all budgets forever.

## Trace structure and emitter context

Keep business identity separate from tracing. Current OTel messaging semconv 1.44 (Development) defaults consumer Receive/Process links to message creation contexts. Single-message Process parenting can be an explicit option; preserve an existing request's correlation. Batch spans link to the contributing message creation contexts. Do not force delayed work into one shared trace or keep a span open while a message waits in the queue.

The executor captures the effective authorized/requested context once before its first await. Every command, attempt, failure, delivery, attachment and feedback record gets that captured value. Queued emitters never read AsyncLocal or Baggage.Current. DraftId and WorkId stay out of metric labels. Sampling must not erase durable membership.

Do not copy producer ExecutionId into a new unrelated consumer command. Keep WorkId stable for the intended job; known replay references the original execution/receipt. Recovery or intentional new command runs get their own execution identity and explicit lineage, while retaining the correct draft/work association.

## Pinned .NET and OSS findings

OpenTelemetry.Api **1.20.1** exposes generic carrier getters/setters on TextMapPropagator and pairs ActivityContext with OpenTelemetry.Baggage in PropagationContext. It is not restricted to HTTP. Activity.Current, Activity baggage, Baggage.Current and LlmCmd's own AsyncLocal are separate stores. Extraction returns data; it does not authorize or establish the proposed LlmCmd scope.

Two extraction traps were source-verified and reproduced:

- A valid ActivityContext in the extraction seed is preserved instead of the incoming trace. A non-default seed baggage similarly prevents received baggage from replacing it. Extract each message from default PropagationContext; apply ambient parent/link policy separately.
- Baggage extraction collapses duplicate keys with last-wins behavior. Preserve and validate raw critical occurrences before normalization. W3C permits duplicate baggage keys; stricter application-key handling is our policy, not a claim about W3C.

MassTransit **v8.5.7**, source commit **855cf1752c94ca9498e0c45ce8d09fdc9e957dd6**, uses MT-Activity-Id and MT-Activity-Correlation-Context in the inspected logging/diagnostics path. Generic W3C extraction alone is not an interoperability claim. A thin send/consume binding should reuse framework tracing and await its consumer pipeline before settlement, rather than start duplicate consumer spans.

.NET's DistributedContextPropagator and OTel's TextMapPropagator are distinct injection paths. In the inspected OTel HTTP instrumentation **1.19.0** example, baggage injection happens before the tracing filter and regardless of sampling. That version is a source example, not a claim that the repo pins HTTP instrumentation to core API 1.20.1. Removing headers in an outer handler, a tracing filter, or setting one handler's ActivityHeadersPropagator to null is not proof that another instrumentation path cannot inject later. Use a controlled external-provider client stack, destination/redirect policy and actual final-wire assertions.

## Evidence, limits and next steps

Eight TUnit **1.78.0** tests passed against the actual OTel.Api **1.20.1** on the pinned .NET 11 RC3 SDK. They cover explicit text round-trip; seeded-trace preservation; seeded-baggage preservation; repeated-key collapse; independent message extraction; a binary-header mapping; extraction leaving Baggage.Current unchanged; and malformed optional trace context. Build: zero warnings/errors. Artifacts: `/tmp/opencode/llmcmd-propagation-probe/{PropagationTests.cs,Probe.csproj,tests.xml,build.log,run.log}`.

Those original eight tests are in-process library characterizations. They must not be described as a cross-process queue test. The subsequent repository spike has its own TUnit reports: `/tmp/opencode/llmcmd-propagation/tests.xml` for parser/scope/runtime contracts and `host-tests.xml` for actual RabbitMQ/HTTP/collector scenarios. Its exact coverage and remaining limits are documented in the reference README.

Production gates beyond the current reference proof:

1. Bounded source-specific parsing/policy with a fake resolver and claim journal; verify required-envelope failures, diagnostic baggage disagreement, version/limit/duplicate handling, per-message scope isolation and captured-context emitter records.
2. Real HTTP requests under the exact configured instrumentation stack; verify internal carrier inclusion, explicit overrides, external-provider exclusion and redirects at the final wire.
3. Two separate producer/consumer processes plus Aspire collector. Pick a reference broker or framework binding and run its real contract tests: redelivery, lost ack, leases/fencing, delayed work, batch isolation, separate trace links and correlation without sampled traces. A fake carrier/queue only proves its own process boundary, not broker conformance.

RabbitMQ is selected only as a reference transport, not a core or production shipping dependency. The reference proves context restoration, known-receipt replay, source trace links and egress exclusion for one explicit stack. Real authentication/grants, dependency checks, lease/fencing recovery, durable HumanLedger/composition storage and a transactional outbox remain unimplemented. Keep the core broker-neutral and add only the binding the application needs.

## Primary sources

- [W3C baggage and duplicate keys](https://www.w3.org/TR/baggage/#baggage-string), [mutation](https://www.w3.org/TR/baggage/#mutating-baggage).
- OTel core-1.20.1: [TextMapPropagator](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.20.1/src/OpenTelemetry.Api/Context/Propagation/TextMapPropagator.cs), [TraceContextPropagator](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.20.1/src/OpenTelemetry.Api/Context/Propagation/TraceContextPropagator.cs), [BaggagePropagator](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.20.1/src/OpenTelemetry.Api/Context/Propagation/BaggagePropagator.cs).
- [OTel .NET API and context](https://github.com/open-telemetry/opentelemetry-dotnet/blob/core-1.20.1/src/OpenTelemetry.Api/README.md); [HTTP instrumentation](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/Instrumentation.Http-1.19.0/src/OpenTelemetry.Instrumentation.Http/README.md); [HTTP injection ordering](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/Instrumentation.Http-1.19.0/src/OpenTelemetry.Instrumentation.Http/Implementation/HttpHandlerDiagnosticListener.cs#L78).
- [Messaging trace structure](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#trace-structure), [messaging propagation](https://opentelemetry.io/docs/specs/semconv/messaging/messaging-spans/#context-propagation).
- MassTransit source pin: [outbound diagnostics](https://github.com/MassTransit/MassTransit/blob/855cf1752c94ca9498e0c45ce8d09fdc9e957dd6/src/MassTransit/Logging/Diagnostics/LogContextActivityExtensions.cs), [inbound diagnostics](https://github.com/MassTransit/MassTransit/blob/855cf1752c94ca9498e0c45ce8d09fdc9e957dd6/src/MassTransit/Logging/Diagnostics/DiagnosticActivityExtensions.cs), [receive lifecycle](https://github.com/MassTransit/MassTransit/blob/855cf1752c94ca9498e0c45ce8d09fdc9e957dd6/src/MassTransit/Transports/ReceivePipeDispatcher.cs).
- [RabbitMQ .NET guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) documents object-valued headers, callback memory ownership and manual acknowledgement. It is an example of transport differences, not a selected dependency or tested binding.
