# Provider response extraction adapters

Research date: **2026-11-01**. Scope: model-provider response extraction at a MEAI client boundary. This is separate from the telemetry/export/feedback sinks in [collector-adapters.md](collector-adapters.md). This document proposes contracts and fixture tests; it adds no implementation and makes no live model calls.

## Recommendation

Use a **normalized MEAI reader followed by a registered provider/protocol enrichment adapter**. Keep inclusive totals and their overlapping detail dimensions internally. Convert to a sink's accounting schema once, at export. Do not adopt a tracing platform's usage dictionary as the internal billing model.

Select adapters from the application's provider/client registration, API protocol, and supported SDK shape. Never select by a model-name substring. An `OpenAI.Chat.ChatCompletion` can come from DeepSeek, a proxy, Azure, or another compatible service. Client metadata is useful evidence, but the configured registration must identify the actual provider and protocol when an SDK wrapper cannot.

The adapter reads usage and response metadata. It does not resolve prices, export telemetry, persist raw responses, or retry model calls. Keep those responsibilities separate.

## Sources and exact revisions

Source below was read at these commits. A repository HEAD is a source snapshot, not a claim that every change is in a particular published NuGet/PyPI release. Runtime compatibility must be verified against the versions actually referenced by a provider package.

| Source | Inspected revision | Date |
|---|---|---|
| dotnet/extensions, MEAI 10.11.0 release | `25f07afa51034e37b334210642d7dc5f191c142d` | package published 2026-10-14 |
| anthropics/anthropic-sdk-csharp, official SDK | `a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a` | 2026-10-23 |
| langfuse/langfuse-python | `1fc854dd2b241cf8e3dac38bfd5907ff2fe365e3` | 2026-10-30 |
| langchain-ai/langsmith-sdk | `290f06a628dffb2ca690e389dc48b629f5623312` | 2026-10-16 |
| open-telemetry/opentelemetry-python-genai | `3747f16c86668e6dba990ceea30fced9298b8851` | 2026-10-31 |
| langfuse/langfuse, OTel ingestion source | `a4467fd418002ed0ee29771277aa9625484d8cb8` | 2026-10-28 |

Provider API pages cited below were checked on the research date. Their live schemas can evolve independently of these SDK revisions.

## What MEAI already preserves

Read `ChatResponse.Usage` first: input, output, total, cached-input, reasoning and all `AdditionalCounts`. Copy nullable counts and dictionary values into an immutable snapshot. Read response/model IDs, finish reason, messages and recognized `AdditionalProperties` separately. There is no universal schema for arbitrary additional properties.

The inspected [UsageDetails source][meai-usage] also exposes experimental input/output audio/text properties. Preserve them when supported by the pinned package; do not assume a provider fills them merely because they exist. `UsageDetails.Add` sums corresponding fields and dictionary keys across responses. It does not know whether a response represents a cumulative stream snapshot, and a non-null sum can still omit usage from unknown attempts. Summable across independent calls does not mean that different detail keys are mutually exclusive categories within one call.

`RawRepresentation` is an opaque native object and is JSON-ignored. A cache/serialization round trip can remove it. The [ChatResponseUpdate documentation][meai-update] explicitly warns that aggregating streaming updates into one `ChatResponse` can lose per-update native representations and other information. Therefore, inspect provider extras while each update passes through middleware. Do not rely on recovering them from a final aggregate.

The same fact can appear in normalized usage, additional counts, native usage and a raw JSON field. Those are multiple pieces of evidence, not additional consumption. Keep the normalized value by default; fill missing fields from an adapter; report disagreement rather than silently adding or replacing conflicting values. A documented bridge-version correction can override a value, but must name the rule and preserve both observations.

An SDK may materialize an absent numeric property as zero. Preserve a normalized zero, but do not claim it proves the wire field was explicitly present. Wire presence is separate provenance when available. Missing/null usage must remain unknown rather than becoming zero.

## Verified provider mappings

### OpenAI Chat Completions and Responses

MEAI 10.11.0's [OpenAIChatClient][meai-openai], method `FromOpenAIUsage`, maps the following native values:

| Native SDK field | MEAI field |
|---|---|
| `ChatTokenUsage.InputTokenCount` | `InputTokenCount` |
| `OutputTokenCount` | `OutputTokenCount` |
| `TotalTokenCount` | `TotalTokenCount` |
| `InputTokenDetails.CachedTokenCount` | `CachedInputTokenCount` |
| `OutputTokenDetails.ReasoningTokenCount` | `ReasoningTokenCount` |
| input audio | `AdditionalCounts["InputTokenDetails.AudioTokenCount"]` |
| output audio | `AdditionalCounts["OutputTokenDetails.AudioTokenCount"]` |
| accepted predicted-output tokens | `AdditionalCounts["OutputTokenDetails.AcceptedPredictionTokenCount"]` |
| rejected predicted-output tokens | `AdditionalCounts["OutputTokenDetails.RejectedPredictionTokenCount"]` |

The API wire fields are `prompt_tokens`, `completion_tokens`, `total_tokens`, `prompt_tokens_details.cached_tokens`, and `completion_tokens_details.reasoning_tokens`. Cached input and reasoning output are detail counts within their respective totals; do not add them to those totals. The [current official API reference][openai-chat-api] also defines input/output audio and prediction details. Rejected prediction tokens still contribute to billed completion tokens. Its current prompt detail schema additionally exposes `cache_write_tokens` as an **unadjusted** write count and image/text detail fields; MEAI's inspected mapping does not project all these fields. Preserve them as versioned provider extras when actually present. Do not infer their presence from a model name or copy an older price formula onto new fields.

[OpenAIResponsesChatClient][meai-responses], method `ToUsageDetails`, maps input/output/total, cached input and reasoning to the same MEAI fields. It does not populate the Chat Completions additional-count keys above. Treat Responses and Chat Completions as separate API kinds. Both retain their native complete-response objects; their native stream-event shapes differ.

For OpenAI Chat Completions with `stream_options.include_usage`, the official schema sends a final usage-only chunk with empty `choices`. Other chunks have null usage; interruption may omit final usage. A collector that only reads nonempty choices loses accounting. That is a provider/protocol rule, not a rule for all compatible APIs. The MEAI chat adapter turns SDK token usage into `UsageContent`; collect it once for the attempt.

MEAI also reads compatible-endpoint `reasoning_content` from the SDK JSON patch at `choices[0].message.reasoning_content` and its streaming `delta` equivalent and projects `TextReasoningContent`. The source deliberately uses choice zero here. Do not claim it preserves every native choice's custom fields. Reasoning text is content, not a token-count estimator.

### Anthropic Messages: official .NET SDK and its MEAI bridge

The official SDK **does provide `AsIChatClient`**, in [AnthropicClientExtensions.cs][anthropic-meai]. It is not necessary to invent a transport or depend on an unrelated third-party adapter. This file contains the bridge's response and streaming conversion.

The native usage semantics differ from OpenAI. Anthropic's [prompt-caching documentation][anthropic-cache] defines native `input_tokens` as uncached input. Total input is native input plus `cache_read_input_tokens` plus `cache_creation_input_tokens`. The 5-minute and 1-hour fields in `cache_creation` partition the aggregate creation count. They must not be counted again on top of it.

The inspected bridge already performs that normalization:

| Native field | Existing MEAI projection | Enrichment needed |
|---|---|---|
| `input_tokens` + cache read + cache creation | inclusive `InputTokenCount` | retain original uncached value and provenance if required |
| `cache_read_input_tokens` | `CachedInputTokenCount` | alias only; do not add again |
| `output_tokens` | `OutputTokenCount` | none for aggregate |
| inclusive input + output | `TotalTokenCount` | computed by bridge; record derived provenance |
| positive `cache_creation_input_tokens` | `AdditionalCounts["CacheCreationInputTokens"]` | preserve aggregate separately from TTL partitions |
| positive server-tool counters | `AdditionalCounts["WebFetchRequests"]`, `["WebSearchRequests"]` | unit is **requests**, not tokens |
| `cache_creation.ephemeral_5m_input_tokens` / `.ephemeral_1h_input_tokens` | not projected by inspected bridge | read native `Usage.CacheCreation` |
| `output_tokens_details.thinking_tokens` | not projected by inspected bridge | read native `Usage.OutputTokensDetails` |

The official SDK models expose [CacheCreation][anthropic-cache-model] and [OutputTokensDetails][anthropic-thinking-model]. Thinking tokens describe underlying reasoning, not the length of returned/summarized thinking text. They are within inclusive output; the model documentation says their count is obtained by re-tokenization and may differ slightly from generation accounting. Preserve that provenance. Mapping to a generic reasoning dimension is defensible, but must retain the provider field and its measurement method. Do not add it to output totals or derive it from visible text.

The bridge only adds positive cache-creation/server-tool counts to `AdditionalCounts`. Absence of those keys therefore does not distinguish zero from unavailable. A native adapter can preserve explicit zero where the native schema exposes it. Native getters can reject malformed shapes, so the extraction boundary must handle validation failures without changing model execution results.

For streaming, [Anthropic's protocol documentation][anthropic-stream] states that `message_delta.usage` is cumulative. The C# bridge remembers starting input/cache counts, replaces its usage snapshot from later cumulative deltas, and emits **one final `UsageContent` after enumeration**. Earlier MEAI updates still contain native event objects. Do not sum those native snapshots together or add them to final normalized usage. On abandonment/failure before the final normalized update, retain the latest available native partial snapshot and mark coverage partial. Missing fields in a later partial delta must not erase known starting fields.

The bridge maps end-turn/stop-sequence to Stop, max-token/context-window limits to Length, tool-use to ToolCalls, and refusal to ContentFilter. It preserves other stop strings, including `pause_turn`, rather than always treating them as normal completion. Preserve both normalized and native stop reason. A tool request, a paused server-tool turn, and a completed user answer are different outcomes.

### DeepSeek Chat Completions through OpenAI-compatible clients

The [current DeepSeek Chat Completions schema][deepseek-chat-api] defines:

- `prompt_tokens = prompt_cache_hit_tokens + prompt_cache_miss_tokens`.
- `prompt_tokens_details.cached_tokens` is the same count as `prompt_cache_hit_tokens`.
- `total_tokens` is prompt plus completion; `completion_tokens_details.reasoning_tokens` supplies reasoning detail when present.
- `message.reasoning_content` / `delta.reasoning_content` is separate from answer `content`. Tool deltas use indexes/IDs and can contain argument fragments.

Prefer normalized cached/reasoning fields if the installed OpenAI/MEAI adapter already projects the nested compatible fields. Enrich from the native SDK JSON patch only for actual additional fields, such as hit/miss counters or a missing normalized value. Hit and nested cached count are aliases. A hit/miss sum that disagrees with prompt total is an inconsistency, not a reason to silently clamp data. Do not assume a DeepSeek prompt-cache miss represents an Anthropic-style separately billed cache-creation category.

The live DeepSeek schema now specifies usage on the **last choice-bearing chunk**, with no separate usage-only chunk. `include_usage` controls whether preceding chunks expose null usage. This differs from OpenAI's empty-choice final chunk. Test both layouts, and retain the protocol/source revision in fixtures. A gateway can implement an older or modified layout; compatibility is not established merely by using the OpenAI SDK.

This research verified DeepSeek's Chat Completions fields. Its separately listed Responses and Messages APIs are **not** verified by this mapping. Register distinct adapters/protocol versions before claiming support for them.

## How the tracing OSS extracts provider shapes

### Langfuse Python

The inspected [OpenAI wrapper][langfuse-openai] instruments separate OpenAI methods, unwraps supported SDK response wrappers, extracts response content and usage, and has distinct streaming reducers. `_parse_usage` copies dict/native usage and preserves recognized nested input/output detail objects while removing null values; it does not itself implement every provider's accounting semantics. Embedding usage gets a separate special case. `_extract_streamed_openai_response` keeps the latest non-null usage while assembling text and tool-call deltas. Stream close/finally paths have an idempotent finalizer. Some raw streaming paths are deliberately passed through because tracing them would consume the caller's body.

Its [LangChain callback parser][langfuse-langchain] uses a different path: known field translations, nested detail flattening, cache detail handling and an integer filter. Exact OpenAI-shaped dictionaries are left for server-side handling. Other shapes, extra fields, service-tier keys and provider details take different branches. This is evidence for explicit schema adapters and fixtures, not evidence for one universal reflection-based parser.

Langfuse's official [usage contract][langfuse-usage-doc] says flat `usage_details` keys must be non-overlapping buckets; `input` excludes `input_*` detail buckets and `output` excludes `output_*`. OpenAI-shaped usage is recognized only under a strict schema; extra keys can cause flat treatment instead. Its [OTel ingestion source][langfuse-ingestion] contains separate extraction/normalization paths for native Langfuse fields, OpenAI usage and generic GenAI attributes. Thus parser location and SDK/server revision both matter.

**Reuse:** method-specific extraction, explicit schema branches, latest-usage streaming reduction, idempotent finalization. **Do not copy:** treating every integer as tokens, flattening unknown fields into billable buckets, or subtracting arbitrary details without knowing their overlap. Audio, cache, prediction and service-tier dimensions cannot be assumed to form one partition.

### LangSmith wrappers and usage metadata

[The OpenAI wrapper][langsmith-openai] converts full results and streaming reductions into `usage_metadata`. Its shared [usage mapper][langsmith-usage] accepts Chat Completions and Responses key names, keeps inclusive input/output totals, and maps cache-read, audio and reasoning details. It handles priority/flex service tiers explicitly. The inspected mapper does not automatically retain every provider-specific numeric field or every prediction detail.

[The Anthropic wrapper][langsmith-anthropic] has a separate usage mapper. It adds cache read/creation to native uncached input. When TTL detail is present, it uses the 5-minute and 1-hour counts instead of also adding aggregate cache creation. Its stream reducer uses the Anthropic SDK's event accumulator and falls back to unnormalized output when it cannot accumulate a shape.

[LangSmith's schema][langsmith-schema] defines input/output totals plus optional detail dictionaries. These detail dictionaries need not cover the whole total. This differs from Langfuse's flat exclusive-bucket contract. Both are platform output schemas, not interchangeable provider wire schemas. Reuse the provider-specific mappers and detail relations; retain unknown/null information more strictly than wrappers that use zero fallbacks.

### OpenTelemetry instrumentations

The current official source is [open-telemetry/opentelemetry-python-genai][otel-repo]. Do not confuse it with similarly named third-party `opentelemetry-instrumentation-*` packages. The older contrib tree also contains OpenAI v2 instrumentation during the repository transition; source/package identity matters.

The official [OpenAI chat helper][otel-openai-chat] extracts inclusive prompt/completion, cached input, cache writes, thinking/reasoning, and audio dimensions explicitly. Its [Responses helper][otel-openai-response] reads a different usage type, preserves absent usage, and supports a cache-write fallback from a compatible cache-creation field. It does not sum arbitrary JSON keys.

The official [Anthropic extractor][otel-anthropic-extract] computes inclusive input by folding native uncached/read/write counts and extracts thinking separately. Its [stream wrappers][otel-anthropic-stream] use SDK snapshots or event accumulation, finalize at most once, and handle stream close/error. These are useful lifecycle patterns. They do not remove the need for our partial/complete coverage or TTL cache-write partitions.

## Proposed extraction contract (no implementation)

### Registration and package boundaries

Keep the core dependent only on LlmCmd abstractions and MEAI abstractions. Proposed optional packages:

- `LlmCmd.ProviderExtraction.OpenAI`: typed OpenAI SDK response/update handling for pinned SDK ranges and separate Chat Completions/Responses API kinds.
- `LlmCmd.ProviderExtraction.Anthropic`: official Anthropic SDK models and streaming events.
- `LlmCmd.ProviderExtraction.DeepSeek`: declared DeepSeek Chat Completions extension rules; compose with OpenAI-compatible extraction. It need not introduce another SDK if the configured transport already uses OpenAI.
- A core normalized-only adapter works for all other `IChatClient` implementations. Other provider packages register explicitly; no core dependency on every SDK.

Registry identity: `ProviderKey`, `ClientRegistrationId`, `ProtocolKind`, `ApiKind`, configured endpoint/profile identity, SDK package/version, and supported raw types. Endpoint identity should not retain credentials or arbitrary query strings. Use registration/metadata as authority and native type as a compatibility check. A provider-specific rule has precedence over a generic protocol rule; ambiguous equal-precedence matches are a configuration result, not nondeterministic first-match behavior. Resolve once per provider attempt and freeze adapter version/rules for that attempt.

`ChatClientMetadata.ProviderName` and `GetService` can contribute evidence, but a router's metadata does not establish which downstream provider served an attempt. Observe the selected client inside routing/retry/tool middleware. A custom registration may have no trustworthy metadata; normalized-only extraction remains valid with incomplete provider provenance.

### Logical operations and results

| Operation | Inputs | Result |
|---|---|---|
| Resolve adapter | immutable registration/client/protocol metadata and available native shape | matched adapter, normalized-only fallback, ambiguous/unsupported configuration |
| Begin attempt | execution/attempt IDs, adapter/rules version, capture policy | fresh request-local extraction state |
| Observe full response | normalized response plus optional native response | immutable baseline and provider evidence |
| Observe update | update sequence, normalized content/usage, optional native event | bounded state update; no external I/O |
| Complete attempt | completed/failed/cancelled/abandoned status | immutable extraction report and coverage |

Expected extraction results are `Extracted`, `Partial`, `UnsupportedShape`, or `InvalidShape`, always with the valid normalized baseline and bounded diagnostics when available. Unknown event/field/type does not throw or change the model result. Isolate known parse/type/overflow failures at this boundary. The report should identify the unsupported native type or field path, not dump its contents into telemetry. Programming defects remain observable as adapter failures while the executor preserves its original outcome.

### Canonical usage and provenance

Keep inclusive `InputTotal`, `OutputTotal`, and `Total` as nullable counts. Each extra measurement carries value, unit, direction, dimension, provider field path, origin, relation, and coverage. Relations include subset-of-total, partition-of-category, alias-of, independent request count, and unknown. Cache-read is a subset of inclusive input; Anthropic 5-minute/1-hour writes partition cache-write; WebSearchRequests is an independent request count. Never sum all measurements.

Preserve separate evidence for normalized MEAI fields, `AdditionalCounts`, allowlisted `AdditionalProperties`, native typed fields, and provider JSON extension paths. Record extractor ID/version, source rules revision, SDK/MEAI versions, actual provider/API identity, and whether a value was provider-reported, SDK-projected, computed, or estimated. Keep original key names under a provider namespace. Unknown extension fields can be reported by name/type without retaining their values.

Only derive a total when all necessary terms and their relations are known; label it computed. Do not treat an incomplete TTL breakdown as a proven aggregate. On disagreement, preserve evidence and mark that category unresolved for pricing. Do not silently use maxima, clamp negative residuals, invent zeros, or count aliases twice. Rate-card enrichment decides pricing and known/unknown coverage later.

### Streaming, attempts and content

Maintain one accumulator per provider attempt, not per command or singleton adapter. The adapter declares whether a particular usage event is a delta, cumulative snapshot, terminal snapshot, or unknown. Replace cumulative fields; add true deltas only under that protocol rule. Reconcile the final normalized usage with native evidence instead of adding both. When an SDK aggregator has already normalized the stream, do not normalize it a second time.

Track stream completion independently from whether text or a finish reason arrived. Finalize once on completion, cancellation, failure or disposal. Never drain a caller-abandoned stream merely to get usage. Retain observed partial counts, mark missing final accounting unknown, and release the accumulator. Resumed/polled Responses fetches may repeat the same usage; response ID/continuation lineage and adapter rules must prevent billing the same inference repeatedly.

Keep each retry/failover/model turn separate. A complete-response tool loop's aggregated usage is an alternative view of its child calls. Bill observed provider attempts once, not attempts plus final aggregate. Historical cached response usage describes the cached response; it is not fresh provider spend. Native SDK/HTTP retries hidden below this boundary remain a coverage limit unless instrumented there too.

Content extraction should preserve ordered text, reasoning, refusal, tool calls and tool results as distinct records with message/block/tool IDs and indexes. Do not append tool arguments to answer text or infer token usage from string lengths. Preserve unknown stop reasons and content kinds. Signed/redacted/opaque reasoning state is not ordinary text. This design extracts available metadata without claiming to reconstruct hidden reasoning.

### Raw inspection versus raw retention

The adapter can inspect a native object in memory to extract allowlisted scalar usage while raw retention is disabled. Do not store the native graph in the execution envelope. Raw response bodies, reasoning content, tool arguments/results, arbitrary additional properties and headers require separate opt-in capture policy, size limits, redaction and retention rules. Prefer a versioned bounded diagnostic artifact reference when raw data is explicitly requested. Count-only extraction must not accidentally serialize content, credentials or opaque provider state.

## Proposed fixture matrix and acceptance gates

These are **proposed synthetic fixtures**, not executed tests or live provider measurements. Store fixture schema/API/SDK provenance. Test the pure extraction rules and the actual pinned SDK-to-MEAI bridge separately, because projection can discard fields.

| Fixture | Required result |
|---|---|
| normalized-only custom client, no native object | valid normalized totals; unsupported extras; no guessed provider |
| null usage, absent usage, explicit zero | remain distinguishable where source shape permits; no invented consumption |
| additional-property dictionary containing unknown objects | no generic traversal/serialization; no throw |
| normalized count and native alias agree/disagree | one count on agreement; conflict evidence on disagreement |
| OpenAI input 100/output 40/cache read 60/reasoning 10 | total 140, not 210; details remain subsets |
| OpenAI audio plus cache/prediction details | retain separate dimensions; no assumption that every detail is exclusive |
| current OpenAI cache-write/image/text extension fields | known supported extras retained; unsupported new keys diagnosed without losing baseline |
| Responses usage absent, incomplete/error status, empty output | no null dereference; preserve status/error and partial coverage |
| Anthropic native uncached 10/read 80/write 20/output 40 | inclusive input 110, total 150; normalized bridge value is not incremented again |
| Anthropic write 20 split into 5m=12 and 1h=8 | write total remains 20; partitions are not added to it |
| Anthropic TTL aggregate mismatch or incomplete split | category conflict/partial coverage; no fabricated residual |
| Anthropic thinking 15 within output 40 | output stays 40; retain re-tokenized reasoning provenance |
| server web-search/fetch counters | unit=request; never included in token total |
| DeepSeek prompt 100/hit 60/miss 40 plus nested cached 60 | prompt stays 100; aliases collapse; miss is not Anthropic cache creation |
| DeepSeek hit+miss or nested cached mismatch | conflict report, baseline retained, affected pricing unresolved |
| OpenAI final empty-choice usage chunk | usage captured even with no text/choices |
| DeepSeek terminal choice-bearing usage chunk | usage captured without expecting an extra usage-only chunk |
| Anthropic start + cumulative output snapshots 5 then 9 | output ends at 9, not 14; starting input/cache values survive omissions |
| Anthropic native snapshots plus final MEAI UsageContent | one attempt usage record, not duplicate accumulation |
| unknown event/stop reason, parallel tools, argument fragments | preserve unknown identity; correlate by IDs/index; no premature tool completion |
| full completion versus early dispose/cancel/error | finalize once; partial counts retained; no forced drain |
| resumed response/poll re-observes final usage | one inference's accounting retained once under continuation lineage |
| retry, failover to another provider, concurrent calls | isolated state/versions per attempt; actual provider identity retained |
| tool loop and outer aggregate | child attempts counted once; loop aggregate retained as an alternate view |
| response cache hit with historical usage | no new provider attempt/spend inferred |
| serialization removes RawRepresentation | normalized fields still work; unsupported raw enrichment reported |
| negative/overflow/malformed/wrongly typed counts | InvalidShape/Partial with valid baseline; no model-result failure |
| normalized source numeric default versus explicit wire zero | no false claim of wire presence |
| SDK or protocol revision upgrade | golden mapping reviewed; fixture provenance updated; old rules remain reproducible |
| raw retention disabled/enabled with limits | no raw graph/content leakage by default; bounded/redacted capture only when enabled |
| Langfuse versus LangSmith export of same canonical usage | exclusive flat buckets for Langfuse; inclusive totals/detail subsets for LangSmith; neither double counts |

Acceptance requires fixture results, package-version compatibility, and streaming lifecycle checks before claiming an adapter complete. No pure fixture suite can prove hidden HTTP retries, provider invoices, or every gateway's undocumented extension behavior. Those remain explicit coverage limits.

## Reuse versus new work

**Reuse:** MEAI normalized fields/content and middleware; official provider SDK models and native event accumulators; OSS examples of method-specific usage extraction and idempotent streaming finalization.

**New LlmCmd work:** provider/protocol registry, optional extraction packages, immutable canonical measurements with semantic relations, merge/conflict policy, per-attempt provenance/coverage, privacy controls, fixture corpus and sink-specific usage conversions. Pricing remains in `ITokenCostEnricher`; telemetry export remains in collector/sink adapters.

No mapping is claimed here for an uninspected provider, an unknown gateway extension, or a new API merely because its response resembles OpenAI. Normalized-only support is a useful supported state.

[meai-openai]: https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIChatClient.cs
[meai-responses]: https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs
[meai-update]: https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatResponseUpdate.cs
[meai-usage]: https://github.com/dotnet/extensions/blob/25f07afa51034e37b334210642d7dc5f191c142d/src/Libraries/Microsoft.Extensions.AI.Abstractions/UsageDetails.cs
[openai-chat-api]: https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create
[anthropic-meai]: https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/src/Anthropic/AnthropicClientExtensions.cs
[anthropic-cache-model]: https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/src/Anthropic/Models/Messages/CacheCreation.cs
[anthropic-thinking-model]: https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/src/Anthropic/Models/Messages/OutputTokensDetails.cs
[anthropic-cache]: https://platform.claude.com/docs/en/build-with-claude/prompt-caching
[anthropic-stream]: https://platform.claude.com/docs/en/build-with-claude/streaming
[deepseek-chat-api]: https://api-docs.deepseek.com/api/create-chat-completion/
[langfuse-openai]: https://github.com/langfuse/langfuse-python/blob/1fc854dd2b241cf8e3dac38bfd5907ff2fe365e3/langfuse/openai.py
[langfuse-langchain]: https://github.com/langfuse/langfuse-python/blob/1fc854dd2b241cf8e3dac38bfd5907ff2fe365e3/langfuse/langchain/CallbackHandler.py
[langfuse-usage-doc]: https://langfuse.com/docs/observability/features/token-and-cost-tracking
[langfuse-ingestion]: https://github.com/langfuse/langfuse/blob/a4467fd418002ed0ee29771277aa9625484d8cb8/packages/shared/src/server/otel/OtelIngestionProcessor.ts
[langsmith-openai]: https://github.com/langchain-ai/langsmith-sdk/blob/290f06a628dffb2ca690e389dc48b629f5623312/python/langsmith/wrappers/_openai.py
[langsmith-usage]: https://github.com/langchain-ai/langsmith-sdk/blob/290f06a628dffb2ca690e389dc48b629f5623312/python/langsmith/_internal/_usage.py
[langsmith-anthropic]: https://github.com/langchain-ai/langsmith-sdk/blob/290f06a628dffb2ca690e389dc48b629f5623312/python/langsmith/wrappers/_anthropic.py
[langsmith-schema]: https://github.com/langchain-ai/langsmith-sdk/blob/290f06a628dffb2ca690e389dc48b629f5623312/python/langsmith/schemas.py
[otel-repo]: https://github.com/open-telemetry/opentelemetry-python-genai/tree/3747f16c86668e6dba990ceea30fced9298b8851
[otel-openai-chat]: https://github.com/open-telemetry/opentelemetry-python-genai/blob/3747f16c86668e6dba990ceea30fced9298b8851/instrumentation/opentelemetry-instrumentation-genai-openai/src/opentelemetry/instrumentation/genai/openai/utils.py
[otel-openai-response]: https://github.com/open-telemetry/opentelemetry-python-genai/blob/3747f16c86668e6dba990ceea30fced9298b8851/instrumentation/opentelemetry-instrumentation-genai-openai/src/opentelemetry/instrumentation/genai/openai/response_extractors.py
[otel-anthropic-extract]: https://github.com/open-telemetry/opentelemetry-python-genai/blob/3747f16c86668e6dba990ceea30fced9298b8851/instrumentation/opentelemetry-instrumentation-genai-anthropic/src/opentelemetry/instrumentation/genai/anthropic/messages_extractors.py
[otel-anthropic-stream]: https://github.com/open-telemetry/opentelemetry-python-genai/blob/3747f16c86668e6dba990ceea30fced9298b8851/instrumentation/opentelemetry-instrumentation-genai-anthropic/src/opentelemetry/instrumentation/genai/anthropic/wrappers.py
