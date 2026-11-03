# LlmCmd design synthesis

Research date: 2026-11-01. Status: architecture research with executable source-generation, command, adapter, feedback and observability spikes. These are research projects, not a published library.

Input: the [user-supplied architecture diagram](input-diagram.png), preserved with the research notes.

## Decision

Build a generated command contract over MEAI. Keep a prompt's definition, bound invocation, resolved execution plan, provider attempts, and collected record as separate objects. Generate parameter APIs and descriptors; put transport, configuration, middleware, and collection in the executor.

Use an injected user-authored `sealed partial` command class as the root. Its base is `PromptCommand<TParameters,TResult>`. The generator supplies descriptor data and an activation constructor. `WithParameters(new(...))` returns a library-owned immutable invocation; fluent settings retain both generic types and the opaque runtime binding. The terminal method is `ExecuteAsync(ct)`. The executor is internal to the runtime assembly. See [the DMTS review](dmts-review.md) and [the actual command-root spike](../../research/spikes/CommandRoot/README.md).

Use named positional records for inputs; target-typed `new(document)` keeps the call short. `Bind(...)` and generated individual setters remain optional conveniences. The separate typestate experiment proves independent required setters can chain in either order without emitting a class for each subset. Anonymous types do not provide a stable, nameable public serialization or replay contract.

Keep `PromptId` independent of revision, caller, and execution ID. Allow a fully qualified class name as an initial default, but issue a diagnostic encouraging an explicit attribute ID for persisted eval data. A class or namespace rename changes the default. Use an explicit stable ID before a prompt enters a durable dataset.

Target **C# 15 and .NET 11 RC3**, as requested. The verified SDK is `11.0.100-rc.3.26522.148`, pinned by root `global.json`. The SDK was downloaded from Microsoft and verified against its published SHA-512. Native unions and closed hierarchies work with `LangVersion=15`; they do not need `preview`. The analyzer host projects can still target netstandard2.0 with older implementation-language syntax. See [the C# 15 documentation](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) and [the SDK release](https://dotnet.microsoft.com/en-us/download/dotnet/11.0).

## Core objects

`PromptDescriptor` is immutable compiled metadata: stable ID, authored revision, parameter schema, raw message templates, default model profile/settings, renderer, serializer, and definition hash. No service clients or per-invocation state belong here.

`BoundCommand` holds typed caller values and explicit settings. Binding does not format the prompt or query external services. Runtime override permissions belong to parameter metadata or an explicit call policy.

`ResolvedCommand` is a frozen execution plan: effective template/messages, validated effective parameters, origins, configuration version, provider/model profile, request options, and experiment identity. Resolve it once before the first provider attempt. Retries reuse it; they must not fetch a newer Redis value mid-execution.

`ExecutionRecord` represents one logical command. `ProviderAttemptRecord[]` represents calls observed at the provider client boundary. A retry, tool loop, or failover can produce multiple attempts under one execution. A short circuit can produce none. A provider SDK can itself retry HTTP requests; the MEAI observer does not see those individual network requests.

`EvaluationResult` is a later artifact linked to an execution or replay. It contains evaluator identity/version, judge configuration where applicable, scores, diagnostics, and the dataset case. It is not the same object as execution telemetry.

## Parameter enforcement and fluent calls

The initial generator spike uses an attribute and a partial `Define` method. It remains a regression experiment for typed binding, optional/default/nullable preservation, and conventional fluent setters. The command-root generator evolves the authoring model to public properties of `TParameters`, constant message templates, a named result type, and a declared System.Text.Json context. It generates descriptors/renderers/projectors and diagnostics. Repeated placeholders map to one value; doubled braces are literals. Its grammar is `{identifier}`; the SK reference uses a different grammar.

The user context must declare both input and output types. Roslyn generators do not read each other's generated output, but our generator can emit references to serializer context members that compile after both generators run. Schema generation and telemetry projection must honor JSON naming/ignore/nullable metadata, rather than assuming serialized names equal C# property names. Do not silently accept unsupported mappings. Both the generated descriptor and the canonical record format need explicit versions.

The compiler enforces argument presence and static types. It does not prove a string is nonempty, a token count is positive, or that `null!` is valid. External data, reflection, deserialization, and plugin implementations still require runtime validation. Enable nullable warnings as errors for normal consumer code, but do not treat that as runtime validation.

The separate generic typestate experiment uses `Draft<TName,TCount>`. `WithName` accepts a draft whose name state is Missing and preserves the count state. `WithCount` does the reverse. `Build` accepts only `Draft<Present,Present>`. Both setter orders compile and execute; early Build, duplicate setters, wrong types, and direct external construction fail compilation. No analyzer has to infer arbitrary control flow.

For N parameters this shape needs N state parameters and N setters preserving the other states. It avoids generating 2^N named stage classes. It does not remove the possible 2^N constructed combinations, generic signature size, or tooling costs. The architect ran exploratory C# 14 compile probes up to 32 parameters; these are not a production IDE or compiler performance benchmark. The reproducible root compiler suite targets the pinned C# 15 SDK. See [the fluent technique review](fluent-techniques.md) for LINQ, EF Core, FluentValidation, inference and analyzer limits.

`required init` is another valid C# 11 option for object initializers, but it does not provide the requested setter chain. It also permits explicit null and trusts `SetsRequiredMembers`. See [the required-member rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/required). A mutable command plus a dataflow analyzer is a weaker enforcement boundary across branches, aliases, helper methods, and assemblies; do not promise full compile-time completeness for that design.

Production diagnostics should include malformed templates, unknown placeholders, unsupported parameter types, unused declarations, duplicate IDs within a compilation, unsupported class shapes, member collisions, and changes to persisted identity. Check duplicate IDs across loaded assemblies at registry startup. Give diagnostics stable codes and locations. Project the Roslyn syntax/semantic inputs into an equatable parameter model so incremental caching can work. Ship the attribute once in abstractions or embed an internal generated attribute; do not emit a public global attribute into every consumer assembly.

Use one parser/parameter model for generated rendering and runtime override rendering, with parity tests. The spikes use independent parsers and have not proven parity. Add AdditionalFiles authoring for `.prompt` files after the constant-template path works; derive typed signatures from an explicit schema rather than guessing types from prompt prose.

## Identity and revisions

Persist separate values:

- `PromptId`: logical identity, ideally explicit, such as `support.reply`.
- `AuthoredRevision`: declared contract/template revision for review and release management.
- `CompiledDefinitionHash`: SHA-256 of canonical message templates plus parameter schema and renderer version.
- `EffectiveTemplateHash`: exact effective message template content, before parameter substitution.
- `OverrideRevision` and `ConfigRevision`: the exact external snapshot used.
- `ExecutionId`: one invocation, unrelated to prompt identity or trace sampling.
- `ExperimentId`, `VariantId`, and `DatasetCaseId`: evaluation context.

The execution spike hashes only raw template bytes. It does not yet hash the schema, canonical messages, or renderer. Hashes do not anonymize prompt or parameter values. Do not include input parameter values in the logical prompt ID. For replay comparisons, use a separate fingerprint of the complete resolved input/settings/schema and a documented canonical representation.

## Runtime replacement and defaults

Keep the diagram's prompt and parameter providers as adapter interfaces if useful. Put an `IExecutionSnapshotResolver` in front of them so one invocation sees a consistent version of the template, parameter replacements, and model settings. Independent mutable reads can mix revisions. Redis can hold one versioned document or a manifest pointing at immutable versioned entries. A single Redis GET of a complete document is an appropriate first adapter spike.

Use distinct source outcomes: `NotFound`, `Found(snapshot)`, `Unavailable(reason)`, and `Invalid(snapshot,error)`. Absence normally means compiled defaults. An invalid present override rejects the invocation by default. Source failure needs an explicit policy: strict for a pinned experiment; optionally last-known-good or compiled fallback for normal service operation. Record any fallback and the chosen revision. Never silently switch a pinned experiment to a different template.

Validate before model execution: every referenced token must exist in the compiled schema; replacement values must deserialize to the declared type and satisfy declared constraints; model/settings must fit supported capabilities. Reject unknown fields and partial invalid replacement batches. A replacement template may use a subset of declared parameters; record unused values where needed for replay. A new required placeholder is a contract change that requires regeneration or an explicit schema adapter.

Separate policies for settings and prompt data:

- Settings precedence: platform defaults → command defaults → selected model/profile configuration and runtime settings → explicit call settings. Resolve the full profile bundle first, then overlay explicit fields. Freeze it before execution. Null-as-inherit is sufficient for the spike; production needs an explicit Unset/Set/Clear patch model for nullable options.
- Parameter precedence: caller values are authoritative unless that parameter explicitly permits runtime replacement. For an allowed replacement, the external value wins and the record stores both caller and effective values plus origin/revision. This implements prompt/parameter experiments without changing unrelated application data. The runtime spike tests denied-by-default and opt-in replacement.

Provider routing and ModelId are different decisions. A model setting does not construct a different provider client. Resolve a logical profile to a provider key, deployment/model ID, supported options, and rate-card reference. Validate incompatible combinations. MEAI now has routing and failover clients, but the researched 10.11.0 APIs carry the experimental MEAI001 diagnostic; isolate adoption behind an adapter. See the [primary-source review](primary-source-research.md).

The [ProfileRouting experiment](../../research/spikes/ProfileRouting/README.md) now connects provider/model selection, retry and fallback to the generated command. Its generated non-static catalog supports `WithProfile<Profiles>(p => p.OpenAI)`. Each profile owns its retry policy and optional fallback profile; one total attempt/deadline budget bounds the execution. The immutable graph rejects cycles and missing registrations before creating clients. Retries keep the profile; eligible exhausted failures can change platform. The prompt/parameters/output contract stays fixed. Profile-specific options must be resolved for the destination instead of carrying native SDK options across platforms.

Fallback telemetry is required: exactly one `llmcmd.profile.fallback` command-span event and `llmcmd.profile.fallbacks` counter increment per selected transition. Separate provider-attempt spans retain each failure and the actual serving platform/model. Counter dimensions use stable configured profile IDs and finite reasons, without run/execution/trace IDs, prompt text or parameters. `llmcmd.provider.attempts` and `llmcmd.provider.attempt.duration` expose calls and latency, with OTel exemplar correlation where sampled. The execution snapshot retains attempts independently of sampled telemetry. A successful fallback remains command success. Cancellation, exhausted execution budget and noneligible errors stop routing.

Create or clone MEAI `ChatOptions` per invocation. `Clone` is not a universal deep-copy guarantee for arbitrary objects in tools, raw factories, and additional dictionaries. Snapshot serializable settings for collection rather than retaining mutable options or executable delegates.

## MEAI middleware ownership and order

Build the pipeline once per registered provider configuration, through injected `ChatClientBuilder` configuration. Request state belongs in a scoped execution context, not in mutable singleton middleware. MEAI already supplies tool invocation, caching, and telemetry; reuse those facilities. The library adds command identity, resolution, rendering, and the eval envelope.

Recommended default order, outside to inside:

```text
Executor: resolve, validate, render, start one logical execution
  command activity
    optional cache with explicit cache-result observation
      MEAI tool invocation
        retry / rate limit policy for a single model call
          any option/message mutation
            provider-attempt observer + optional MEAI model telemetry
              owned provider client
Executor: finalize once, enrich, queue immutable record
```

The executor is the outer boundary even on cache hits or validation failure. The observer must be inside mutations/retries to capture actual client-boundary inputs. The spike tests this order by changing model/text in middleware and retrying a failed provider call. MEAI build ordering is also visible in [the version-pinned builder source](https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/ChatClientBuilder.cs).

Inject newly created, **owned** raw clients through factories if the executor owns disposal. Do not pass `() => services.GetRequiredService<IChatClient>()` into an owning factory API. For DI-owned clients, implement a distinct non-owning adapter/lifetime contract. A prebuilt client with hidden middleware limits what the observer can see; it observes the client boundary, not exact HTTP payload bytes.

Use a scoped context/AsyncLocal for correlation and restore the previous value in finally. Do not place unique execution IDs, caller paths, or parameter values into `ChatOptions.AdditionalProperties`: MEAI cache keys include options, and provider adapters may interpret additional properties. The spike proves concurrent command isolation for the tested asynchronous flow. Parallel hedging or tool children require a thread-safe attempt accumulator and explicit parent/child IDs.

Retry inside a tool loop so it retries a model request, not tool side effects. Record attempts that fail with unknown usage. Audit SDK/HTTP retry policies to avoid accidental retry multiplication. Cache eligibility depends on tool side effects and experiment policy. An absent provider attempt alone is not proof of a cache hit. Cache reads can carry historical response usage; preserve it as response metadata and price only attempts actually incurred by this execution.

MEAI supports both full responses and streaming. A `Use(nonStreamingDelegate, null)` handler can adapt streaming through a buffered response; it does **not** prove streaming support. Production capture must implement both paths, record first-token time and terminal status, finalize on disposal/cancellation, and mark abandoned streams partial. Preserve UsageContent by provider attempt without double-counting cumulative updates. Test tools plus streaming before claiming usage completeness. See [the pinned anonymous middleware source](https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/AnonymousDelegatingChatClient.cs).

## Call-site capture

Declare CallerMemberName, CallerFilePath and CallerLineNumber on the outermost API the application calls. Forward them explicitly through generated wrappers; placing them only on the underlying executor captures the wrapper. Offer an explicit CallSite object for shared helpers. Separate command creation location from execution location if both are useful. The spike captures the direct executor caller; generated wrapper forwarding is not integrated yet.

These attributes supply source literals, not an unspoofable runtime identity. Normalize file paths with deterministic build/path mapping for portable records. Keep caller line/path out of metric dimensions. See [the compiler caller-information rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/attributes/caller-information).

## Execution data and cost accounting

Store a schema-versioned immutable envelope containing:

- Execution ID, trace/span when available, UTC start, duration, outcome/error category, delivery receipt.
- Stable prompt ID and compiled/effective revisions/hashes.
- Authored raw message templates and effective raw templates, plus fully rendered role/content messages.
- Caller values, effective values, type/schema IDs, origins and override permissions.
- Requested profile/provider/model, resolved effective settings, returned model/deployment identity.
- Response messages, finish reason, structured-output/schema information and parse outcome.
- Per-provider attempts: parent IDs, sequence, request/settings snapshots, response model/ID, timings, full nullable usage snapshot and outcome.
- Experiment, variant, dataset case, baseline and application build identifiers.

Production should support system/user/assistant roles and non-text content descriptors. The prototype uses one user message and captures text. Preserve content hashes or controlled artifact references for large binaries. Never serialize tool delegates or SDK object graphs as an eval format; capture versioned names/schemas/results instead.

Keep full usage fields: input/output/total, cached input, reasoning, modality-specific counts, and provider-specific additional counts. Missing means unknown, not zero. Some categories overlap totals; summing every field is incorrect. The response's aggregate usage and per-attempt usage are alternative views. Bill from authoritative provider attempts once; never add final response aggregate to those attempts. The prototype labels top-level counts as response counts.

`ITokenCostEnricher` receives immutable attempts and routing metadata. Select rates by actual provider/deployment/response model and effective rate version. Return Known(amount,currency,rateVersion,coverage) or Unknown(reason,knownSubtotal) rather than fabricating zero. Price cached reads/writes, reasoning and modalities according to their provider rules, not generic addition. Include failures whose billed usage is unavailable in coverage. Keep evaluator/judge-model usage in separate linked executions. The prototype uses explicitly fictitious flat rates and returns unknown for missing/unsupported usage.

Use MEAI OpenTelemetry for model spans/usage, plus a command activity and bounded library metrics. Bodies, dictionaries, output and caller details belong in the controlled collector. Counts/outcomes/latency/token measures belong in telemetry; execution IDs and raw values must not be metric labels. OTel sampling is useful for diagnosis but cannot be the only dataset collection policy. MEAI telemetry is a building block, not a complete replay envelope.

Give the collector a delivery contract: accepted-to-queue is distinct from persisted. Use ExecutionId as an idempotency key, a bounded channel, explicit backpressure/drop policy, shutdown drain, and observable delivery failures. Stronger requirements can use an outbox. The spike performs timeout-bounded inline collection; it does not prove durability or recovery. A collector or pricing failure must preserve a successful model result and report its own delivery/enrichment status.

## C# 15 results and exception boundary

Use closed result cases for expected domain failures: invalid template/parameter/settings, unavailable configuration, provider failure/timeout, caller cancellation, output parse failure, and success. Use independent enrichment and collection receipts. Put one finalization point after execution classification.

Use a native `closed record` with public sealed leaf cases. The compiler checks exhaustive switches; warnings for missing cases should fail the build. A union is useful when grouping unrelated existing types, but its default struct contents can be null. Handle an explicit `null` arm at boundaries instead of a catch-all that hides future cases. The collector adapter spike uses a native union. The earlier C# 11 private-record-constructor workaround was discarded; its synthesized protected copy constructor did not actually close the hierarchy.

MEAI and network providers throw exceptions. Convert expected dependency failures at the executor boundary; do not use exceptions for local validation. Only classify an OperationCanceledException as caller cancellation when the caller token is cancelled. An uncancelled-token OCE can indicate a dependency timeout or another internal abort and must remain distinguishable. The final runtime tests include throwing source, provider, collector and enricher, plus cancellation and timeout-like cases. This is not a promise that programming errors, process termination, resource exhaustion, or arbitrary blocking plugins can be made exception-free.

## Eval and replay workflow

Capture execution, select dataset cases, freeze a resolved baseline, run variants on the same cases, evaluate, compare, and retain lineage. Replay should accept a pinned resolved plan and input snapshot so it does not fetch today's Redis values. A variant can explicitly choose a new template, parameter set, profile/model, or settings; validate its schema before execution. Record the change and parent execution.

Separate deterministic checks from model-based evaluation. Deterministic checks include formatting, JSON schema, expected facts, denied/allowed content, and application constraints. MEAI Evaluation provides IEvaluator, NLP metrics, quality evaluators, and reporting facilities; integrate through an adapter rather than inventing a second evaluator ecosystem. See [Microsoft's evaluation library overview](https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries).

Compare variants with paired dataset cases and repeated runs where output is stochastic. Track quality together with latency, known/unknown cost coverage, errors, and complete/partial outcomes. Pin evaluator/judge prompt/model versions and reference answers. A fixed seed does not guarantee identical outputs across providers or model revisions. Tool side effects require a recorded/fake tool environment for replay. Sampled production traffic is a dataset source, not ground truth.

No actual prompt-quality evaluation, judge call, Redis deployment, or real pricing measurement ran in this session. All provider responses and rate values in the execution tests are fake.

## Relationship to SKPromptGenerator

The requested reference is [CharlieDigital/SKPromptGenerator](https://github.com/CharlieDigital/SKPromptGenerator), reviewed at commit `cfb1bc1d0f2851821d3e66575ee52392d2609382` / package 0.5.1. It demonstrates typed constructor parameters from annotated constant prompt templates. Reuse that authoring idea and the source-generator lessons. Its C# 12 syntax is compatible with our C# 15 target. Its Semantic Kernel runtime, immediate interpolation, and OpenAI-oriented settings still differ from the required MEAI descriptor/capture architecture. The [primary-source review](primary-source-research.md) lists exact source files and findings.

## Pluggable providers, sinks and human feedback

Keep three independent ports. Provider/protocol adapters enrich a normalized MEAI response at the model-client boundary. Execution collectors persist the canonical envelope. Telemetry/eval sink adapters translate that envelope to OTLP or a vendor's feedback API. This avoids a provider-by-sink matrix of custom implementations.

Read MEAI normalized usage first; preserve inclusive input/output totals, overlapping cache/reasoning details, units, and source/conflict metadata. Anthropic cache-write TTL and thinking details are concrete native enrichment targets. DeepSeek using an OpenAI-compatible SDK remains DeepSeek. Select adapters by explicit provider registration and API protocol; never guess from a model name. Native SDK projection belongs in optional packages, not a reflection-based core serializer. See [provider source mappings](provider-adapters.md).

Langfuse, LangSmith (the LangChain observability service), Logfire and Braintrust have OTLP profiles with different paths, headers and supported signals. Local wire tests verify exporter output, not remote acceptance. Evaluation feedback is a separate capability: it is not portable merely because the target accepts traces. Unknown OTel-span-to-vendor-run addressing remains a gate for some feedback APIs. See [collector adapter contracts](collector-adapters.md).

The human-feedback spike separates stable RunId, per-call ExecutionId, immutable output artifact/version, and append-only grade/correction event IDs. A correction requires an exact OutputRef, RFC 6901 pointer and expected old value. It creates a new version; grades keep the version the reviewer saw. Follow-up HTTP requests create their own traces, link to the stored extraction context, and carry stable run/execution/version attributes. Actor/tenant identity comes from authenticated application context, never from baggage or traceparent. The ledger is an in-memory prototype; persistence, schema revalidation and outbox delivery remain production work. See [human grading design](human-grading.md).

Custom `Action`/`Func` hooks resolve to frozen values before model execution. Store effective values and named hook versions; do not serialize delegates. The command-root spike proves immediate immutable settings patches and caller forwarding. Throwing customization hooks require a distinct outcome; collection failure must not discard a valid model result. Command instances can be shared only when request authorization context is resolved per execution or per scope.

## Evidence and remaining gates

The root verification runner builds spikes, runs positive/negative compiler cases, and executes TUnit contract tests. TUnit APIs and reporting were checked against the requested `tunit.dev/llms.txt`. A separate Aspire stack runs a real OTel Collector and actual OTLP exports. Receiver-side TUnit tests compare GenAI spans/metrics with interface outputs and verify multi-HTTP extraction/correction/grading links. Model responses and rates are fake. No application model keys or remote collector credentials were configured in the session. Read each spike README for exact commands and limits.

Proven here: generated typed binding and descriptors; hidden runtime execution; typed result/caller propagation; immutable settings patches; required/optional/nullable values; any-order generic typestate; runtime override validation; retry observation; usage/cost unknown handling; collector/enricher isolation; OTLP transport and receiver data; sink profile wire configuration; and immutable human grading/correction correlation. This does not prove that every spike has been combined into one production pipeline.

Next implementation gates, in order:

1. Consolidate the generated command facade, generic runtime, provider extraction, override/cost capture and sinks behind one versioned contract. Extend the simple-record schema/telemetry matrix and parser parity tests. Add generated DI registration and validate profile defaults at startup.
2. Add real MEAI tools, cache-hit observation, and streaming/disposal cases. Verify no duplicate usage accounting or tool retry side effects. Multiple text roles and structured output already have focused proofs; multimodal/general schema support is not complete.
3. Add a real versioned Redis snapshot adapter and model-profile/config resolver. Test timeouts, atomic revisions, pinned experiments and explicit fallback policies.
4. Add durable execution/grade stores, schema validation of human corrections, an outbox and replay/eval integration. Test delivery receipts, retry/idempotency, shutdown, retention and migration. Then validate a real provider/rate-card adapter with configured credentials.
5. Test NuGet analyzer packaging, cross-assembly attributes/IDs, incremental caching, supported SDK/TFM combinations, IDE behavior and generated-source size before declaring the public API stable.

This sequence is a proposed implementation path. Streaming, Redis, durable persistence, and eval replay remain unimplemented; none is claimed complete by the current spike tests.
