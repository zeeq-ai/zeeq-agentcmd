# Provider response extraction spike

Provider adapters read a MEAI `ChatResponse` (plus optional provider wire JSON) and produce one immutable, per-attempt `ResponseSnapshot`. Sink converters (Langfuse, LangSmith, Braintrust, collectors) consume that canonical snapshot downstream. They never see a provider shape, so provider and sink counts do not multiply. Source-pinned research: `docs/research/provider-adapters.md`. This is a research spike. No live vendor or model call is made, no key is read, and the core has no vendor SDK dependency (a test checks the assembly references).

```sh
mise run test:provider-adapters
```

Use the SDK selected by root `global.json` and `mise`. `mise run test:provider-adapters` runs this suite. Pins: net11 / C# 15, Microsoft.Extensions.AI 10.11.0, TUnit 1.78.0.

The separate [explicit live-provider test project](../../tests/LlmCmd.LiveProviders.Tests/README.md) supplies pinned OpenAI and Anthropic native projectors at the integration boundary. It can read its own user-secrets directly without Aspire. Its offline checks exercise actual SDK bridges; live API success requires explicitly running its paid tests. The fixtures and SDK-free core described below remain unchanged.

## Fixtures are synthetic

`tests/LlmCmd.ProviderAdapters.Tests/Fixtures/*.json` are **hand-written**, not captured from any provider. Each one carries `fixture.synthetic: true`, `live: false` and a note (a gate test enforces this). The `meai` section hand-simulates what an MEAI bridge projects. It is not the output of the production OpenAI or Anthropic bridge. The `wire` section follows the field names in the research doc. Passing tests prove the **mapping rules** over these shapes. They do not prove the shipped SDKs, live gateways or provider invoices behave this way.

## Contracts

| Piece | Role |
|---|---|
| `ProviderRegistration(providerKey, clientRegistrationId, ApiKind, protocolRevision?, endpoint?)` | Explicit identity. The endpoint keeps scheme, host, port and path only. |
| `IProviderResponseAdapter` | Pure `Enrich(AdapterContext)` over wire JSON, plus declared stream rule and a MEAI `AdditionalCounts` key map. Platform modules: `OpenAiChatAdapter`, `AnthropicMessagesAdapter`, `DeepSeekChatAdapter`. |
| `INativeResponseProjector` | Optional bridge from a native SDK object to wire JSON. It belongs in an optional package. **None ships here.** |
| `ProviderAdapterRegistry.Resolve` | Closed `Matched \| NormalizedOnly \| Ambiguous`. A provider-specific rule beats a protocol-generic rule (`providerKey: null`, host opt-in). Equal precedence is `Ambiguous`, never first-match. The model name and SDK type are never consulted. |
| `ProviderExtractor.Extract / Begin` | Resolves the adapter once per attempt, reads the baseline, and runs the adapter on a clone. A throw drops the clone, keeps the baseline and adds `adapter-failed`. |
| `ExtractionResult` (closed) | `Extracted \| Partial \| Invalid`, each carrying the snapshot. An `Error` diagnostic gives `Invalid`. A `Warning` or any conflict gives `Partial`. An unsupported native object, an unmatched provider and an ambiguous registry are `Partial`. This folds the research doc's `UnsupportedShape` into `Partial`, with a diagnostic code. |
| `ProviderExtractionChatClient` / `UseProviderExtraction` | Real `DelegatingChatClient`. Returns the inner response untouched. Sends one `ExtractionResult` per attempt to `IExtractionSink`. Put it innermost, below retry, failover and tool-loop middleware. |

Evidence is kept, not summed. One `UsageMeasurement` exists per dimension. It holds `Value`, `Unit` (Tokens, Requests, Unknown), `Relation` (Total, SubsetOfParent, PartOfParent, Independent, Unknown) and `Parent`. It also holds every `Observation` with its origin (Normalized, NormalizedAdditional, Native, Computed), source path and note. Agreeing aliases collapse into one measurement with several observations. A disagreement keeps the **normalized** value, sets `Status = Conflicted` and adds a `UsageConflict`. Nothing is clamped, maxed or replaced silently. A missing dimension is unknown, never zero. A normalized zero stays zero, but only a `Native` observation shows the wire carried the field.

Failure isolation: adapter, projector, sink and extractor defects never change or lose the model response, and `OnFailure` (itself guarded) is told. Cancellation is recorded as `Cancelled` only when the caller's token is cancelled. An exception from a live token, such as an SDK timeout, is `Failed`. Streaming finalizes exactly once. A provider stream dispose failure still publishes the attempt as `Failed` and keeps the provider's exception. Raw-content policy and `OnFailure` have a single source: `ProviderExtractor.Options`.

## Mapping contracts

| Provider | Rules implemented |
|---|---|
| OpenAI Chat Completions | Input, output and total are inclusive. Cached, audio, reasoning, accepted and rejected prediction tokens, and `cache_write_tokens` (noted as unadjusted) are subsets and are never added. MEAI `AdditionalCounts` keys `InputTokenDetails.AudioTokenCount`, `OutputTokenDetails.AudioTokenCount` and `OutputTokenDetails.Accepted/RejectedPredictionTokenCount` map to the same dimensions. Total is checked against input + output. Unknown usage keys are diagnosed by name and kind only. Their values are not extracted. |
| Anthropic Messages | Native `input_tokens` is **uncached** (`input.uncached`). Inclusive input = uncached + cache read + cache creation, recorded as a `Computed` observation and compared with MEAI `InputTokenCount`. A non-normalizing client therefore shows a conflict. Cache write is the aggregate. The 5m/1h fields are `PartOfParent` of it and are checked against it, never added. A split with a missing or unknown bucket is `Partial`, with no residual. Thinking tokens are within output with a re-tokenization note. `WebSearch/WebFetch` are `Unit.Requests`, `Independent`, and never go into a token total. `stop_reason` is kept as `NativeStopReason`. A missing term leaves inclusive input unknown. |
| DeepSeek Chat Completions | Reuses the OpenAI-shaped reader. `prompt_cache_hit_tokens` is an alias of nested `cached_tokens` (one measurement, several observations). `prompt_cache_miss_tokens` is `deepseek.input.cache_miss`, a detail of the prompt total and **not** cache creation. Cached + miss is checked against input. Responses and Messages APIs of DeepSeek are not covered. |
| Any other provider | `NormalizedOnly`. Normalized totals work. `AdditionalCounts` keys are kept as `additional:<key>` with unknown unit and relation. The result is `Partial` (`no-adapter`). |

## Limits (read before relying on this)

- **Streaming is partial by design.** Streaming reads normalized `UsageContent` only. A latest-snapshot rule applies when the adapter declares one (cumulative or terminal frames replace each other and are never summed). Without a declaration the latest frame is kept and `stream-usage-frames-ambiguous` is raised. Native stream events are **not extracted**, so every streamed result carries `stream-native-not-extracted` and is `Partial`, never `Extracted`. Layout differences are tested only through `UsageContent`: an OpenAI usage-only final update versus a DeepSeek usage on the last content update. A delta rule is not implemented. Native stream layouts, accumulator reconciliation with native evidence, abandoned-stream native partials and resumed-response lineage are future gates.
- **No native SDK support ships in the core.** Adapters read wire-shaped `JsonElement`, `JsonDocument` or `JsonNode` from `RawRepresentation`, or explicit raw JSON. The original suite uses a test-local native stand-in. The separate live-provider test project adds real pinned SDK projectors and offline bridge checks; those are integration-test code, not packaged SDK support. `RawRepresentation` is lost on serialization (`native-unavailable`).
- **Per attempt only.** Place the middleware innermost. An outer tool-loop or retry aggregate is an alternative view of the same attempts. Do not add it to the children (a test shows the outer total equals the sum of children while only the children are recorded). Provider-hidden SDK or HTTP retries, response-cache hits with historical usage and resumed-response de-duplication are not covered.
- Raw content is off by default, bounded when enabled, and never includes tool arguments or the native object graph. Snapshots hold no `object` fields and use no reflection over SDK graphs.
- MEAI's experimental `InputTextTokenCount` / `OutputTextTokenCount` are not read. Audio ones are.
- Pricing, sink conversion (exclusive flat buckets for Langfuse, inclusive totals with detail subsets for LangSmith), OTLP export and feedback are out of scope here and live in the pricing enricher and `CollectorAdapters`.
