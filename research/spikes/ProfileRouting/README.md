# Profiles, provider routing, retries and fallback

This .NET 11 / C# 15 experiment connects the generated CommandRoot command to a profile-owned execution policy. One typed parameter record, prompt definition/hash, output type, RunId and ExecutionId survive retries and fallback. Separate calls have separate ExecutionIds. Provider registrations are explicit; model names do not select providers.

`ExecutionProfile` contains a stable `ProfileKey`, provider, model, temperature, retry policy, fallback and optional attempt timeout. `ExecutionBudget` limits total attempts and elapsed time across the route. `MaxRetries` excludes the initial attempt. The graph is copied and validated before provider factories run. Cycles, missing providers/fallbacks and invalid policies return `RouterBuildResult.Invalid`. The router owns newly-created clients. Runtime instances borrow it unless `ownsRouter: true` is explicit.

The actual source-generated catalog, host factory helper and call sites are in [ProfileExamples.cs](../CommandRoot/ProfileExamples.cs). `ProfileExamples.CreateRouter(...)` accepts separately configured MEAI client factories and model IDs. Supply its router once to `CommandRuntime.Create(router, ledger, actorProvider)`. `WithProfile<Profiles>(p => p.OpenAI)` selects a key immediately and preserves `Invocation<TParameters,TResult>`; it never retains the delegate. The generator emits the instance partial properties, singleton and interface. `LLMPROFILE001` diagnoses invalid declarations and duplicate IDs within a compilation. The legacy string overload remains for earlier spike compatibility.

Each retry reuses its profile. Eligible exhausted failures select a fallback profile, including another platform. Each attempt gets cloned messages/options, its destination's model/defaults and the explicit temperature patch. Both destinations receive the same structured-output format. The command validates the result and stores the original wire JSON as before.

## Trace and metric contract

The command span owns RunId, ExecutionId, prompt identity, requested profile and eventual served profile. Each actual client invocation has a child `llmcmd.provider.attempt` span. MEAI model telemetry can sit inside that span. Failed attempts are errors; successful fallback preserves command success.

Selecting a fallback adds exactly one `llmcmd.profile.fallback` event to the command span and increments `llmcmd.profile.fallbacks` once. This counts a transition, not a failed attempt or a successful fallback. A blocked transition does not emit. The counter dimensions are `llmcmd.profile.requested`, `llmcmd.profile.from`, `llmcmd.profile.to`, `llmcmd.fallback.reason`. Profile names must be stable configured identities, never per-run or per-user identities. Run/execution/trace IDs, parameters, prompt text and exception messages are excluded from metric labels.

`llmcmd.provider.attempts` counts invocations. `llmcmd.provider.attempt.duration` is a seconds histogram. They carry actual profile/provider, attempt kind and outcome. Exemplars can link measurements to sampled traces. Do not sum repeated cumulative exports. The execution snapshot separately keeps immutable attempt records, actual response model/ID and available usage. Failed/missing usage stays unknown. This module does not price attempts or add final-response usage to attempt usage.

## Policy and verification

The default classifier recognizes HTTP 429, HTTP 408/5xx, transport errors and timeouts. Other errors do not retry or fall back. Caller cancellation stops; an unrequested provider cancellation is a timeout. Registration-specific classifiers support SDK exception shapes. A throwing classifier stops as permanent. Input and structured-output validation failures outside the provider call do not trigger fallback.

One deadline and attempt limit cover the whole route. Retry uses capped exponential backoff and optional jitter. `WaitAsync` bounds noncooperative asynchronous providers and observes later faults. Synchronous blocking before a provider returns a Task cannot be bounded. An abandoned remote request may still incur cost.

TUnit in `tests/LlmCmd.CommandRoot.Tests/ProfileTests.cs` checks the actual generated command against two fake providers, identical prompts/hashes, typed output, per-execution identity, retries, fallback, permanent failures, cancellation, deadlines, total attempt limits, concurrency, graph validation, single notification, span hierarchy and metric labels. Compiler cases cover generated catalogs/selectors, wrong types/members and declaration diagnostics. The Aspire receiver test checks actual OTLP after two synthetic OpenAI failures and Anthropic success. Its healthy control has no fallback event.

No hosted model or vendor endpoint is called. Real SDK failure classifiers, Retry-After handling, full provider extraction/pricing, external binding/reload, DI discovery, durable collection and capability checks remain production gates. The example factory helper uses the generic HTTP classifier; real SDK registrations must supply and verify theirs. Hidden SDK/HTTP retries are not separate MEAI attempts.

Streaming is explicitly unsupported, rather than buffered. Tool-bearing requests are rejected: routing must be inside model calls without replaying tools. Hidden middleware in a supplied client is a host contract. Clone methods do not deep-copy arbitrary tools/raw factories/content. Provider-specific options need destination-specific mapping. For isolated provider comparisons, configure profiles without fallback or use the recorded actual provider.

The catalog generator supports public sealed top-level partial classes, public instance partial getters and constant IDs. Nested catalogs, incremental caching, IDE completion, packaging and the full diagnostic matrix remain gates. Nominal keys and diagnostics prevent ordinary mistakes; they are not a security boundary. Follow the [observability README](../Observability/README.md) for collector execution. Reports are under `/tmp/opencode/llmcmd-commandroot` and `/tmp/opencode/llmcmd-otel/fallback-evidence.json`.
