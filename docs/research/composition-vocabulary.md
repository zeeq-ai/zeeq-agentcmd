# Composition vocabulary review

Historical naming review. Precompile references describe the archived demo;
the maintained library uses [generated serializer metadata](../architecture/serialization-and-aot.md).

Status: the naming decision is implemented in the composition spike. Generated `With{Slot}From`, `Reuse{Slot}`, and typed `Steps` members now work with the library-owned bounded root. The [four-pass demo](../../research/spikes/Composition.Demo/README.md) is the current example. The review discussion below is historical rationale, not a statement that implementation is still awaiting authorization. These names have not been tested with users. Current runtime review and verification remain separate from this naming assessment.

## Implemented vocabulary

Use generated `With{Member}From(...)` methods to register a source command for each named part of the aggregate. Examples are `WithHeaderFrom`, `WithPartiesFrom`, `WithLineItemsFrom` and `WithTotalsFrom`. Derive the member name from the aggregate's CLR property. Serialized JSON names remain a separate contract concern.

Use **Part** in the generic fallback: `WithPartFrom(typedDestination, invocation)`. The generated named methods are the ordinary call surface; callers should not need to mention slot, claim or manifest concepts for each registration. A generic fallback must retain an explicit typed destination, since two members may have the same result type.

`With` keeps the generated methods together in completion. `From` identifies the argument as the source of a future result. The method registers work on the bounded construction handle; it does not obtain the result immediately.

The core overload accepts the existing typed invocation, so leaf configuration stays consistent. The implemented `(command, parameters)` convenience overload forwards to it without adding a second naming grammar. A third overload accepts a deferred `PartSource<TPart>` built through `StepRef.Then`.

```csharp
var text = new SourceText("example invoice");
var root = await roots.BeginAsync<DocumentExtraction>(
    new CompositionRequest(runId, "input-v1", "demo"));

root.WithHeaderFrom(header.WithParameters(text))
    .WithPartiesFrom(parties, text);

if (detailed)
{
    root.WithLineItemsFrom(lines.WithParameters(text));
}
else
{
    root.WithLineItemsFrom(quickLines.WithParameters(text));
}

root.WithTotalsFrom(root.Steps.LineItems.Then(pinned =>
    totals.WithParameters(
        new TotalsParameters(text.Document, pinned.Value.Count))));

var result = await root.ExecuteAsync();
```

This uses the types in the demo's [Commands.cs](../../research/spikes/Composition.Demo/Commands.cs). `roots` is the injected `IRootCommands` factory; `root` is `CompositionRoot<DocumentExtraction>`. `DocumentExtraction` is the aggregate record with `Header`, `Parties`, `LineItems`, and `Totals` result records. `SourceText` contains the document. `TotalsParameters` contains the document and line count. `CompositionRequest` identifies the run, input revision, and work key, with an optional trial ID. `BeginAsync` opens or reopens a draft; the default store is in memory.

The other receivers are the injected `ExtractHeader`, `ExtractParties`, `ExtractLineItems`, `ExtractQuickLineItems`, and `ExtractTotals` leaf commands. Each is a typed `PromptCommand<TParameters, TResult>` subclass. `detailed` is a normal application condition. Generated `AddLlmCommandsFromComposition_Demo(runtime)` registration wires the roots, leaves, aggregate metadata, and default composition services; the demo's [Program.cs](../../research/spikes/Composition.Demo/Program.cs) shows the full setup.

`Steps.LineItems` is a `StepRef<LineItems>` reference to planned work, not the line-items value. Its callback receives `Pinned<LineItems>`, containing the exact stored reference and the value read from that JSON. `Then` returns a deferred totals source; it does not run the totals call during registration. Ordinary `if` branches choose planned work now; conditions inside the callback can use the prerequisite later. This differs from `Outcome<T>.Bind`, which chains already available success/failure outcomes without creating slot membership.

## Companion operations

Keep one public name per operation. Do not ship IntoHeader, FillHeader and WithHeaderFrom as aliases.

- `WithHeaderFrom(invocation)` registers a producer for an unassigned part.
- `ReuseHeader(pinnedHeader)` explicitly selects an exact stored output version without another model call. The runtime rereads and authorizes the reference; a caller-supplied value alone is not storage evidence. `ReusePart` is the generic counterpart.
- Replacement and revision are not implemented. `ReplaceHeaderFrom(invocation)` was a possible future spelling, not a callable method or a committed API. Expected-version/conflict and human-patch policies must be designed first.
- `ExecuteAsync` freezes and executes the composition. Do not add Run or Build as synonyms for execution.

The earlier lower-level leaf form, `call.IntoSlot(destination).ExecuteAsync(ct)`, remains a historical proposal, not a shipped escape hatch or alias. Its receiver would be the call being directed into a destination. On the implemented root receiver, `WithHeaderFrom` names the root member and its source.

## Review discussion

The existing DMTS, System Architect and End-User Customer Advocate reviewed full call sites independently. The lead then circulated the competing recommendations and challenged immediate-value wording and assumptions about mutable builders.

- **DMTS:** initially preferred FillHeader. Changed to WithHeaderFrom because Fill suggests the value is already present when registration returns. Prefers the explicit source word. Suggested plain WithHeader for stored reuse; the lead instead recommends the explicit ReuseHeader verb.
- **System Architect:** initially preferred IntoHeader. Changed to WithHeader after considering receiver direction and an actual mutable .NET With builder. Continues to prefer the shorter spelling, with a separate ReuseHeader operation to identify stored outputs. Does not consider From necessary.
- **Customer Advocate:** initially preferred HeaderFrom. Changed to WithHeaderFrom because grouping generated methods under With helps discovery and From explains the command argument. Recommends Part for the generic fallback.
- **Lead:** recommends WithHeaderFrom and WithPartFrom. The extra source word is useful when reading unfamiliar call sites; the argument may be a configured invocation or a deferred step reference, and no output value exists yet. This is a reasoned preference, not measured usability evidence.

## Alternatives

- **IntoHeader:** direction is less clear on a root receiver; the root is not itself being moved into Header. It would remain natural on the proposed leaf `IntoSlot` form.
- **WithHeader:** concise and viable. The architect's preferred alternative. The local API must explain that the root handle mutates, while leaf WithParameters/WithProfile return copies.
- **HeaderFrom:** clear source/destination relationship and shorter. Generated methods spread across property-name prefixes rather than one completion group.
- **WithPartFrom:** appropriate generic fallback, with a typed destination. Do not generate verbose WithHeaderPart or WithHeaderComponent names when the domain member already identifies the part.
- **WithComponent:** technically understandable but longer and less plain than Part. Component remains useful in architecture and telemetry documentation.
- **WithChild:** can suggest execution-tree ownership or trace parentage. Stored provenance and dependencies can cross traces and do not imply one parent/child tree.
- **WithComposite:** describes the whole being assembled, rather than its individual named parts. Nested aggregates can use their domain member names without another composition grammar.
- **FillHeader:** can suggest immediate materialization. Registering the producer does not mean that Header exists yet.
- **AddHeader:** can suggest multiple entries. The current contract is one selected source per named destination. Collection append semantics are outside this review.
- **BindHeader:** competes with functional Bind and parameter/DI binding terminology.

## Semantics that naming must not change

Registration changes the same construction handle, returns it for chaining, and runs no model call. Discarding that return value is valid. Author the plan on one flow; runtime synchronization does not make competing branch decisions deterministic. First execution freezes its plan and memoizes the operation/result. Repeated execution of the same handle is not a retry. Registration after execution starts throws. Leaf invocations remain immutable. Duplicates become configuration failures, not silent replacements. Dependent work receives exact pinned outputs. Required-part coverage and conflict checks remain runtime responsibilities.

The root returns `CompositeOutcome<TWhole>` with per-step evidence. It does not collapse a failed leaf into a generic root error: available `ExecutionFailure`, snapshot, delivery, and captured context remain on the step. The shared `LlmEvent` observer contract carries execution and composition events. Root-only and skipped/reused operations have no invented model execution ID. Emitters use captured context rather than reading their own ambient scope; coverage of all paths belongs to runtime verification, not the naming review.

The implemented example uses a fake provider, an in-memory ledger, and an in-memory composition store. It has no distributed leases, crash recovery, or exactly-once spending guarantee. Deferred callbacks are not serialized jobs. The [implementation/design reconciliation](composite-outputs.md) keeps these limits separate from the broader durable-composition proposal. The reflection-disabled aggregate precompile demo exists; this documentation update did not run builds or tests.

With does not universally mean an immutable copy in .NET. ASP.NET Core's WithName delegates to WithMetadata, which registers an endpoint convention and returns the same builder. This is a useful precedent, not proof that all callers will understand our API without documentation. The local immutable-leaf versus mutable-root distinction remains real.

Primary naming references: [ASP.NET Core endpoint builder implementation](https://github.com/dotnet/aspnetcore/blob/main/src/Http/Routing/src/Builder/RoutingEndpointConventionBuilderExtensions.cs), [Framework word-choice guidance](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/general-naming-conventions). These sources inform the review; they do not select the LlmCmd vocabulary.
