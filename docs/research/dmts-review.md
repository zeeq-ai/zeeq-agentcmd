# DMTS review: command-root typed prompt API (LlmCmd)

Historical review. Precompile discussion below describes the archived spikes.
The maintained library now emits serializer metadata directly; see
[serialization and NativeAOT](../architecture/serialization-and-aot.md).

Status: design review, copied by the lead from the DMTS review artifact. Recommendations are distinct from implemented scope; the CommandRoot README and test reports describe the current proof.
Inputs read: `spikes/CommandRoot/*`, `spikes/TypedCommands/{Generator/PromptGenerator.cs,Sample/Program.cs}`, `spikes/ArbitraryOrder/*`, `docs/research/design.md`, `call-pattern-feedback.md`, heading scan of `fluent-techniques.md`, `collector-adapters.md`, `human-grading.md`.

## 1. Verdict

The current `CommandRoot` proof has the correct *typed core*: unbound → bound transition, closed `TResult` propagation, immutable settings, and caller attributes on the outer terminal method. Its *public shape* is wrong for the stated requirements:

- `ExecuteAsync(executor)` exposes the executor at every call site.
- `ExtractInvoice.Create()` is a hand-written static factory with a hand-written descriptor, lambdas, and a JSON schema string.
- `Command<TP,TR>` is a public positional record. Its `with` and public ctor let callers build a "bound" command with any parameters/settings, bypassing `WithParameters`.

Recommended target: **a user-authored `sealed partial class` that derives from a library base `PromptCommand<TParameters,TResult>`. The generator supplies only static descriptor data. DI (or one explicit runtime object) activates the class. The runtime handle is opaque. Execution stays inside the library assembly.**

## 2. Key constraint the brief misses: "internal executor" vs generated code

Generated code compiles into the **consumer** assembly. It cannot call `internal` members of `LlmCmd.Runtime`. If the generator emits `runtime.ExecuteAsync(...)`, that API must be public, and the executor is no longer hidden.

Resolution: generate **data**, not execution.
- The library owns `abstract class PromptCommand<TP,TR>` with a private runtime field. It also owns `WithParameters`, `Invocation<TP,TR>`, and `ExecuteAsync`. All executor calls stay internal to the library.
- The generator implements one static abstract member, `static PromptDescriptor<TP,TR> IPromptDescriptorSource<TP,TR>.Descriptor`. The descriptor holds id, revision, templates, compiled renderer, parameter projector, result `JsonTypeInfo`, hashes and hook delegates.
- The only public runtime type is `CommandRuntime`, an opaque sealed class. It has no public execute members. It is passed to the generated ctor.

This hides the executor through the type system, not through convention, and it uses no ambient service locator.

## 3. Recommended public shape

### User code (all that a developer writes)

```csharp
public sealed record InvoiceParameters(string Document, string Locale = "en");
public sealed record Invoice([property: JsonRequired] decimal Total, [property: JsonRequired] string Currency);

[PromptCommand("invoice.extract", Revision = 3, Profile = "extraction")]
public sealed partial class ExtractInvoice : PromptCommand<InvoiceParameters, Invoice>
{
    const string System = "You extract invoices. Reply with JSON only.";
    const string User = """
        Extract the invoice from this document (locale {Locale}):
        {Document}
        """;

    // Optional, detected by the generator. Pure, static, and synchronous.
    static string? Validate(InvoiceParameters p) => string.IsNullOrWhiteSpace(p.Document) ? "Document is required" : null;
    static string? Validate(Invoice r) => r.Total < 0 ? "Total must be non-negative" : null;
}

[JsonSerializable(typeof(InvoiceParameters)), JsonSerializable(typeof(Invoice))]
internal partial class AppJson : JsonSerializerContext;   // see §5: required, checked by diagnostic
```

### Host setup (once)

```csharp
builder.Services.AddLlmCommands(llm => llm
    .AddCommandsFromThisAssembly()                       // generated, no reflection scan
    .AddProvider("openai", sp => new OpenAIClient(...).GetChatClient("gpt-x").AsIChatClient(), owned: true)
    .AddProfile("extraction", p => p.Provider("openai").Model("gpt-x").Temperature(0))
    .UseChatPipeline(b => b.UseFunctionInvocation())     // MEAI ChatClientBuilder, built once per provider
    .AddSink<OtlpExecutionSink>()                        // sinks ≠ providers, separate registries
    .AddOverrideSource<RedisSnapshotResolver>());        // optional runtime overrides, validated separately
```

### Call site (common path is terse)

```csharp
public sealed class InvoiceService(ExtractInvoice extract)
{
    public async Task<Invoice?> ReadAsync(string doc, Guid runId, CancellationToken ct)
    {
        var outcome = await extract
            .WithParameters(new(doc))          // target-typed new; no executor, no generics spelled
            .WithTemperature(0.1f)             // explicit override beats profile regardless of chain order
            .InRun(runId)
            .ExecuteAsync(ct);                 // caller attrs captured here

        return outcome switch
        {
            Outcome<Invoice>.Success s => s.Value,
            Outcome<Invoice>.InvalidOutput f => LogAndNull(f, f.ExecutionId),
            _ => null,
        };
    }
}
```

### Library types (sketch, not generated)

- `abstract class PromptCommand<TP,TR>`: ctor `(CommandRuntime)`, public `Invocation<TP,TR> WithParameters(TP)`, `static abstract` descriptor via `IPromptDescriptorSource<TP,TR>`. It has no `ExecuteAsync`.
- `sealed class Invocation<TP,TR>`: private ctor and readonly state. **Use a class, not a record**: no public `with`, no copy ctor, and no external construction. Methods: `WithProfile`, `WithTemperature`, `WithMaxOutputTokens`, `ClearTemperature`, `InRun`, `WithExperiment`, `Configure(Action<ChatSettingsPatch>)`, and `ExecuteAsync(CancellationToken ct = default, [CallerMemberName]…, [CallerFilePath]…, [CallerLineNumber]…)`. Also `ExecuteAsync(CallSite site, CancellationToken ct)` for shared helpers.
- Per-command optional members (generated): C# 14 extension blocks on the closed type, for example `extension(Invocation<InvoiceParameters,Invoice> i) { public Invocation<…> WithLocale(string l) => … }`. The generator never needs to emit its own invocation class.
- `Outcome<TR>`: a closed hierarchy (C# 15 `closed`). **Every case carries `ExecutionId`**, so failures are gradable and linkable. Add `GetValueOrThrow()` for scripts and tests.

### Non-DI / tests (no service locator)

```csharp
using var runtime = CommandRuntime.Create(llm => llm.AddProvider("fake", () => fakeClient, owned: true).AddProfile("extraction", p => p.Provider("fake")));
var outcome = await new ExtractInvoice(runtime).WithParameters(new("doc")).InRun(run).ExecuteAsync();
```

## 4. Decisions with tradeoffs

| Question | Recommend | Why / rejected alternative |
|---|---|---|
| Activation | DI-registered singleton per command (stateless; holds runtime ref). Generated `AddCommandsFromThisAssembly()` per assembly | Static `ExtractInvoice.Create()` needs an ambient runtime (rejected). Factories (`ICommandFactory.Get<T>()`) add a hop with no gain. Reflection scan is not AOT/trim safe. |
| Base class vs attribute-only `PromptCommand<TP,TR>` attr | Base class + non-generic attribute (id/revision/profile) | Generic attribute duplicates type args, and the attribute can't give `WithParameters` without generated members. The base class keeps generated output small, so incremental tests stay simple. |
| Template location | `const` members named `System`/`User`/`Assistant*` (raw strings) or attr `Template=` for one-liners | Consts support multi-line raw strings, multiple roles, and IDE navigation. The generator reads `ConstantValue`. |
| Placeholder binding | `{PropertyName}` against **public get-props of TParameters** (records: primary-ctor props) | One source of truth. Typed-`Define` partial method spike becomes obsolete. Same parser shared with runtime override validation (parity gate). |
| Optional params | Record defaults (`Locale = "en"`) for **values**; generated `With<Prop>` extension for overridable values that must track origin (inherited vs explicit) | C# default values lose the "omitted vs explicit default" distinction. Only params marked `[RuntimeOverridable]` need origin tracking. |
| Bound immutability | `sealed class` + private ctor | `sealed record` still exposes `with` on public init props. |
| Settings precedence | Store profile selection and field patches separately; resolve platform → command attr → profile → runtime config → explicit patches at execute | Chain order must not matter (`WithTemperature` then `WithProfile` keeps the temperature). |
| `TResult` = string | Supported: `PromptCommand<TP,string>`, no schema, no ResponseFormat | Common case; don't force records for free text. |

## 5. Anonymous types: do not use as TParameters

- They are `internal` to the declaring assembly and can't be named in `PromptCommand<TP,TR>` or a DI signature. They can't carry attributes, and no generator target can bind to them.
- C# has no structural typing. Two anonymous types with the same shape from different assemblies are different types, so placeholder checks would require reflection at runtime.
- STJ source generation can't include them in a `JsonSerializerContext`. Telemetry/replay deserialization is impossible.
- Acceptable narrow use: a **one-off, untyped ad-hoc prompt** API (`runtime.Adhoc("…{x}…").WithValues(new { x })`) explicitly labelled dynamic, validated at runtime, and excluded from typed eval datasets. Recommend deferring even this.
- Named positional records give the terse `new(doc)` call with full guarantees. They are the answer to "minimal setup".

## 6. Serializers, descriptors, telemetry mapping

Hard constraint: **Roslyn generators do not see each other's output.** Our generator cannot reference `AppJson.Default.Invoice` members produced by the STJ generator in the same pass (it can only emit code that references them by name, compiled later).

Recommendation:
1. The generator emits code that references `AppJson.Default.InvoiceParameters` / `.Invoice` **by name**. It finds the context by scanning user `JsonSerializerContext` partials for `[JsonSerializable(typeof(T))]`. A missing entry gives a diagnostic with a code fix that adds the attribute line.
2. The result JSON schema is built once per descriptor at first use via `AIJsonUtilities.CreateJsonSchema(typeInfo)` and cached. The generator also emits a **schema hash input** (property names/types/required flags from the symbol model) so `CompiledDefinitionHash` is computed at build time.
3. The parameter telemetry projector is generated: a `void Project(TP p, ParameterRecordWriter w)` method with one `w.Write("Document", p.Document, origin)` per property. No reflection. Nested complex property types serialize through the context's `JsonTypeInfo`.
4. The canonical telemetry model stays library-owned (`ExecutionRecord`, `ParameterRecord{Name, TypeId, Value: JsonElement, Origin, OverrideRevision}`). Generated code only fills it, so changing the canonical schema never needs regeneration of user code beyond a version stamp.

## 7. Func / Action / interface customization

Rules: customization resolves to **frozen data before the model call**. The effective settings record stores the *result*, never the delegate. A delegate identity may be recorded only as a declared name (`hook: "ExtractInvoice.Validate"`).

| Scope | Mechanism | Runs |
|---|---|---|
| Per command, compile-time | Optional static members detected by generator: `Validate(TP)`, `Validate(TR)`, `Normalize(TP) → TP`, `ConfigureOptions(ChatSettingsPatch)`. Wrong signature → diagnostic | Validate/normalize: before render. Result validate: after parse |
| Per command, DI-time | `IConfigureCommand<ExtractInvoice>` (options pattern) for environment-specific defaults | Once at runtime build; frozen into the command's default settings |
| Per call | `.Configure(Action<ChatSettingsPatch>)` executes **immediately** at the call, producing a patch value | Bind time; never deferred |
| Cross-cutting | MEAI `ChatClientBuilder` middleware; `IExecutionSink`, `ITokenCostEnricher` | Pipeline / post-finalize |

Prefer static detected members over `partial void` hooks. Partial void methods can't return values, and C# 13+ partial members with return types become mandatory when declared. Detected static methods stay optional and readable, and the generator can check them.

## 8. Generator diagnostics (stable codes; error unless noted)

- LLMCMD001 class not `sealed partial`, nested, generic, or in the global namespace
- LLMCMD002 missing/duplicate `[PromptCommand]`; non-constant id; id fails `^[a-z0-9]+(\.[a-z0-9_-]+)+$`
- LLMCMD003 id contains a version-like suffix (`.v1`): warning, use `Revision`
- LLMCMD004 duplicate prompt id in compilation (cross-assembly check at runtime startup)
- LLMCMD010 malformed template (unclosed/unescaped braces)
- LLMCMD011 placeholder not a public property of TParameters
- LLMCMD012 TParameters property unused by every template (warning; suppressible per property via `[NotRendered]`)
- LLMCMD013 unsupported property type for rendering (no `ISpanFormattable`/string/enum and no `[RenderAs]`)
- LLMCMD014 nullable property rendered without `[RenderNullAs("")]` (warning)
- LLMCMD020 TParameters / TResult missing from any `JsonSerializerContext` (code fix)
- LLMCMD021 TResult is not a closed, serializable shape (interface, abstract without polymorphism attrs, `object`, `dynamic`)
- LLMCMD022 TParameters is mutable (settable props / mutable collections): warning, since telemetry would snapshot at bind time
- LLMCMD030 hook method with wrong signature, non-static, or async
- LLMCMD031 member name collides with library fluent names (`WithProfile`, `InRun`, …)
- LLMCMD040 revision unchanged while `CompiledDefinitionHash` changed versus the baseline file (opt-in, via a `.llmcmd.lock` AdditionalFile): warning

Do not promise analysis of arbitrary mutable code or side effects in hooks. State it: the generator checks shapes and signatures only.

## 9. Test matrix (gates before public API)

Compile tests (positive/negative, TUnit + `compile-negative.py` pattern):
- `ExecuteAsync` unavailable on `PromptCommand` before `WithParameters`; wrong TP rejected; `Outcome<T>` inferred without type args
- external `new Invocation<…>` / `with` impossible; external derivation of `Outcome` impossible
- each diagnostic above has one firing test and one non-firing near-miss
- extension-block optional setters only visible on their closed `Invocation<TP,TR>`

Generator snapshot + behaviour:
- golden generated output; incremental cache test (edit unrelated file → no re-run step outputs)
- **record-shape mapping**: for a matrix of TParameters (primitives, enums, nullable ref/value, DateTimeOffset, nested record, list, defaulted ctor param, `required init` record), assert the projector emits exactly one `ParameterRecord` per public property with correct name/type id/value. Then round-trip through the AppJson context so that `Deserialize(projected) == original`
- render parity: generated renderer vs runtime override renderer, same output on a fixed corpus incl. braces, repeated placeholders, nulls, culture (`InvariantCulture` enforced)
- hash stability: whitespace-identical regen gives same hash; property rename changes hash

Runtime (fake `IChatClient`):
- precedence permutations (profile/temperature chain order), clear vs inherit
- caller info equals the user line, not the library line; helper `CallSite` overload
- cancellation vs timeout OCE; provider throw; invalid JSON; `Validate(TR)` failure, each yielding an Outcome with an ExecutionId and one sink record
- DI: singleton command, scoped context isolation under concurrency; `CommandRuntime.Create` path disposal ownership
- sinks/providers in separate registries; a sink failure never changes the Outcome

## 10. Stale items to fix in design.md

- "Keep the generated API C# 11-compatible" and "C# 11 results" sections conflict with the C# 15 / .NET 11 RC3 target and the `closed` record already used in `CommandRoot.cs`.
- "Use atomic `Bind` as the basic call surface" is superseded by the command root (call-pattern-feedback already says so).
- The typed `Define(...)` partial-method authoring is superseded by TParameters property binding. Keep `Bind` only as an optional generated convenience, `extract.Bind(document: d)`, which forwards to `WithParameters(new(d))`.

## 11. Open questions for root/user

1. Is `string` result (no schema) in the first version scope?
2. Commands as DI singletons are fine only if the runtime is singleton. Is there a need for per-tenant runtimes (which would require scoped commands)?
3. Should `Configure(Action<ChatSettingsPatch>)` be public in the first version, or limited to named fluent setters to keep the effective-settings record closed?

## Integration decisions and follow-up review

The lead accepted the generated-data/library-owned-invocation boundary. CommandRoot now uses separate runtime, generator and consumer assemblies. The implementation uses a generated activation constructor passing a descriptor to the library base; it does not require static-abstract self-type dispatch to execute. `Configure(Action<ChatSettingsPatch>)` is present in the experiment and freezes its result immediately. Named records remain the typed authoring model. Generated DI registration is a later gate; the example has one explicit composition root.

The DMTS then reviewed the implementation and identified schema names invalid for OpenAI, JSON naming/ignore-policy mismatch, escaping customization failures, loss of valid output on ledger failure, fixed actor identity, and overbroad claims about trusted descriptors. The engineer added safe distinct schema names; JSON-metadata schema/projector behavior; explicit failure outcomes; separate output delivery; per-execution actor resolution; and diagnostics/private descriptors. Output artifacts keep the original validated wire JSON, so serialization policies do not erase gradeable null or ignored fields. Those fixes have focused TUnit and compiler cases.

Remaining production gates include incremental caching, general converter/AOT support, mutable parameter snapshots, dynamic profile registry validation at startup, full failure-envelope/sink integration, durable collection, native provider adapters and real credentialed calls. Explicitly unsupported generator shapes need diagnostics. Build-time guarantees apply to generated shapes, not malicious hand-authored descriptors, reflection or arbitrary hook side effects.

## Typed profile review — proposed, not implemented

The DMTS and End-User Customer Advocate reviewed two complete shapes: generated profile types with generic defaults/calls, and generated `ProfileKey` properties with a typed command default. Both are valid designs. The reviewers changed preferences during cross-review; this is a tradeoff, not a proven correctness advantage of one design. Neither proposal was built or tested in this review. The executable CommandRoot spike still uses strings.

The lead recommends profile values for the user's object-oriented, fluent, minimal-setup goal:

```csharp
public static partial class Profiles
{
    [ModelProfile("extraction")]
    public static partial ProfileKey Extraction { get; }

    [ModelProfile("drafting")]
    public static partial ProfileKey Drafting { get; }
}

// Inside the user's existing ExtractInvoice partial class:
static ProfileKey DefaultProfile => Profiles.Extraction;

// Host setup, once. Model/provider values can also bind from configuration.
// llm.ConfigureProfile(Profiles.Extraction, p => p.Temperature = 0);

// Application call; the default profile needs no explicit selection.
Outcome<Invoice> result = await extract
    .WithParameters(new InvoiceParameters(document, Locale: "en"))
    .WithProfile(Profiles.Drafting) // Optional override; retains parameter/result types.
    .InRun(runId)
    .ExecuteAsync(ct);
```

`ProfileKey` should be an immutable sealed value object with ordinal equality on its stable external ID. It has no string conversion or public constructor. Generated declarations cache their keys. Dynamic configuration uses an explicit validated factory with a failure result; it does not throw for an invalid expected input. A generated declaration factory must be callable from the consumer assembly, so its restricted editor visibility or analyzer is a usage signal, not a security boundary.

The generator checks declaration shape, ID format and duplicate IDs within a compilation. It checks that `DefaultProfile` is static, readable and non-nullable `ProfileKey`, then emits a reference to that member. It does not evaluate arbitrary property bodies. The default key is evaluated once when the descriptor initializes. The profile's settings are resolved separately for each execution and captured as a frozen configuration snapshot.

Removing `WithProfile(string)` from the typed surface makes accidental raw strings/model IDs a compiler error. A misspelled `Profiles` member is also a compiler error. Null suppression, manually created keys and unregistered configuration still need runtime validation. Startup checks registered defaults, duplicate IDs across assemblies, provider registrations and option ranges. Dynamically selected keys are checked at resolution. These checks do not prove remote credentials, model availability or actual provider capabilities.

The marker-only alternative remains sound: `[ModelProfile("extraction")] partial class Extraction`, a generic command attribute and `WithProfile<Profiles.Extraction>()`. It gives a typed attribute default. Value keys instead support ordinary fields, collections and per-tenant selection through the same method. Do not generate marker types plus value tokens for the same default API.

Keep configuration values in named options keyed by the stable ID. Freeze `Action` hook results into snapshots, preserve explicit call settings regardless of chain order, and record profile ID plus configuration revision in telemetry. Multi-provider routing, generated startup registration and the corresponding compiler/startup tests remain implementation gates.

### Selector refinement requested by the user

Implementation follow-up: the [ProfileRouting spike](../../research/spikes/ProfileRouting/README.md) now integrates this selector with generated catalogs, the actual generated command, provider routing, bounded retries/fallback and telemetry. The isolated probe described below remains language evidence; the newer CommandRoot TUnit/compiler suites test the integrated shape. Streaming/tools, complete SDK adapters and external configuration/DI remain gates.

`WithProfile<Profiles>(p => p.Extraction)` is a valid alternative selection syntax. It keeps the value-key model: the selector returns a `ProfileKey`, and the invocation stores that key. It requires a non-static catalog with instance properties, replacing the static container in the earlier sketch:

```csharp
public sealed partial class Profiles
{
    [ModelProfile("extraction")]
    public partial ProfileKey Extraction { get; }
}

Outcome<Invoice> result = await extract
    .WithParameters(new InvoiceParameters(document, Locale: "en"))
    .WithProfile<Profiles>(p => p.Extraction)
    .InRun(runId)
    .ExecuteAsync(ct);
```

The library method is an instance member of `Invocation<TParameters, TResult>` with its own single `TCatalog` type parameter, a `Func<TCatalog, ProfileKey>` argument, and a generated-catalog constraint. The generator can supply a shared immutable catalog through a static-abstract interface member. The application does not create it or register it as a service. Execute the selector immediately and keep only its selected key. Do not retain or serialize the delegate. The return type remains `Invocation<TParameters, TResult>`.

The lambda parameter is statically `Profiles`, giving language services a concrete type for member completion. IDE presentation was not tested. A `Func` checks the return type, not the syntax or origin of the returned key: a closure can return a key from elsewhere. If catalog-exclusive keys become a requirement, use a catalog-qualified key type or a separate diagnostic; do not imply that an ordinary delegate proves origin.

Six isolated compiler/runtime checks pass on SDK `11.0.100-rc.3.26522.148`, language version 15, nullable warnings as errors. They cover the valid selector, immediate single evaluation and immutable/type-preserving result; wrong return type; misspelled property; wrong catalog constraint; rejected static catalog; and the intentionally allowed externally sourced key. Reports are `/tmp/opencode/llmcmd-profile-selector/tests.xml` and `results.json`. The probe hand-writes the generated partial implementation and a minimal invocation. It does not implement this API in CommandRoot or its generator.

The static-type restriction is also documented in Microsoft's [generic type argument diagnostics (CS0718)](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-messages/generic-type-parameters-errors).

## Partial-member follow-up (2026-11-01)

The user clarified **partial members**, not extension members. The original DMTS and System Architect sessions hit their provider session quota. Two explicitly identified Codex substitutes reviewed the same roles and source files; the existing End-User Customer Advocate also reviewed them. This section is the lead's synthesis, informed by their final addenda and compiler evidence. It does not change the runtime/generator API.

### Decision

Use partial members where authored behavior meets generated wiring. Keep the ordinary constructor fully generated and ExecuteAsync in the shared runtime library. A mandatory authored partial constructor or Descriptor property adds declarations to plumbing that users currently do not write. A partial constructor remains a valid optional contract when a user explicitly needs it.

Generated/debugging metadata may live on generated members. Attributes on partial properties and methods generally merge into the compiled member; do not duplicate attributes whose AllowMultiple is false. Do not mark a user-written customization body as generated code merely because its defining declaration is generated.

Keep stable identity explicit. Discovering PromptCommand<TP,TR> subclasses could replace the current command marker, but metadata still needs an authored source. Three constants for ID/revision/profile are not automatically less boilerplate than one attribute. The current profile generator already uses partial getters and reads ModelProfile from source. Moving that discovery attribute into its own output without changing discovery would stop generation. Keep the existing marker as the concise default unless a different authoring convention gives a concrete benefit.

### Proposed hook direction

The application could optionally implement `static partial void ConfigureDefaults(ChatSettingsPatch defaults)`; the generator would emit its defining declaration and a wrapper. The runtime would invoke the wrapper inside a configuration-error boundary and freeze the resulting settings. Do not run arbitrary hooks in a static descriptor initializer. No hook implementation means the optional call is erased. This language pattern is tested; its integration into PromptGenerator is not implemented.

This keeps declaration and execution semantics separate: typed parameter/result records and command-root fluent calls stay unchanged. Defaults never override explicit per-call settings. Existing validation methods can remain convention-based; do not require extra partial signatures just to rename the current hooks.

### Attribute-placement limits

Caller-info attributes must be on the **defining declaration**, which may itself be generated. A real probe generator emitted that declaration while the user supplied the implementation; an application call captured Main correctly. It is therefore too restrictive to say these attributes must be user-authored. Implementation-only CallerMemberName was ignored, with CS4026 on the pinned SDK; suppressing the warning confirmed that the caller's default string was used. Default arguments also belong to the defining declaration. Both partial signatures must still match, so this technique does not remove their parameters. The existing ordinary library ExecuteAsync already centralizes caller capture without adding a user declaration.

Normal source generators do not consume each other's ordinary source output. A generated-only JsonSerializable context failed to get STJ implementations (CS0534). More subtly, a generated JsonRequired on a partial property appeared in final CLR reflection but the STJ source-generated contract had IsRequired=false and accepted an omitted field, with serialization reflection disabled. Keep JSON discovery/shape annotations source-visible in this architecture, or choose a distinct serialization-generation/build stage. Compiled attribute merging alone is not enough.

### Evidence

Thirteen compiler/runtime characterization checks passed with SDK 11.0.100-rc.3.26522.148, C# 15 and warnings as errors. Positive cases verify attribute merging, partial constructor/base initialization, optional hook pairing/erasure, caller capture from an actual generated defining signature and the STJ metadata distinction. Negative cases assert duplicate-attribute, missing implementation, ignored implementation-default and peer-generator diagnostics. Reports and probe sources: `/tmp/opencode/llmcmd-partial-members/{tests.xml,results.json,run.py,Generator/Generator.cs}`. These are isolated language/serialization probes; the proposed hook is not integrated into LlmCmd.

Primary references: [C# partial-member rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/partial-member), [partial-property attribute merging](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-13.0/partial-properties), [Roslyn normal generator inputs](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.md).

### Generate executable code instead of attributes

Follow-up: the user asked whether code can replace attributes. All three available reviewers recommend a generated JSON contract modifier as the first implementation. The normal serializer generator still supplies the base metadata from one source-authored app context; LlmCmd emits executable assignments to JsonPropertyInfo.IsRequired before metadata is frozen. This replaces repetitive JsonRequired property annotations without expecting STJ to discover our emitted annotations. Deserialization, MEAI schema export and parameter JSON-name mapping must all use the same modified contract/options.

Requiredness needs one documented source policy. For supported positional records, use non-optional constructor parameters and C# required members; nullable means a value may be null, not that its JSON member may be absent. Optional constructor defaults remain optional. Advanced converters/naming/ignore policies require explicit contract overrides or clear unsupported-shape diagnostics. Do not guess from non-nullability alone.

Eight additional isolated checks passed with a real probe generator, MEAI 10.11.0 and reflection-based serialization disabled. Generated code sets requiredness for ordinary record properties without JsonRequired annotations. The checks verify missing-field rejection, optional constructor defaults, non-nullable string rejection of explicit null, nullable-but-present fields, renamed JSON properties and MEAI schema required lists. Probe source/report: `/tmp/opencode/llmcmd-contract-code/{Program.cs,tests.xml,run.log,schema.json}`; actual generated policy: `obj/generated/Generator/ProbeGenerator/ContractPolicy.g.cs` under the same directory. The probe emitter hard-codes two supported record shapes; it is proof of the contract API, not a general inference implementation or a NativeAOT publish test.

Recommended implementation sequence:

1. Discover commands from the explicit PromptCommand<TP,TR> base and a stable authored Id constant, with diagnostics. Make routine revision/profile/system defaults host-owned, retaining explicit overrides. Keep the current compact attribute form as a compatibility input during migration; do not require two identities.
2. Generate required-field policy and typed validation directly. Reuse one authoritative JSON contract for schema, deserialize/serialize and telemetry field mapping. Preserve the internal executor and current typed fluent chain.
3. Centralize one app serializer context and generated command/DI registration. Keep JsonSerializable source-visible in the first version; caller-info attributes remain centralized in the library terminal. Generate ordinary constructors/descriptors, and offer optional partial hooks without mandatory extra declarations.
4. If eliminating the app context itself is required, evaluate a separate precompile stage that produces serializer inputs, or explicitly supported generated contracts/converters. Gate either on AOT and JSON parity. Avoid turning a small prompt generator into an unbounded serializer rewrite or depending on serializer helper APIs documented only for STJ generator output.

This is a proposed implementation plan. No production generator/runtime files changed for this review. Public API reference: [JSON contract customization](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/custom-contracts).

### Approved plan implemented in the CommandRoot spike

The user approved implementation after the plan above. [CommandRoot.Demo](../../research/spikes/CommandRoot.Demo/README.md) now uses ordinary parameter/result records and an attribute-free typed command with an authored stable `Id` and `UserTemplate`. The constructor and descriptor remain generated. Routine revision/system/profile metadata have defaults. Explicit JSON context selection and the legacy command attribute remain compatibility inputs.

The precompile stage emits the app context as a normal source item before `CoreCompile`. STJ therefore consumes it directly. The command generator emits executable required-field policy, projection, nested nullable validation, schema wiring, optional partial defaults hooks and DI registration. It does not depend on one ordinary source generator consuming another's output. `AddLlmCommandsFrom<AssemblyName>(runtime)` checks generated contracts and default profiles at setup. Registration does not call a provider.

Requiredness is now a documented convention: non-optional constructor parameters, C# required members and JsonRequired require presence. A settable non-nullable reference without a constructor parameter is also required, even with an initializer. Optional constructor defaults remain optional. Nullable permits null independently of presence. This explicitly extends the earlier positional-record policy to ordinary DTO setters.

The original Claude DMTS supplied a fresh read-only review. The lead accepted and addressed its four high-value findings: hashes exclude unrelated context roots and use typed hash inputs; schema requiredness matches setter validation; invalid contracts are checked at setup and mapped to runtime ConfigurationFailure; templates reject always-ignored properties. Two principal findings were also addressed: conflicting nullable instantiations of the same CLR generic contract are diagnosed, and validation dispatches to declared JSON-derived types.

The decision on raw output is unchanged: preserve the original parsed wire JSON after typed validation. Extra/ignored fields are not covered by typed validation and remain subject to collection policy. An explicit serializer context may reject unknown members. Re-serializing the typed value would lose wire fields and was not adopted. Strict-provider schema adaptation remains a separate gate. Initial requested settings and actual per-attempt settings are recorded separately. Per-build precompile analysis cost and the initial IDE build requirement are documented; full incremental caching remains open.

Evidence lives in the CommandRoot TUnit/compiler suites, `generation-proof.py`, `tests/PrecompileProof/run.py` and the executable demo. The generation proof checks unrelated-command hash stability in default and explicit contexts, meaningful policy/default changes, invalid-contract setup/runtime boundaries and generated registration. The demo and generation proof run with reflection serialization disabled. This is integrated spike evidence, not a packaged library, full NativeAOT publish test or verification of an IDE lifecycle. See the [current README](../../research/spikes/CommandRoot/README.md) for supported contracts and remaining gates.
