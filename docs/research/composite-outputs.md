# Composite outputs: implementation and design rationale

Historical spike record. Its precompile setup is retained for reproduction.
The maintained library uses generated metadata and a package-only NativeAOT proof;
see [the current serialization boundary](../architecture/serialization-and-aot.md).

Status: an implemented and verified research spike. The user authorized the deferred root implementation after the earlier direct-slot review. Generated composition methods, the in-memory store, typed dependencies, aggregate publication, and the reflection-disabled demo now exist. The final integration run passed 442 checks, including 97 composition runtime tests, compiler/precompile proofs, both demo branches, and actual Aspire collector/RabbitMQ checks. This evidence covers the spike, not production durability or live model behavior.

Start with [Composition.Demo](../../research/spikes/Composition.Demo/README.md) for the complete runnable example and explanations of every application type. The public contracts are in [CompositeContracts.cs](../../research/spikes/CommandRoot/Runtime/Composition/CompositeContracts.cs); runtime behavior lives in [CompositionRoot.cs](../../research/spikes/CommandRoot/Runtime/Composition/CompositionRoot.cs), [CompositionEngine.cs](../../research/spikes/CommandRoot/Runtime/Composition/CompositionEngine.cs), and [CompositionStore.cs](../../research/spikes/CommandRoot/Runtime/Composition/CompositionStore.cs). The [vocabulary review](composition-vocabulary.md) records why the generated methods use `With{Slot}From`.

## Current decision

Open a bounded typed root, register sources for named output slots across ordinary statements and branches, then execute the plan once. Each executed component claims its slot before provider spending. Publication requires stored component output references and produces an aggregate with its own identity and an exact membership manifest. A common `RunId` or trace tag is correlation, not membership.

The root construction handle is mutable; leaf `Invocation<TParameters, TResult>` values remain immutable. Registration returns the same root, so a caller can discard the return value or chain several registrations. `ExecuteAsync` freezes the plan and memoizes its operation/result. Repeating it on the same handle does not retry failed work. Registration after execution starts throws. Duplicate and missing required registrations are reported as configuration failures before this plan spends on providers.

The compiler checks slot/result compatibility through invariant `PartSlot<TWhole, TPart>` tokens and generated typed methods. It cannot prove that arbitrary application branches, requests, or jobs filled every required slot. The runner checks actual registrations and stored state. No mandatory orchestration subclass or single chained expression is required.

## Implemented application surface

The demo declares `DocumentExtraction` with four required result records: `Header(string Number)`, `Parties(string Seller)`, `LineItems(int Count)`, and `Totals(decimal Amount)`. `SourceText(string Document)` is the independent-pass input; `TotalsParameters(string Document, int LineCount)` is the dependent totals input. Their full declarations and the five partial command classes are in [Commands.cs](../../research/spikes/Composition.Demo/Commands.cs).

```csharp
[CompositeOutput("invoice.composite", Revision = 1)]
public sealed partial record DocumentExtraction(
    [property: JsonPropertyName("header_data")] Header Header,
    Parties Parties,
    LineItems LineItems,
    Totals Totals);

var text = new SourceText("example invoice");
var root = await roots.BeginAsync<DocumentExtraction>(
    new CompositionRequest(runId, "input-v1", "demo"));

root.WithHeaderFrom(header.WithParameters(text))
    .WithPartiesFrom(parties, text);

if (detailed) root.WithLineItemsFrom(lines.WithParameters(text));
else root.WithLineItemsFrom(quickLines.WithParameters(text));

root.WithTotalsFrom(root.Steps.LineItems.Then(pinned =>
    totals.WithParameters(
        new TotalsParameters(text.Document, pinned.Value.Count))));

var outcome = await root.ExecuteAsync();
```

`roots` is the injected `IRootCommands` factory. `root` is `CompositionRoot<DocumentExtraction>`. The other receivers are injected `ExtractHeader`, `ExtractParties`, `ExtractLineItems`, `ExtractQuickLineItems`, and `ExtractTotals` commands. Each derives from `PromptCommand<TParameters, TResult>` with the input and result types above. `detailed` is an ordinary application condition. The executable selects it with `--quick` and contains the complete startup code, without placeholder callbacks.

Generated `With{Slot}From` overloads accept a configured invocation, a command plus parameters, or a deferred `PartSource<TPart>`. `WithPartFrom(typedSlot, source)` is the generic fallback. Generated `Reuse{Slot}(Pinned<TPart>)` methods attach an exact existing output instead of running a new command; `ReusePart` is the generic counterpart. Reuse rereads and authorizes the stored reference rather than trusting the supplied `Value`.

`root.Steps.LineItems` is a `StepRef<LineItems>`, not a value. Its `Then` callback receives `Pinned<LineItems>` after the prerequisite is stored. `Pinned<T>` contains a typed value read from stored JSON and the exact `OutputRef`. The callback constructs the dependent invocation; the engine executes it. Independent work may run in parallel. A prerequisite that does not attach causes its dependent step to be skipped, without a dependent provider call.

Generated registration includes commands and aggregate descriptors. The demo's `services.AddLlmCommandsFromComposition_Demo(runtime)` also wires `IRootCommands`, the composition registry, the default in-memory store, and the same ledger and actor accessor used by leaves. The precompile target includes aggregate/result metadata before normal source generation. The executable disables reflection serialization; it is not a NativeAOT publish proof.

## Identity, replay, and lifecycle

- `RunId` identifies the run or evaluation scope. It is not an output identity.
- `CompositionRequest` carries `RunId`, `InputRevision`, `Key`, and optional `TrialId`. Together with tenant and definition identity, these form the key for opening or reopening a draft.
- `DraftId` identifies one assembly generation, independently of trace IDs. Reopening the same stored request preserves it. The default store preserves it only while that process lives.
- `SlotId` identifies the declared responsibility. Generated slot IDs currently follow CLR member names such as `Header`; `JsonName` separately follows the serializer contract, such as `header_data`. A CLR rename is therefore a contract change, not an automatic stable-ID migration.
- `StepId` identifies a claimed component attempt. A leaf `ExecutionId` identifies a command invocation, including its transport retries/fallback. Root operations, reuse, and skipped steps do not invent model executions.
- A component `OutputRef` pins one immutable stored artifact/version. `AggregateRef` adds the draft, aggregate, and definition identities around the published aggregate's exact `OutputRef`.

`ProducerRef` distinguishes command execution, composition, and human correction. An aggregate uses `ProducerRef.Composition`; it does not borrow the last leaf's execution ID. This prevents a human grade of the assembled answer from being misidentified as a grade of one model call.

The store has explicit claim, attach, release, and seal transitions. `Attached` slots are terminal. A later handle can reopen the same draft and retry eligible failed/cancelled work while retaining stored attachments. An unresolved claim is `Pending`, with unknown spending; it is not silently retried. Replay compares stored intent instead of treating any previous output as interchangeable. Replay and authorization edge cases remain under active review.

There is **no replace or revise API yet**. The earlier proposal to open a new generation from an exact aggregate, reuse unaffected components, replace one selected component, and preserve aggregate lineage remains future work. Different keys or trials can represent distinct runs of the same input; they do not supply a complete revision workflow.

## Stored evidence and outcomes

Only `OutputDelivery.Stored` can fill a slot. A parsed leaf result with rejected or failed delivery remains unfilled. The engine prepares observable command inputs and compares invocation fingerprints; dependent inputs come from exact stored prerequisite references. The application-supplied `InputRevision` label does not prove the document content matches that revision. A trusted input-artifact contract or equivalent validation remains necessary for that stronger guarantee.

The current `CompositeMember` records `SlotId`, `JsonName`, exact `OutputRef`, provenance (`Executed` or `Reused`), and available step/execution IDs. It is not the richer proposed manifest of arbitrary source/destination pointers and a complete persisted dependency DAG. Assembly mounts each selected component's stored wire JSON under its destination JSON name, then deserializes and validates the whole. It does not reserialize the typed value to silently rename or omit component data. General overlapping merges, array appends, and arbitrary projection are outside this API.

`CompositeOutcome<TWhole>` remains distinct from a leaf outcome:

- `Published` contains the typed value, exact aggregate reference, member references, and step results.
- `ConfigurationFailure` reports invalid wiring or request state. Missing required registrations are detected here before spending.
- `Incomplete` reports required work that did not attach. Existing attachments remain in the draft.
- `Conflict` reports incompatible stored intent or parts that cannot form the requested aggregate.
- `Unavailable` reports storage/engine failure and can include unresolved spending.
- `Cancelled` reports cancellation with the available step results.

A `StepResult` reports `Attached`, `Replayed`, `Reused`, `Failed`, `Cancelled`, `Pending`, `Skipped`, or `Conflict`. It retains the available caller, exact output reference, context, snapshot, delivery result, and `ExecutionFailure`. Callback/dependency errors can have a step code without a leaf execution or leaf failure, because the command never ran. Root failure does not flatten away the component evidence.

The leaf contract is `Outcome<T>.Success | Outcome<T>.Failed(ExecutionFailure)`. `ExecutionFailure` is a non-generic closed record; `Map`, `Bind`, and `BindAsync` forward the exact failure object when the source failed. These combinators transform application values/control flow. They do not create aggregate membership or persist a mapped output. Deferred `StepRef.Then` is the composition mechanism for result-dependent work.

## Paths, feedback, and provenance

Grades and corrections use RFC 6901 JSON Pointers over serialized names, including naming policies and `JsonPropertyName`. Escape `~` and `/` as `~0` and `~1`. Dot strings are suitable only for display; JSONPath is a query language rather than one exact field address. Array positions address one immutable version. Stable item IDs and expected values are needed to interpret changes across versions.

The demo grades `/Totals/Amount` on `published.Aggregate.Output`. Its header number is `/header_data/Number`, not `/Header/Number`. The exact target includes the output version; no operation guesses “latest.” Aggregate feedback does not fan out into component datasets. Component feedback targets the component's own exact reference.

The human ledger preserves the original JSON and records append-only corrections as new output versions with a human-correction producer and source lineage. Composition-aware reassembly after a component correction is not automated. The earlier policy for carrying aggregate patches across replacement remains proposed: check expected old values, retain human provenance, and never silently drop or reapply a correction. There is no replace/revise API implementing that policy yet.

Member references and JSON names explain which component supplied a subtree. This is provenance, not proof of causal blame. A richer field-resolution API and persistence of full input/dependency lineage remain separate work.

## Captured context and the shared event contract

`DraftId` must reach emitters as explicit data. It is not reconstructed from `Activity.Current`, baggage, or span tags. A leaf has a `CommandExecutionContext` with its execution identity, captured operation context, and optional slot/step IDs. Root registration supplies the explicit composition target when the leaf runs. Capture happens once for that execution boundary; downstream attempts and emitters receive the captured value even if their delivery flow has a different ambient context.

The shared observer contract in [OperationContext.cs](../../research/spikes/ContextPropagation/OperationContext.cs) is a closed `LlmEvent` union:

- `ExecutionEvent` carries a real leaf's `CommandExecutionContext`, with optional provider-attempt and usage fields.
- `LlmCmd.ContextPropagation.CompositionEvent` carries `CompositionEventContext`: captured operation, definition ID, and available idempotency/slot/step/output identities. Its `ExecutionId` is nullable. Root-only operations and skipped/reused work must not fabricate a model execution. `OutboxSequence` orders composition events only, not all journals.

`IExecutionEventSink.EmitAsync` accepts either event through the same port. It is a best-effort observer. `IExecutionJournal` is a separate awaited port that gates provider sends. The store's `CompositionOutboxEvent` is the transition-log record, distinct from the shared observer event. The in-memory log is not durable delivery. Runtime tests cover root failures, skipped steps, callbacks, and delayed captured-context delivery; those checks do not guarantee delivery to every external observer.

A reused component retains its original producer and output reference. Its new attachment describes the destination draft without relabeling the source execution. Aggregate and later feedback records likewise identify their actual producer/version rather than an invented leaf call.

## Local scope and future distributed execution

Local AsyncLocal operation defaults and explicit override/suppression are implemented in the [context spike](../../research/spikes/ContextPropagation/README.md). Explicit context wins as a whole; it is not a field-by-field merge with ambient state. A composition's selected slot and draft are explicit membership, while ambient-only auxiliary calls provide correlation without filling a slot.

`BeginAsync` opens the draft and returns its handle; it does not install an ambient scope in the awaiting caller. The runner establishes its root scope inside execution for callbacks and passes explicit captured context into component calls. Ordinary application scopes still enter synchronously with `LlmCmdContext.BeginScope`. Disposal restores the parent flow; it does not publish, cancel, roll back spending, or revoke values already captured by child work.

Deferred callbacks remain in-process C# delegates. The current demo does not serialize a plan or resume it across process restarts. Registration can be split across local statements and helpers, but that is not proof of durable execution across HTTP requests or jobs.

The reviewed distributed direction remains: serialize a versioned draft/work reference in explicit request or queue data, resolve and authorize it at the receiving boundary, and establish a fresh local scope. Never serialize the AsyncLocal frame or treat a DraftId as a grant. Worker grants across identities remain design work. The current human ledger authorizes the original run actor.

The separate [propagation design](propagation-adapter.md) and [Aspire reference proof](../../research/spikes/PropagationHost/README.md) cover private carriers, actual RabbitMQ/HTTP boundaries, trace links, and provider-egress exclusions. That evidence does not establish durable composition, distributed leases, or crash recovery. Keep trace propagation separate from work identity; optional baggage is diagnostic and cannot create, change, or veto authoritative membership.

## Tracing and evaluation rationale

Use the exact aggregate version as the evaluation subject, with optional component and trajectory evaluations. Preserve normal trace parents inside a request. Different requests can have different trace IDs; links and `llmcmd.output.draft_id` / `llmcmd.output.slot_id` attributes aid navigation. They do not replace the membership record. These are application attributes, not claimed GenAI standards.

Run, draft, and execution IDs belong on records/spans/logs, not high-cardinality metric labels. Full work spending includes attempts, failures, retries/fallback, and discarded work. Selected-component provenance cost is a different view. Reused outputs and missing usage require explicit accounting. The current API does not provide a complete production cost report, and `Pending` must never be counted as zero spend.

## Historical alternatives and remaining gates

The earlier direct-slot proposal used `OutputDraft<TWhole>`, invariant `BoundSlot<TWhole,TPart>`, `call.IntoSlot(...).ExecuteAsync`, `OpenAsync`, and explicit `CompleteAsync`. It was superseded as the ordinary authoring surface by the bounded deferred root. Those types and methods are historical design names, not current aliases. A lower-level leaf binding could be considered separately; it has not shipped here.

The direct-slot and catalog-selector C#15 probes recorded in earlier reviews explored invariant typing, ordinary branches, and the limits of type-level draft identity. Their hand-written stand-ins under `/tmp/opencode/llmcmd-composite-bound-slots/` and `/tmp/opencode/llmcmd-composite-types/` do not verify today's generated API. The useful rationale survives: explicit destinations distinguish same-typed slots, invariant tokens reject wrong result wiring, and runtime checks remain necessary for actual draft identity and completeness.

Current runtime tests are in [AgentCmd.Composition.Tests](../../tests/AgentCmd.Composition.Tests); the historical compiler and precompile proofs are in [LlmCmd.Composition.Compile](../../research/tests/LlmCmd.Composition.Compile). The [demo](../../research/spikes/Composition.Demo/README.md) contains a four-pass branch, pinned dependency, repeated-handle check, exact aggregate grade, and reflection-disabled startup. The historical [verification script](../../research/scripts/verify.py) passed with both `--with-observability` and `--with-propagation`. Reports are under `/tmp/opencode/llmcmd-verification/`; the composition JUnit report is `/tmp/opencode/llmcmd-composition/tests.xml`.

Production gates remain: persistent stores and output ledgers; end-to-end atomic publication/outbox recovery; distributed lease/fencing and operator recovery; explicit replace/revise and patch-carry-forward rules; verified input-artifact identity and persisted dependency lineage; cross-identity worker grants; complete emitter delivery coverage; and shutdown/delivery behavior. The prototype has no crash recovery or exactly-once provider spending guarantee. Its state disappears on process exit. An unresolved claim is exposed as pending rather than guessed safe to rerun.

## Primary references retained from the design review

- [RFC 6901](https://www.rfc-editor.org/rfc/rfc6901#section-3) and [JSONPath comparison](https://www.rfc-editor.org/rfc/rfc9535#appendix-C).
- [OTel cross-trace links](https://opentelemetry.io/docs/specs/otel/overview/#links-between-spans), [link creation](https://opentelemetry.io/docs/specs/otel/trace/api/#link), and [SDK limits](https://opentelemetry.io/docs/specs/otel/trace/sdk/#span-limits).
- [LangSmith target functions](https://docs.langchain.com/langsmith/define-target-function) and [evaluation scopes](https://docs.langchain.com/langsmith/evaluate-complex-agent).
- [Langfuse sessions](https://langfuse.com/docs/observability/features/sessions) group traces but do not define exact assembly membership; its [experiment model](https://langfuse.com/docs/evaluation/experiments/data-model) is not a portable output/trial identity contract.
- [LangGraph checkpoints](https://docs.langchain.com/oss/python/langgraph/checkpointers) illustrate versioned snapshots without requiring a workflow engine here.
- [W3C baggage](https://www.w3.org/TR/baggage/#mutating-baggage) and [OTel baggage](https://opentelemetry.io/docs/concepts/signals/baggage/) explain why diagnostic baggage is not authoritative work state.
