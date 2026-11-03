# Four-pass document extraction

This executable example assembles four typed command results into one stored `DocumentExtraction`. Application code registers work across statements and a normal `if` branch. `ExecuteAsync` runs the plan and publishes an aggregate only when its required parts are stored. The example then grades an exact field of that aggregate version.

The provider and storage are local test implementations. No credentials, Docker, or live model are needed.

## Read and run

Start with [Commands.cs](Commands.cs) for the records and command declarations, then [Program.cs](Program.cs) for DI setup, registration, execution, and grading. [Composition.Demo.csproj](Composition.Demo.csproj) enables C# 15, disables reflection serialization, references the generator and runtime, and imports the serializer precompile target.

Run from the repository root with its pinned .NET 11 SDK:

```sh
/tmp/opencode/llmcmd-dotnet11/dotnet run --project spikes/Composition.Demo/Composition.Demo.csproj
/tmp/opencode/llmcmd-dotnet11/dotnet run --project spikes/Composition.Demo/Composition.Demo.csproj -- --quick
```

The first command chooses `ExtractLineItems`. `--quick` chooses `ExtractQuickLineItems`. Either route makes four fake provider calls: header, parties, line items, and totals. A first normal build creates the serializer context needed by design-time builds.

## The types you author

The aggregate is an ordinary partial record with a stable composition ID and revision:

```csharp
[CompositeOutput("invoice.composite", Revision = 1)]
public sealed partial record DocumentExtraction(
    [property: JsonPropertyName("header_data")] Header Header,
    Parties Parties,
    LineItems LineItems,
    Totals Totals);
```

Each property declares one destination slot. All four slots are required in this example. Their result records keep the sample small: `Header` contains the invoice number, `Parties` contains the seller, `LineItems` contains a count, and `Totals` contains an amount. `LineItems` is deliberately a count rather than a complete invoice-line schema.

`SourceText(string Document)` is the input record shared by the independent passes. `TotalsParameters(string Document, int LineCount)` adds the count produced by the selected line-items pass. Parameters remain separate from prompt templates.

The five command classes in `Commands.cs` derive from the runtime's `PromptCommand<TParameters, TResult>`:

- `ExtractHeader` maps `SourceText` to `Header`.
- `ExtractParties` maps `SourceText` to `Parties`.
- `ExtractLineItems` and `ExtractQuickLineItems` both map `SourceText` to `LineItems`; the branch selects one.
- `ExtractTotals` maps `TotalsParameters` to `Totals`.

A leaf declaration contains the stable command ID and template, for example:

```csharp
public sealed partial class ExtractHeader : PromptCommand<SourceText, Header>
{
    public const string Id = "example.header";
    public const string UserTemplate = "HEADER {Document}";
}
```

The generator supplies construction, rendering, parameter projection, contracts, and registration. The imported precompile target supplies serializer-context source before normal source generation. There is no handwritten JSON context or execution wrapper in the demo. The library owns execution; callers do not pass an executor to each call.

CLR member names produce `WithHeaderFrom`, `WithPartiesFrom`, `WithLineItemsFrom`, and `WithTotalsFrom`, plus the typed `Steps` properties. JSON names remain separate: `Header` is the slot name, while `header_data` is its serialized property name. This lets the call site use C# names while the manifest and grading use the actual wire contract.

## Host setup and dependency injection

`Actor` identifies the demo tenant and reviewer. `HumanLedger` creates the run and stores versioned outputs and feedback in memory. `DemoClient`, defined at the end of `Program.cs`, implements MEAI's `IChatClient`; it returns fixed JSON for each prompt and counts calls. Its model name and token counts are synthetic.

`CommandRuntime` binds that provider, the `extract` profile, the ledger, and the actor accessor once. The generated registration method wires both leaf commands and composition services:

```csharp
var actor = new Actor("demo", "reviewer");
var ledger = new HumanLedger();
var runId = ledger.CreateRun(actor);
var client = new DemoClient();
using var runtime = CommandRuntime.Create(() => client,
    new Dictionary<string, Profile> { ["extract"] = new("synthetic-model") },
    ledger, () => actor);

var services = new ServiceCollection();
services.AddLlmCommandsFromComposition_Demo(runtime);
using var provider = services.BuildServiceProvider();
var roots = provider.GetRequiredService<IRootCommands>();
var header = provider.GetRequiredService<ExtractHeader>();
var parties = provider.GetRequiredService<ExtractParties>();
var lines = provider.GetRequiredService<ExtractLineItems>();
var quickLines = provider.GetRequiredService<ExtractQuickLineItems>();
var totals = provider.GetRequiredService<ExtractTotals>();
```

`AddLlmCommandsFromComposition_Demo` comes from the assembly's generated catalog in `LlmCmd.Generated`. It validates generated contracts and default profiles, registers the leaf commands, and registers the aggregate descriptor. Composition uses the same runtime ledger and actor accessor by default. It also gets a default `InMemoryCompositionStore` for drafts, claims, and membership, and a registry of known aggregate definitions.

`IRootCommands` is the injected factory for bounded composition handles. `CompositionRoot<DocumentExtraction>` is the returned handle; the application does not write an orchestration subclass. Resolving services directly makes this console example self-contained. An application can constructor-inject `IRootCommands` and the same leaf command types.

## Register work, then execute

This is the authoring flow from `Program.cs`, with its assertion counter omitted:

```csharp
var text = new SourceText("example invoice");
var root = await roots.BeginAsync<DocumentExtraction>(
    new CompositionRequest(runId, "input-v1", "demo"));

root.WithHeaderFrom(header.WithParameters(text))
    .WithPartiesFrom(parties, text);

var detailed = !args.Contains("--quick", StringComparer.Ordinal);
if (detailed) root.WithLineItemsFrom(lines.WithParameters(text));
else root.WithLineItemsFrom(quickLines.WithParameters(text));

root.WithTotalsFrom(root.Steps.LineItems.Then(pinned =>
    totals.WithParameters(
        new TotalsParameters(text.Document, pinned.Value.Count))));

var outcome = await root.ExecuteAsync();
```

`CompositionRequest` contains the run ID, input revision, composition key, and an optional trial ID. Together with the tenant and aggregate definition, these identify the draft to open or reopen. `input-v1` is an application-supplied revision label; the demo does not calculate it from the document. Opening a draft assigns a `DraftId` distinct from the run and trace IDs. The engine carries that draft identity into component execution and attempt context and the resulting records.

`WithParameters` returns an immutable `Invocation<TParameters, TResult>`. Leaf configuration such as `WithProfile` and `Configure` returns another invocation and preserves both type arguments. In contrast, the root's `With…From` methods **change the same construction handle** and return it. Registration prepares the call inputs but makes no model call. The command-and-input overload, used for parties above, is shorthand for passing `parties.WithParameters(text)`.

`With…From` names the destination and says where its result will come from. It assigns a single slot; it does not append to a collection or replace an earlier assignment. Registering the same slot twice becomes a configuration failure at execution. This is why the two line-items registrations are in mutually exclusive branches.

`root.Steps.LineItems` is a `StepRef<LineItems>`, not a result value. `Then` produces a `PartSource<Totals>`: deferred instructions for building the totals invocation. Its callback runs after the line-items output is stored. The callback receives `Pinned<LineItems>`, which contains the exact `OutputRef` and a typed value read from that stored JSON. It uses the stored count to construct real `TotalsParameters`; it does not execute the totals command itself. A failed or unstored prerequisite prevents the dependent call from starting.

At `ExecuteAsync`, the handle freezes its registrations. The runner checks missing required slots, duplicates, and dependency cycles before provider spending. It claims each slot before running its command. Independent calls can run in parallel; totals waits for line items. Publication assembles stored component JSON and validates it through the aggregate contract, so an unstored in-memory result cannot fill a slot.

The runner checks known leaf serializer contracts against the aggregate before any claim. A deferred `Then` invocation exists only after its prerequisite is stored, so its contract is checked then, before that dependent call spends. Incompatible contracts are refused instead of rewriting the stored component JSON. Custom converter configurations can be conservatively rejected. An optional slot may be absent; attaching a JSON-null part is unsupported.

Registration also freezes the selected profile, model, effective settings, and requested fallback route. Reopening the same draft after a route change reports a conflict. It does not silently combine parts from different model configurations. Command defaults run once for each prepared call.

Repeating `ExecuteAsync` on this handle shares the existing operation and result. It does not retry failed work. Registering after execution starts throws because the plan is frozen. A later handle can reopen the same draft and retry eligible failed slots while retaining attached outputs. An unresolved `Pending` claim is not treated as permission to spend again. Generated `Reuse{Slot}` methods can select an exact existing `Pinned<T>` output for an unassigned slot; the runtime rereads and authorizes the reference. There is no replace or revise API yet.

## Read outcomes without losing failures

The demo expects `CompositeOutcome<DocumentExtraction>.Published` and throws if its assertions fail. Application code should inspect the other cases defined in [CompositeContracts.cs](../CommandRoot/Runtime/Composition/CompositeContracts.cs):

- `Published` contains the typed `Value`, `DraftId`, exact `AggregateRef`, component `Members`, and per-slot `Steps`.
- `ConfigurationFailure` contains wiring or request problems. A missing required registration is reported here before this execution spends on providers.
- `Incomplete` means required work remains unfilled. Already attached outputs remain available in the draft.
- `Conflict` means stored facts disagree with the request, or the selected parts cannot form a valid aggregate.
- `Unavailable` reports a storage or engine failure. It can include unresolved work; it does not establish whether a provider spent tokens.
- `Cancelled` reports cancellation and the available step results.

Each `StepResult` identifies its slot and has a `Status`. `Attached`, `Replayed`, and `Reused` distinguish new execution, an existing attachment, and explicit attachment of a pinned output. `Failed`, `Cancelled`, `Skipped`, `Pending`, and `Conflict` describe unfinished work. `Skipped` means a prerequisite did not attach. `Pending` means a claim or its completion is unresolved; do not blindly rerun it.

A direct leaf returns `Outcome<T>.Success` or `Outcome<T>.Failed`. The latter carries a non-generic closed `ExecutionFailure`: invalid input, provider failure, hook failure, configuration failure, invalid output, or cancellation. `Map`, `Bind`, and `BindAsync` forward the exact failure object when the source failed. Composition preserves that failure object on `StepResult.Failure`, alongside the available snapshot, delivery result, and captured context. Callback and dependency errors instead have a step `Code` and may have no leaf failure or execution ID because no command ran. See [Outcomes.cs](../CommandRoot/Runtime/Outcomes.cs).

Even leaf success is not proof of storage. `OutputDelivery.Stored` is required for attachment; rejected or failed delivery leaves the slot unfilled. This distinction prevents an aggregate from claiming evidence that the ledger cannot supply.

For hosts that configure an event observer, the shared `LlmEvent` contract includes `ExecutionEvent` and composition events. Each carries captured context; an emitter does not read its delivery worker's ambient scope. Root-only operations and skipped/reused steps do not invent execution IDs. This demo does not configure an external observer or establish complete event-delivery coverage. The observer remains separate from the awaited journal that gates provider sends.

## Grade the exact aggregate version

`Published.Aggregate` is an `AggregateRef` with its own composition producer identity and an exact `OutputRef`. `Published.Members` records the exact component versions used. A composition does not borrow a leaf's execution ID or infer membership from shared trace tags.

The grading code uses the published reference directly:

```csharp
var outputRef = published.Aggregate.Output;
using var expected = JsonDocument.Parse("12.50");
var gradeRequest = new GradeRequest(
    Guid.NewGuid(), outputRef, "/Totals/Amount",
    expected.RootElement, 1.0, "Matches the invoice");
var grade = ledger.RecordGrade(gradeRequest, actor);
var gradeReplay = ledger.RecordGrade(gradeRequest, actor);
```

Here, `published` is the `Published` case matched in `Program.cs`. `GradeRequest` supplies an event ID, exact output version, JSON Pointer, expected field value, score, and rationale. `JsonDocument` supplies the expected JSON number. `LedgerResult<GradeReceipt>.Accepted` contains the recorded grade receipt and indicates whether the event was replayed. Sending the same request again is idempotent.

`/Totals/Amount` addresses this serializer's actual property names. Grading the header number would use `/header_data/Number`, not `/Header/Number` or a dot path. The target never means “latest output.” An aggregate grade remains an aggregate grade; it does not automatically grade the contributing component outputs or alter their original JSON.

## What the executable checks, and its limits

`Program.cs` contains assertions for reflection-disabled startup, no provider calls during registration, four stored members, the renamed header field, the application caller location, one plan execution per handle, one dependency callback, composition producer identity, and exact aggregate grading with idempotent replay. Its success message is printed only after those assertions pass. Run both commands above to exercise both branches.

The provider is fake. Both `HumanLedger` and `InMemoryCompositionStore` lose their state when the process ends. This example provides no crash recovery, distributed lease guarantee, exactly-once provider spending guarantee, queue worker, or live provider validation. Deferred callbacks are in-process C# delegates; this example does not serialize or resume them across requests or process restarts. The store abstraction and journal are extension points, not proof of production durability.
