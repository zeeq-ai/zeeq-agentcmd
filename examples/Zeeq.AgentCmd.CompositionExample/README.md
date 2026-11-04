# Four-pass composition example

This application uses the actual source library. It builds one `DocumentExtraction` from four independently produced parts, with an ordinary C# branch and an exact stored-result dependency.

```sh
mise run demo:composition
mise run demo:composition --quick
```

The model client is offline. Reflection serialization is disabled. The same source is also copied outside the repository and built through a NuGet-only reference by `mise run test:package`.

## Types and registration

[Commands.cs](Commands.cs) declares `DocumentExtraction`, its `Header`, `Parties`, `LineItems` and `Totals` records, parameter records, and five typed commands. The detailed and quick line-item commands both return `LineItems`. `ExtractTotals` accepts `TotalsParameters`, which includes the prerequisite's exact line count. `header_data` demonstrates a JSON name different from the CLR property.

[Program.cs](Program.cs) supplies an `IChatClient`, a model profile and a trusted actor accessor through `AddAgentCmd`. `AddGeneratedCommands` registers all source-declared leaf commands and the aggregate descriptor. DI supplies `IRootCommands`, the shared ledger and the typed commands. The default store and ledger are in memory.

## Deferred work and branches

`BeginAsync<DocumentExtraction>` opens the bounded draft with a run, immutable input revision and idempotency key. `WithHeaderFrom` and the other generated registration methods mutate that same handle and return it. They do no model work. Ordinary `if/else` chooses which line-item invocation to register.

`root.Steps.LineItems` is a typed reference to planned work, not its value. Its `Then` callback runs after line items attach and receives `Pinned<LineItems>`: value plus exact stored output reference. The dependent does not execute line extraction again. Its invocation contract is checked before the dependent spends; the prerequisite has already run at that point.

`ExecuteAsync` freezes and checks the plan, performs authorized claims before provider calls, runs eligible steps, attaches stored outputs and publishes the aggregate. A second call on the same handle shares the result. Registration after freeze throws. Duplicate registrations before freeze become a configuration failure. These rules do not imply restart recovery or exactly-once provider billing.

## Outcomes and provenance

The application verifies a `CompositeOutcome<DocumentExtraction>.Published` result, exact member JSON names and caller location, four model calls, one dependency callback, and replay without further work. Other root cases are `Incomplete`, `Conflict`, `Unavailable`, `ConfigurationFailure` and `Cancelled`. Their step evidence explains failed, skipped, pending or conflicting work. See [CompositeContracts.cs](../../src/Zeeq.AgentCmd/Commands/Composition/CompositeContracts.cs).

A leaf returns `Outcome<T>.Success` or `Outcome<T>.Failed`. A failed step retains the original non-generic `ExecutionFailure`, snapshot, delivery receipt and captured context. Skipped or callback-failed work has no invented model execution ID.

The aggregate's producer is `ProducerRef.Composition(DraftId)`. The example reads that exact aggregate version from the ledger and deserializes it with the registered generated contract. It evaluates the whole across all four parts, then grades the whole at the root JSON pointer. It repeats the same grade event ID and checks its replay receipt. Reading and grading make no provider calls. The aggregate never borrows a leaf execution ID.

The in-memory defaults, lack of recovery/leases, null-part rule and other boundaries are recorded in the [library README](../../README.md) and [foundation decisions](../../docs/architecture/library-foundation.md).
