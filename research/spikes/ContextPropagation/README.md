# Context propagation spike

The CommandRoot invocation now inherits a flow-local context by default. An explicit context replaces it as a whole. Explicit suppression stops inheritance. The command captures context synchronously at its terminal call, before validation or any await.

```csharp
using LlmCmd.ContextPropagation;

// The host creates/resolves the run and context under its current actor.
using var scope = LlmCmdContext.BeginScope(context);

var invoice = await extractInvoice
    .WithParameters(new InvoiceParameters("invoice text", Locale: "en"))
    .ExecuteAsync(ct); // inherits context at this statement

var alternate = await extractInvoice.WithParameters(new("other text"))
    .InContext(otherContext).ExecuteAsync(ct); // whole override

var independent = await extractInvoice.WithParameters(new("utility text"))
    .WithoutContext().ExecuteAsync(ct); // no inherited draft
```

Scopes nest and restore. The scope does no storage I/O and never publishes or rolls back work. Its immutable frame can flow to a child task after the parent disposes its scope. Await owned work; background jobs must establish their own scope after resolution. Out-of-order disposal is a no-op; dispose in LIFO order. Async helper writes do not set the caller's ambient context. BeginScope is synchronous; an asynchronous receiver wrapper calls it and invokes the handler within the same flow.

## Records and runtime integration

`CommandExecutionContext` is outside the nullable snapshot. Every returned outcome, legacy outcome notification, asynchronous event and routing attempt carries the captured context, including early actor/configuration failure, cancellation and zero attempts. Attempts have distinct AttemptIds. Command and attempt spans project DraftId/WorkId directly from that record. These IDs are not metric dimensions.

`IExecutionEventSink.EmitAsync(LlmEvent, ...)` receives a closed hierarchy: `ExecutionEvent` for leaf calls and `CompositionEvent` for roots and steps. Both expose the captured `Operation`. A root, reused part, or skipped step has no fabricated model execution ID; `CompositionEventContext.ExecutionId` is nullable. Update sink switches when a new event case is added. JSON storage that reorders properties must read `$event` with `AllowOutOfOrderMetadataProperties = true`.

The observer receives immutable records, with a two-second delivery timeout per notification. Errors cannot replace the model result. This is best-effort observation, **not** an execution journal or outbox. `IExecutionJournal` is a separate awaited port used before provider sends. Composition projects its stored outbox entries to the same observer; its sequence numbers order only composition records. Queued implementations must use the supplied record and not read ambient state at delivery time. The current synchronous legacy callback is also best effort and can still block its caller.

Context is correlation, not authority. The public `FromResolvedReference` factory lets a host-owned resolver construct a local value. It does not prove authorization. The [composition spike](../Composition.Demo/README.md) adds claims before spend, stored attachments and publication through an in-memory store. It rechecks run ownership at execution. Production grants and durable storage remain host work; the AsyncLocal frame itself establishes neither.

## Carrier and ingress contract

`PropagationAdapter` has no broker dependency. `ICarrierCodec<T>` snapshots repeated raw text headers and builds a fresh carrier. A binary example handles strings, byte arrays and ReadOnlyMemory with strict UTF-8. A transport binding selects its context fields before decoding opaque broker metadata.

For internal HTTP, `Inject(..., internalDestination: true)` creates a versioned `llmcmd-context` header plus explicit W3C trace context. It does not install global baggage or replace the global OTel propagator. External injection is rejected. The caller configures which endpoints are internal; a boolean is policy input, not destination authentication.

For queued work, `EncodeWork` creates a version-1 JSON envelope: operation reference, stable producer-assigned WorkId, optional SlotId and business payload. Component policy requires a draft and slot. The draft reference pins definition/revision/hash, generation and input revision. The host resolver checks these against authoritative storage and its independently authenticated actor. A transported ID is never a grant.

Parsing returns closed Absent, Parsed, UnsupportedVersion, Malformed or LimitExceeded cases. Limits apply to bytes, count, value size, envelope size and JSON depth. Duplicate authoritative JSON properties and duplicate private context headers are rejected. Unknown additive JSON fields are ignored. The fingerprint hashes the normalized declared envelope including its payload; it is an intent comparison, not a universal canonical-JSON or document-provenance proof.

The work envelope wins over optional headers. Conflicting, malformed or repeated optional context headers are diagnostic when a valid envelope exists. Baggage is ignored, including duplicate keys. It cannot establish membership or veto valid work. Baggage hint ingestion is not implemented in this spike.

`PropagationIngress.HandleAsync` awaits the resolver, verifies that the returned reference/WorkId match, then establishes a local scope around the handler. A consumer activity links to the producer context while preserving an existing processing parent. It does not acknowledge a broker. The transport wrapper waits for its own required durable disposition before settlement.

## External HTTP boundary

`ExternalProviderHttp.CreateClient()` owns a specific HTTP stack. It removes explicit LlmCmd headers, filters LlmCmd-prefixed global OTel baggage within the send flow, disables the .NET handler's propagation path and does not follow redirects. Normal local trace activities remain available to OTel. The reference proof tests this exact stack with HTTP instrumentation 1.19.0, private values in both Activity baggage and Baggage.Current, and actual loopback requests.

This is not a guarantee for arbitrary SDK clients or other instrumentation versions. An SDK must accept this handler/client or provide an equivalent verified boundary. Context still goes to approved telemetry payloads; preventing private HTTP propagation does not remove correlation fields from emitter records.

## Verification

```sh
/tmp/opencode/llmcmd-dotnet11/dotnet build tests/LlmCmd.ContextPropagation.Tests/LlmCmd.ContextPropagation.Tests.csproj
TUNIT_ENABLE_JUNIT_REPORTER=1 /tmp/opencode/llmcmd-dotnet11/dotnet run --no-build --project tests/LlmCmd.ContextPropagation.Tests/LlmCmd.ContextPropagation.Tests.csproj -- --junit-output-path /tmp/opencode/llmcmd-propagation/tests.xml
```

The [Aspire reference proof](../PropagationHost/README.md) adds actual RabbitMQ, separate producer/worker processes, collector trace links, replay and HTTP wire assertions. See [the research](../../docs/research/propagation-adapter.md) for pinned sources and remaining production gates.
