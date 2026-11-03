# Generated command root

Start with [CommandRoot.Demo](../CommandRoot.Demo/README.md). Its application uses ordinary parameter/result records and a `sealed partial` class with a stable `Id` and `UserTemplate`. It has no command attribute, serializer context, `JsonRequired` annotations, descriptor, constructor or public executor. The host registers the generated commands once; application code calls the injected command.

```csharp
public sealed record InvoiceParameters(string Document, string Locale = "en");
public sealed record Invoice(decimal Total, string Currency, int Count = 3);

public sealed partial class ExtractInvoice : PromptCommand<InvoiceParameters, Invoice>
{
    const string Id = "invoice.extract";
    const string UserTemplate = "Extract an invoice from {Document} ({Locale}).";
}

// extract is injected. TResult remains Invoice throughout the chain.
Outcome<Invoice> result = await extract
    .WithParameters(new InvoiceParameters(document, Locale: "en"))
    .ExecuteAsync(ct);
```

## Build and registration

Import [build/LlmCmd.Precompile.targets](build/LlmCmd.Precompile.targets) into the consumer project and reference the runtime and generator. Before `CoreCompile`, the precompile tool discovers source-authored direct `PromptCommand<TParameters,TResult>` subclasses and composite output declarations. It writes the app's serializer context as a normal `Compile` item. STJ and LlmCmd then see that context in the same compiler input. Ordinary source generators do not need to consume each other's output.

The LlmCmd generator emits the private descriptor, scalar template renderer, JSON-name projector, required-field contract policy, typed validators, definition hash, constructor and command registrations. It does not call the executor. The shared runtime library owns execution and the private-constructor immutable `Invocation<TParameters,TResult>`.

Each assembly gets a registration extension in `LlmCmd.Generated`. For the demo assembly, use `services.AddLlmCommandsFromCommandRoot_Demo(runtime)`. This registers the supplied runtime and generated command factories, validates all JSON contracts and checks command default profiles against that runtime before the host starts. No reflection scan is used. `AddLlmCommands<TCatalog>()` remains available for hosts that register the runtime separately; that overload checks contracts only, so profile resolution remains a per-call check. When a runtime factory resolves a ledger, actor accessor or observer from DI, register those host dependencies explicitly before command registration. The fallback services forward to an already configured runtime; resolving them from that runtime's own factory would form a cycle.

When the assembly declares composite outputs, the same registration includes their descriptors. The default composition services use the command runtime's ledger and actor accessor, an in-memory composition store, and `IRootCommands`. The [four-pass demo](../Composition.Demo/README.md) uses `AddLlmCommandsFromComposition_Demo(runtime)` to wire its roots and leaves together.

Revision defaults to 1. The system message is omitted when `SystemTemplate` is absent. With one configured profile, the host selects it automatically. With multiple profiles, supply `defaultProfile: ProfileKey` when creating the runtime or a command default. Missing defaults fail before model work; the recommended registration overload also catches them during setup. The legacy `[PromptCommand]` input and `const string Profile` remain compatibility paths. The minimal example does not require either.

## JSON contract rules

One modified serializer contract supplies deserialization, schema generation and parameter mapping. Generated executable assignments to `JsonPropertyInfo.IsRequired` replace repetitive annotations. Presence and nullability are separate:

- A non-optional constructor parameter requires its JSON member, including nullable and value-type parameters.
- An optional constructor parameter retains its default when the member is absent.
- A C# `required` member or an existing `JsonRequired` annotation requires presence.
- A public settable non-nullable reference with no matching constructor parameter also requires presence. This convention applies even if the property has an initializer.
- A nullable type permits explicit null. It does not make a non-optional constructor parameter optional.
- `JsonIgnore(Always)` excludes a property. A placeholder that names it is a generator error.

The graph covers nested DTOs, arrays, dictionary/enumerable values and declared JSON-derived types. Generated validation checks non-nullable collection values that STJ's nullable checking does not cover. The same CLR generic type cannot carry conflicting nullable reference contracts in one command, such as both `Box<string?>` and `Box<string>`; the generator diagnoses this instead of reusing the wrong validator.

For custom naming or ignore behavior, supply one authored context through `[PromptCommand("id", JsonContext = typeof(AppJson))]`. It must declare both root types. Context property aliases are supported. Names come from `JsonPropertyName` or the configured naming policy; `ParameterRecord` carries both the CLR `Name` and serialized `JsonName`. Projection respects serializer omission rules. Result contracts and their nested metadata are checked during generated registration and again through a cached validation boundary before execution. At execution, invalid contracts return `Outcome<T>.Failed` carrying `ExecutionFailure.ConfigurationFailure`, not a projection-hook failure.

The ledger stores the original parsed wire JSON after typed validation, including unknown or ignored fields. Validation guarantees apply to the modeled fields; they do not validate those extra fields. This preserves exact response content for grading. An explicit context can choose `UnmappedMemberHandling = Disallow` to reject unknown members. Production collection still needs an explicit retention and access policy. Schema generation is not validation of every JSON Schema constraint.

Optional constructor fields use a normal JSON schema. A provider's strict-output mode may require a different schema policy. Strict-provider adaptation is not implemented, and changing a profile does not silently change requiredness.

## Customization and execution

An optional authored `static partial void ConfigureDefaults(ChatSettingsPatch defaults)` body supplies command defaults. Its defining declaration and wrapper are generated. The runtime invokes the hook inside a guarded boundary; the static descriptor does not execute user configuration. Explicit call settings win over profile settings, which win over hook defaults. `Configure` and profile selectors run once and retain only their values; callback errors become deferred terminal failures.

Use the generated [profile catalog](ProfileExamples.cs) for `WithProfile<Profiles>(p => p.OpenAI)` or `p.Anthropic`. The same command can use different provider platforms, retry policies and fallback through [ProfileRouting](../ProfileRouting/README.md). The top-level snapshot's temperature describes the initial selected profile; each routing attempt records the settings actually sent, including fallback settings.

`ExecuteAsync` captures caller information and the effective [operation context](../ContextPropagation/README.md) at entry. If no run is supplied, the runtime creates one for the actor resolved for that call. Every outcome carries an execution ID and captured context. Snapshots can be absent on early failures. Model success and output delivery are separate: ledger failure preserves the typed value. Notification failures do not replace the outcome.

The [outcome contract](Runtime/Outcomes.cs) is `Outcome<T>.Success` or `Outcome<T>.Failed(ExecutionFailure)`. `ExecutionFailure` is a non-generic closed record with cases for invalid input, provider failure, hook failure, configuration failure, invalid output, and cancellation. It carries the original context and optional snapshot. Callers match the outer success/failure once, then inspect the failure case when they need its details.

`Map`, `Bind`, and `BindAsync` forward the same failure object when the source failed; they do not reconstruct it for each result type. `Map` changes the application value without writing a new artifact: its receipt still identifies the original stored output. `Bind` calls its next function only on success. These value combinators do not catch application exceptions or assemble an aggregate manifest. Composition uses the distinct root and step outcomes described below.

The shared observer interface, `IExecutionEventSink.EmitAsync(LlmEvent, ...)`, accepts both `ExecutionEvent` and `LlmCmd.ContextPropagation.CompositionEvent`. Each event carries captured context; a delayed sink does not read `AsyncLocal` or recover identity from span tags. A composition root or skipped/reused step is not a model call, so its composition event context has a nullable `ExecutionId`. Only an associated real leaf execution supplies that ID. This best-effort observer is separate from the awaited `IExecutionJournal` that gates provider sends. The shared event contract does not itself guarantee durable delivery or complete emitter coverage.

The review helper in [Examples.cs](Examples.cs) accepts caller-created event IDs for grades and corrections. Reuse those IDs on retries. The ledger retains the exact target version; corrections do not mutate the original.

Definition hashes include the command ID/revision, templates, reachable type/property shapes, requiredness, constructor defaults and declared serializer policy. They exclude unrelated serializer-context roots. Hash inputs use explicit typed values rather than `AttributeData.ToString()`. This is a prototype definition fingerprint; it does not fingerprint arbitrary hook or converter implementation bodies, runtime configuration or the provider's behavior.

Composition separately freezes the resolved call configuration at registration: the selected profile, model, effective temperature, command-default values, and the router's requested fallback/retry route. Its invocation fingerprint includes those values. Reopening a draft after a profile remap reports a conflict instead of silently replaying an old-model part. `ConfigureDefaults` runs once for that prepared call, and execution uses its captured result. Provider factory and classifier implementation bodies remain host-versioned behavior.

## Deferred composition

The [composition demo](../Composition.Demo/README.md) implements four typed passes, an ordinary C# branch, a stored-result dependency, and grading of the exact aggregate version. Its [Commands.cs](../Composition.Demo/Commands.cs) declares the input/result records and partial leaf commands. Its [Program.cs](../Composition.Demo/Program.cs) shows the full setup and runs with reflection serialization disabled.

`IRootCommands.BeginAsync<TWhole>(CompositionRequest)` opens or reopens a draft and returns `CompositionRoot<TWhole>`. Generated `With{Slot}From` methods accept a configured immutable invocation, a command plus its parameters, or a deferred `PartSource<TPart>`. These methods change the same root construction handle and return it. They register work; no provider call runs until `ExecuteAsync`. `WithPartFrom` is the generic fallback with an invariant typed `PartSlot<TWhole,TPart>` destination.

Use normal statements and branches to select work. A generated property such as `root.Steps.LineItems` is a `StepRef<LineItems>`. Its `Then` callback receives `Pinned<LineItems>` after the exact prerequisite version is stored and returns the dependent invocation. Generated `Reuse{Slot}` methods instead select an existing pinned output; the runtime rereads and authorizes that reference. Neither path trusts a caller-supplied value as evidence of storage.

First execution freezes the root plan. Repeated calls on that handle share the operation and its result; they are not retries. Registration after execution starts throws. Duplicate and missing required registrations are configuration failures. Attached slots are not silently replaced. There is no replace or revise API yet.

The terminal type is `CompositeOutcome<TWhole>`, with `Published`, `Incomplete`, `Conflict`, `Unavailable`, `ConfigurationFailure`, and `Cancelled` cases. `Published` contains the typed value, exact aggregate reference, member references, and step results. Each `StepResult` reports attachment, replay, reuse, failure, cancellation, pending work, a skipped dependency, or conflict. When a leaf fails, the step preserves its `ExecutionFailure`, snapshot, delivery, and captured context rather than flattening them into a root error string. A parsed leaf value whose delivery was not stored cannot fill a slot.

The composition store and human ledger in the demo are in memory. There are no distributed leases, crash recovery, or exactly-once spending guarantees. Deferred C# delegates are not serialized jobs. See [the reconciled design](../../docs/research/composite-outputs.md) for current behavior, historical alternatives, and production gates.

## Verification and limits

```sh
/tmp/opencode/llmcmd-dotnet11/dotnet build tests/LlmCmd.CommandRoot.Tests/LlmCmd.CommandRoot.Tests.csproj
TUNIT_ENABLE_JUNIT_REPORTER=1 /tmp/opencode/llmcmd-dotnet11/dotnet run --no-build --project tests/LlmCmd.CommandRoot.Tests/LlmCmd.CommandRoot.Tests.csproj -- --junit-output-path /tmp/opencode/llmcmd-commandroot/tests.xml
DOTNET_HOST_PATH=/tmp/opencode/llmcmd-dotnet11/dotnet python3 tests/LlmCmd.CommandRoot.Tests/compile-negative.py
DOTNET_HOST_PATH=/tmp/opencode/llmcmd-dotnet11/dotnet python3 tests/LlmCmd.CommandRoot.Tests/generation-proof.py
python3 tests/PrecompileProof/run.py
```

The demo and isolated generation proof disable reflection-based JSON serialization. This verifies source-generated metadata use; it is not a NativeAOT publish test. Tests use fake MEAI clients and make no hosted provider calls.

Composition runtime and compiler checks are in [LlmCmd.Composition.Tests](../../tests/LlmCmd.Composition.Tests) and [LlmCmd.Composition.Compile](../../tests/LlmCmd.Composition.Compile). Replay, contract compatibility, authorization, and shared-event integration are under active review. The API descriptions here do not certify those latest changes as passing; this documentation reconciliation ran no builds or tests.

The precompile tool is pinned to SDK `11.0.100-rc.3.26522.148`. A first normal build is needed for IDE metadata. Design-time builds reuse the last context without regenerating it; IDE-specific behavior is unverified. Precompilation rebuilds the tool and analyzes sources on each normal build, although unchanged context output is not rewritten. Commands emitted by another generator in the same compiler run are not discovered. Full incremental caching, multi-targeting and packaging remain open.

Other limits remain: arbitrary converters/extension data and complex constructor policies need dedicated support; custom object converters fail contract validation rather than being silently interpreted. Validation errors do not yet include field pointers. Composition prepares observable inputs when an invocation is registered; that does not prove every mutable/custom-converter or conditional-omission case. Strict provider schemas, native SDK classifiers, streaming/tool routing, persistent composition, shutdown and durable event delivery remain production gates. The separate runtime, human ledger, propagation and provider extraction spikes are not one complete production library.
