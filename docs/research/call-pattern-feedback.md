# Call pattern feedback from the application developer

Reviewed 2026-11-01 for C# 15 / .NET 11 RC3 and Microsoft.Extensions.AI 10.11.0. Role: End-User Customer Advocate. This is an API usability review. It does not change the generator, executor, tests, or collector adapters.

## Recommendation

Make **an injected, generated user-space partial-class command root with typed parameters and a typed result** the main example. This follows the user's explicit preference for more chaining and an internal executor. Use `Command<TParameters, TResult>` as the conceptual executable contract while preserving the generated command's public fluent surface. The application calls the command; it does not receive or pass an executor. Keep atomic `Bind` as a supported convenience. The earlier preference for atomic binding as the default is superseded.

**Preferred integrated call site, proposed pending DMTS architecture review:**

```csharp
public sealed record InvoiceParameters(string Document);
```

Inside an application method, `extractInvoice` is the injected `ExtractInvoice` partial-class command root. Its declared result type is `InvoiceExtraction`:

```csharp
ExecutionResult<InvoiceExtraction> result = await extractInvoice
    .WithParameters(new InvoiceParameters(document))
    .WithProfile("extraction")
    .InRun(runId)
    .ExecuteAsync(cancellationToken);
```

The required constructor makes `new InvoiceParameters(document)` a complete instance. `new InvoiceParameters()` is not valid for this declaration. The injected root represents an unbound command with no `ExecuteAsync`. `WithParameters` returns a generated executable invocation carrying `InvoiceParameters` and `InvoiceExtraction`; `Command<InvoiceParameters, InvoiceExtraction>` describes that type contract. `WithProfile`, `WithTemperature`, and `InRun` must preserve both types and the fluent surface. The final call returns `ExecutionResult<InvoiceExtraction>` without requiring another result-type argument or an executor argument.

The generated partial-class root fixes both generic arguments from the command definition, so application code need not spell them. `Command<TParameters>` alone is insufficient for a general structured-output contract: two definitions can share an input type and return different output types. A one-generic convenience can be offered where the output is fixed, but it must not erase the result type from helpers or execution.

The record constructor establishes required argument presence. Null suppression, external data, mutable referenced values, and semantic constraints still require runtime validation. A `required init` parameter object is another supported authoring shape; its object initializer must supply all required members. Neither shape requires a fixed order of individual fluent setters.

The injected partial-class root, `WithParameters`, generic command/result types, `InRun`, and terminal execution are proposals. Generated constructors, DI registration, and any runtime façade remain subject to DMTS architecture review and later proof. The current generator supports atomic `Bind` and `WithModel`; the separate execution spike still accepts `DemoCommand`. Its public executor is a proof mechanism, not the intended application API.

**Supported convenience already available in the generator spike:**

```csharp
var command = SupportReply.Bind(
    customer: customer,
    question: question)
    .WithModel("example-model");
```

Named arguments are an optional readability aid for this convenience, especially when adjacent parameters share a type. They also allow a different argument order. This is a compiler feature, not a reason to displace the user's preferred chain. See the [C# named-argument rules](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/named-and-optional-arguments).

Use an explicit logical ID such as `support.reply`. The sample currently uses `support.reply.v1`. That spelling can teach users to change the identity on every revision. Put the revision in separate metadata. A class-name default is convenient for a local experiment, but moving or renaming the class must not silently rename a durable evaluation dataset.

## Choosing the input surface

### Command root plus typed parameter record: preferred default

The injected command stays the root of the chain through execution. A single `WithParameters` call establishes the required-input boundary. Keep the unbound and executable types separate so completion can offer execution only after parameters are attached. A mutable command with a nullable `Parameters` property and an always-visible `ExecuteAsync` would lose that guarantee. Every immutable invocation copy must retain its internal runtime binding. Parameters, settings, and run IDs belong to that invocation; fluent calls must not mutate the injected root shared by other calls.

The parameter record is a value carried by the command, not a new root API. It gives helpers a concise type to accept and return. With the constructor declaration above, `WithParameters(new InvoiceParameters(document))` is valid; `WithParameters(document)` should fail unless a deliberate generated overload exists. Do not accept arbitrary objects or dictionaries through this method.

Configuration methods after `WithParameters` return `Command<TParameters, TResult>`, or a concrete generated command that preserves both arguments and domain-specific fluent members. If settings are also offered before binding, their return type must remain `UnboundCommand<TParameters, TResult>`. They must not unlock execution early.

### Atomic Bind: supported convenience

Required arguments appear together. Ordinary argument completion and named arguments provide the key affordances. Missing arguments produce CS7036, which names the missing parameter. Wrong argument types produce CS1503. No incomplete command is returned by a successful call.

Several arguments fit on separate lines. Keep normal .NET naming: `customer` for the method parameter and `Customer` for a generated public property. In the integrated design, a `Bind` convenience on the injected root should return the same executable typed contract as `WithParameters(...)`, retaining the internal runtime binding. It is a shorter alternative, not the primary documented experience. The static `Bind` shown earlier is evidence from the current generator spike.

### Generated individual setters: optional fluent convenience

The existing hand-written spike proves that each setter can change its own presence state and preserve the other states. It also proves that both orders compile and an incomplete `Build()` does not. This is a sound compiler boundary for presence when consumers use the intended API.

**Proposed generated spelling; only the analogous Name/Count spike is proven:**

```csharp
var command = supportReply // Injected typed command root.
    .WithQuestion(question)
    .WithCustomer(customer)
    .Build()
    .WithProfile(ModelProfiles.Support);
```

This makes sense when values arrive in separate helpers or the user wants every required value in the chain. It supplements `WithParameters(new SupportReplyParameters(customer, question))`. The sample above retains the proven spike's explicit `Build` boundary. A future generated all-Present state could instead expose `ExecuteAsync` directly, but that needs its own compile tests. Keep execution unavailable before the final required value in either form.

Individual setters expose state types in diagnostics. At two inputs, CS1929 describes `Draft<Present, Missing>` and the receiver type required by `Build`. It does not directly say “question is missing.” At ten inputs, that message will be harder to decode. That last statement is a usability inference; no ten-parameter IDE study has been done.

Keep optional settings after `Build()` in the first version. Allowing them on every draft stage expands the surface and forces every helper to preserve every presence state. Support that later only if real call sites need it.

The source can avoid 2^N named classes. That does not make signatures small: each setter preserves the other N-1 state parameters. The possible constructed combinations remain exponential. Do not claim scalability or IDE quality from the two-input compile proof.

### Fixed staged flow: not the default for independent inputs

The generated `Stage0` → `Stage1` → `Stage2` chain puts only the next setter on each stage. This is useful when the domain actually has an order. Customer and question are independent values. Requiring customer first imposes the declaration order on application code.

Adding or moving a parameter changes the chain structure and stage numbers. Returning a generated stage from a helper makes that helper more fragile. `Stage1` also gives little help in a compiler error. If a domain needs stages, use meaningful names such as `NeedsQuestion`, not numeric stage names. This is a naming proposal, not a request to generate every subset as a named type.

### Required-property parameter object: alternative to a constructor record

**Proposed:**

```csharp
public sealed record SupportReplyParameters
{
    public required string Customer { get; init; }
    public required string Question { get; init; }
}
```

Inside an application method:

```csharp
var parameters = new SupportReplyParameters
{
    Customer = customer,
    Question = question
};
var command = supportReply.WithParameters(parameters); // Injected typed root.
```

Generated `required init` members can make omission a compile error in an initializer. They provide normal property completion and a convenient application-layer DTO. The command's `WithParameters` transition then enables execution. The initializer alone does not establish that command-state transition or validate runtime data. `required string?` means “supply this member, and null is allowed”; it does not mean “optional.” `SetsRequiredMembers` is an assertion that the compiler trusts. See the [required-member rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/required).

For many related values, an immutable parameter object is easier to pass between methods than a large generic draft. Do not hide a runtime dictionary behind this API and call it compile-time typing. Retain the generated parameter types and validate deserialized values at the execution boundary.

## Presence, types, defaults, and replacement

There are three different questions: was a value supplied, does it have the declared type, and is it valid for this command? Typestate answers the first. Static signatures answer much of the second. Runtime validation must answer the third, including values from external templates and parameter providers.

The current generator has two specific gaps, reproduced in isolated C# 15 consumers with nullable warnings treated as errors:

- `Define(string? name, int count)` becomes generated `Define(string name, int count)` and `Bind(string name, int count)`. The compiler reports CS8611 for the partial-method mismatch and CS8625 when the caller supplies null. The formatter at `spikes/TypedCommands/Generator/PromptGenerator.cs:55` does not preserve the nullable reference annotation.
- `Define(string name, int count = 3)` still generates a `Bind` that requires count. Omitting it reports CS7036. The generator neither propagates the default nor rejects unsupported optional declarations with a specific diagnostic.

The review also confirmed that nullable value types survive generation, an explicit null for a non-nullable reference is rejected under warnings-as-errors, and `null!` still compiles. “All parameters supplied” must never be advertised as “all parameters valid.”

Define optionality explicitly before adding optional template parameters. A useful proposed convention is a parameter record with required constructor inputs, followed by generated optional methods such as `WithLocale("en-US")` on the typed command. An omitted optional value then retains its inherited origin. A plain C# optional parameter default often loses the distinction between “omitted” and “explicitly set to the default value.” That distinction matters when an external configuration can supply the effective default.

Settings should distinguish inherited, explicitly set, and explicitly cleared values internally. Do not make the caller guess whether `WithTemperature(null)` means clear or inherit. Prefer a value-setting method plus explicit reset/clear operations where the option supports them. Names and clear semantics still need a public API decision.

Publish a short precedence contract: platform defaults, command defaults, selected profile/runtime settings, then explicit call settings. Applying a profile must not silently erase an explicit temperature merely because `WithProfile` appears later in the chain. Store profile selection and explicit field patches separately, then resolve the profile before applying explicit patches. Repeating the same explicit setting can use “last call wins”; that behavior is normal for a configuration chain and is easy to test.

Required draft setters can reject duplicates. The current any-order spike does so. `WithParameters` on the unbound root is the initial binding transition. If it is also available on a complete command, document that it replaces the complete parameter value, returns a new command with the same two generic arguments, and preserves the original instance. Do not make it a partial dictionary merge. External replacement remains denied unless the parameter contract or call policy permits it. Record caller and effective values so an application developer can explain a changed model input.

## Does completion expose execution correctly?

**Proven:** the fixed builder has `Build` only on its last stage. The any-order extension accepts only an all-Present receiver. The existing negative compiler tests reject early completion and duplicate required setters.

**Not proven:** IDE completion hiding, a generated any-order builder, or integrated execution eligibility. Neither builder spike executes the generated command. The separate executor's signature accepts `DemoCommand`, not a generated bound contract.

The proposed injected root must expose no `ExecuteAsync` before `WithParameters`. The generated complete invocation exposes `ExecuteAsync(cancellationToken)` and forwards internally to an executor accepting only a complete contract such as `IBoundCommand<TParameters, TResult>`. Unbound roots and partial drafts must not implement that contract. Avoid a broad public `ExecuteAsync(object)` or an executable common base shared by complete and incomplete commands. The executor and its internal dispatch contract are implementation details; application calls must not need to supply them.

Do not add an obsolete “error” `Build` fallback just to make a nicer diagnostic without checking IDE behavior. Such a method can make an invalid operation appear in completion. An optional analyzer can instead report missing parameter names or duplicate setters. The compiler types should remain the source of the guarantee when that analyzer is disabled.

Before publishing a completion claim, check supported IDEs with the real package: empty draft, one missing value, complete draft, wrong namespace import, helper returns, and errors on a long chain. Applicable method binding and what an editor chooses to show are different facts.

## Branch reuse, helpers, and asynchronous execution

The review compiled and ran branching from the current immutable scalar draft:

```csharp
var common = Command.Start().WithName("Ada");
var first = common.WithCount(3).Build();
var second = common.WithCount(7).Build();
```

Both branches retain their own values. The review also confirmed that two `WithModel` calls retain the second model and leave the original bound command unchanged.

An application developer must understand that a local variable's type does not evolve:

```csharp
var draft = Command.Start();
draft = draft.WithName("Ada"); // CS0029: the presence state changed.
```

Prefer a single expression, a new local for the next state, or branches that return the same completed state. Avoid requiring application helpers to spell a long `Draft<...>` return type. A complete command or parameter object is a better public helper boundary.

Immutability is shallow unless the library snapshots reference values. The current strings and integers make branching safe. Adding a list, dictionary, or mutable customer object needs a documented snapshot point and tests. Prefer supported immutable inputs or snapshots taken when binding if the command promises stable reuse. An `IReadOnlyList<T>` type alone does not make its contents immutable.

`WithParameters`, other `With...` methods, `InRun`, and any `Build` convenience should perform local work only. Place external resolution, rendering that depends on external data, model calls, and collection behind the command's terminal `ExecuteAsync`. The application passes only its cancellation token; runtime dispatch stays behind the generated command. A bound command is reusable input, not a cached result; executing it twice can produce two executions and model charges. Use a separate explicit replay/pinned-plan API when that behavior is required.

## Output type propagation and lessons from other libraries

Preserve three facts through the API: whether parameters are attached, their declared type, and the result type. Distinct unbound/complete types carry presence. `Command<TParameters, TResult>` carries the other two facts. For extraction, the chain stays `Command<InvoiceParameters, InvoiceExtraction>` after `WithParameters`, `WithProfile`, `WithTemperature`, and `InRun`. Its `ExecuteAsync` returns `Task<ExecutionResult<InvoiceExtraction>>`.

Do not downgrade a settings return type to `Command<TParameters>`, `object`, or an untyped command base. That would force a later type argument or cast and break the user's typed-chain requirement. Generic helper methods must also preserve both arguments. For example, a profile helper accepting `Command<TParameters, TResult>` should return `Command<TParameters, TResult>`.

Prefer configuration methods that return the full typed command, or the concrete generated command when it has additional fluent members. Returning only `IBoundCommand<TParameters, TResult>` preserves the type arguments but may hide those methods from the rest of the chain. The internal executor can use that interface and infer both arguments from its input. The application-facing terminal operation remains on the command.

The command definition should normally select the structured output contract. A generic `ExecuteAsync<Anything>()` on an untyped command lets each caller request a different schema without making the contract change visible. An advanced `AsJson<T>()` operation could be supported, but it must produce a new typed command/variant, update schema identity, and record that choice. It is not a harmless cast.

Useful library techniques, with limits:

- LINQ's [`ThenBy`](https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable.thenby?view=net-10.0) requires `IOrderedEnumerable<T>` and returns it. A type grants the next operation. This is the closest simple analogy to exposing completion only on a complete command.
- LINQ's [`Select<TSource,TResult>`](https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable.select?view=net-10.0) carries a deliberate type change in its return type. LlmCmd should make a structured-output change equally explicit. Normal settings do not change the output type.
- EF Core's [`ThenInclude`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.entityframeworkqueryableextensions.theninclude?view=efcore-10.0) retains the root entity type while changing the current navigation type. It illustrates preserving independent type dimensions through a fluent API. It does not prove arbitrary required-input tracking or good completion for large state vectors.

MEAI 10.11.0 already returns `Task<ChatResponse<T>>` from its structured-output extensions. The [pinned implementation](https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/ChatClientStructuredOutputExtensions.cs) generates the requested schema and returns a wrapper after the model call. Parsing is lazy: [`ChatResponse<T>`](https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/ChatResponse%7BT%7D.cs) exposes a throwing `Result` property and a `TryGetResult` method.

For the proposed closed `ExecutionResult<T>`, success should contain an already parsed and validated `T`, plus the full response and execution reference. The executor should classify parse/schema failures before reporting success. Returning `Success` with a lazy property that can later fail would defeat the promised expected-error boundary. Retain the original output for diagnosis. A correctly typed C# object still needs domain validation. A streaming update is not a complete `T`; structured streaming needs a separate completion contract.

## Caller capture and names

Capture the source location at the public execution entry point. Binding and execution can occur in different methods. If creation location is useful, record it separately and label it as creation metadata.

The primary generated `command.ExecuteAsync(cancellationToken)` method must declare optional caller-information parameters with caller attributes and forward their values to the internal runtime. These parameters are supplied by the compiler in the normal call. Attributes only on the underlying executor record the generated wrapper, not the application line. A shared application helper must also forward caller values if the original caller matters. A helper that does not forward them should record the helper honestly. An explicit `CallSite` overload is useful for advanced wrappers; it must not require an executor argument and is not a trusted identity. See the [compiler caller-information rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/attributes/caller-information).

Prefer `WithCustomer` to `With_customer`. Keep the authored template token `customer` as schema metadata. Generate conventional PascalCase public properties. Diagnose collisions after case conversion, including acronym and reserved member cases. A rename of a C# parameter does not automatically rewrite a raw template string or stored external template; diagnostics must report both unresolved and unused names. Keep logical prompt IDs stable across such refactors.

## Run context and later human feedback

**New requirement; the entire surface below is proposed.** One HTTP call returns extraction output. Later HTTP calls submit grades or corrections. The developer needs durable identifiers in the response, not an in-memory command, a `ChatResponse` object graph, or an ambient context expected to survive across requests.

Return a transport-safe envelope with `RunId`, `ExecutionId`, `OutputRef` (artifact ID and immutable version), schema identity, and the structured value. Keep the original JSON artifact separately. `RunId` is the durable correlation handle. If a run can contain re-extractions, do not imply that it always equals one execution ID. Each extraction still identifies its own output artifact/version.

The command-root `.InRun(runId)` makes correlation explicit at the call site. It returns a new `Command<TParameters, TResult>` carrying that ID; it must preserve both type arguments and leave other command instances unchanged. It does not create an execution ID, load a run from storage, or restore an ambient scope from an earlier HTTP request. Validate the run relationship during execution. A later feedback request reconstructs its explicit target from the returned IDs. If repeated `InRun` calls are allowed, define the last ID as the chosen context and freeze it when execution starts.

**Explicit-target feedback overload, illustrative type names:**

```csharp
var target = new OutputTarget(
    runId: request.RunId,
    outputId: request.OutputId,
    version: request.OutputVersion);

var receipt = await feedback.RecordGradeAsync(
    target,
    new HumanGrade(
        feedbackId: request.FeedbackId,
        rubricId: "extraction.accuracy",
        rubricVersion: "1",
        score: request.Score,
        fieldPath: request.FieldPath),
    cancellationToken: cancellationToken);
```

A fluent context such as `feedback.InRun(runId).ForOutput(outputRef).RecordGradeAsync(grade, cancellationToken)` fits the user's preferred style too. The output-bound context must retain the artifact ID and immutable version; only the terminal call performs I/O. Keep the explicit-target overload above for callers that already have an HTTP DTO or target value object. Additional `ForField` or grade setters must preserve the full target and enforce any new required state. No fluent method should silently replace the selected output version.

Do not silently attach feedback to “latest output.” A grade belongs to the exact version the person saw. Require an output version even if the run currently contains only one output. Verify that the output belongs to the named run. For run-level feedback, use a separate run target; do not make a missing output version change the meaning of an output grade.

Use [JSON Pointer](https://www.rfc-editor.org/rfc/rfc6901) against the stored JSON representation. `/items/0/amount` identifies a field in one output version. Use serialized JSON names, not CLR property paths. Escape `/` as `~1` and `~` as `~0`; the empty pointer identifies the whole document. Array indexes are meaningful only against the specified version. Generated field helpers may improve completion, but accepting externally supplied pointers still needs runtime validation.

Record a correction as an append-only feedback event with the original output target, field path, proposed value, and feedback ID. If the application applies the correction, return a new corrected output version and link it to the source. Preserve the original JSON and grade history. A suggestion and an accepted correction should have distinct status so a caller cannot mistake an unreviewed suggestion for the authoritative output.

Use an idempotency/feedback ID for retried HTTP submissions. Repeating the same submission should return the prior receipt; reusing that ID for different content should return a conflict. If two corrections target the same base version, return an explicit conflict or a documented merge result. Do not silently update a moving “current” record. Keep rubric identity and version with numeric grades so the meaning of a score remains known.

If context loading needs storage, name it `GetRunAsync` or `LoadRunAsync`. A synchronous `ForRun(runId)` should only construct a handle. The server should obtain the original trace/span references from durable execution metadata, not require the client to resubmit telemetry fields. Each follow-up request has its own activity; link it to the original activity. The original span can already be closed, unsampled, or expired, so trace context cannot replace `RunId` or `OutputRef`.

The feedback receipt should distinguish accepted/queued from persisted. Return typed failures for unknown target, version conflict, invalid field/value, and failed persistence. A successful extraction must keep its output identity even if later feedback fails. No model call is required merely to save a human grade or correction.

## Verification and release gates

Existing artifacts inspected during this review: TypedCommands 17/17, ArbitraryOrder 10/10, and the current TUnit report 34/34. These counts describe those reports at review time. They do not prove the proposed integrated API.

Additional review checks ran with `/tmp/opencode/llmcmd-dotnet11/dotnet`, SDK `11.0.100-rc.3.26522.148`, `LangVersion=15`, `net11.0`, and warnings-as-errors. The generator DLL was copied to an isolated temporary directory; no shared implementation was edited. Report: `/tmp/opencode/llmcmd-call-pattern/tests.xml`. Per-case source, generated source, and logs remain beside it. All nine characterization checks passed, including the expected compiler failures that expose current limitations.

The nine checks cover named argument reorder; lost nullable-reference annotation; lost optional default; non-nullable null rejection; null-forgiving acceptance; nullable value-type preservation; partial branch reuse; repeated setting precedence; and changing-state local reassignment failure. The last three use the current any-order source. They do not test IDE completion.

The `dotnet-inspect` query identified the MEAI structured-output return type but reported a rejected metadata row and a nonzero status. The installed 10.11.0 XML documentation and pinned source were used to confirm the details above. The inspection warning is not evidence of a package runtime failure.

Before calling the public surface complete:

- Preserve nullable annotations and parameter modifiers, or issue a precise unsupported-contract diagnostic. Choose and test optional-default semantics.
- Compile the injected generated partial-class root through `WithParameters` and the internal runtime. Verify both generic arguments after every setting/context method and through generic application helpers. Verify that `extractInvoice.ExecuteAsync(...)` fails before binding, the parameter record cannot be constructed without required values, wrong parameter records fail, and the complete chain infers its result type with only a cancellation-token argument. Check the optional individual-setter path separately. Constructor/DI/runtime-façade proof remains pending DMTS review.
- Test parse failure before success, null and invalid structured output, retained raw response, and schema identity. Test a structured response from a deterministic fake; a real paid model is not needed for this contract.
- Test command-root caller forwarding and separate creation/execution locations. Verify that `InRun` preserves the chosen run and both command types without mutating a shared root or creating an execution early.
- Exercise 5-, 10-, and 20-input schemas. Measure generated source and compile behavior; inspect IDE completion and diagnostics. Do not set a performance claim from the small spike.
- Add focused generator diagnostics with stable IDs and parameter/template locations. Let analyzers improve messages; keep compiler enforcement when analyzers are absent. Test rename, member collisions, nullable nested types, and explicit defaults.
- For feedback, round-trip the transport envelope; test delayed/out-of-order submission, retry idempotency, output-version mismatch, JSON Pointer escaping, concurrent corrections, original-output preservation, and linked activities without an active original span.

Make the user's preferred injected command root the public example: `extractInvoice.WithParameters(new InvoiceParameters(document)).WithProfile(...).InRun(runId).ExecuteAsync(ct)`. Preserve input and result types throughout that chain, and keep the executor internal. Keep `Bind` and generated individual setters as complementary conveniences. The integrated partial-class surface remains proposed pending DMTS architecture review and its compiler, runtime, and completion checks.
