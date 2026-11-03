# Collector, telemetry export, and eval feedback adapters

Research date: 2026-11-01. Author role: System Architect (read-only research). No vendor endpoint was called and no data was sent. No credentials appear in this document.

Tags:

- **[V]**: verified in a current primary source (vendor or OpenTelemetry docs) on the research date.
- **[S]**: verified in vendor or library source code at the stated commit.
- **[K]**: from prior knowledge; not re-verified today.
- **[P]**: proposal.

Do not read compatibility into anything that is not tagged [V] or [S].

## 1. Summary

- Every target accepts **OTLP over HTTP**. Only Logfire documents gRPC. Default to `http/protobuf` [V].
- Send all telemetry from the app to one **OpenTelemetry Collector**, and fan out to vendors there. Vendor credentials and vendor-specific mappings then stay out of application code [P].
- GenAI semantic conventions are in **Development** status, and no version is released [V]. Each vendor maps a different subset of the conventions. **Universal compatibility does not exist.**
- **No portable path exists for eval scores.** Braintrust ingests the semconv `gen_ai.evaluation.result` event. Langfuse and LangSmith need their own REST APIs. Logfire documents no feedback ingestion API [V][S].
- "LangChain" in the requirement means **LangSmith**, the platform. LangChain and LangGraph are frameworks that can *emit* OTel to LangSmith [V].
- OTel export is lossy and sampled diagnostics. The **execution collector** stays the eval source of truth [P].

## 2. Endpoint and authentication matrix

Each "Traces URI" value is the full signal URI. When `OTEL_EXPORTER_OTLP_ENDPOINT` is a base URL, the OTLP exporter appends `/v1/traces`. Signal-specific endpoint variables are used exactly as given [V: LangSmith, Braintrust and Logfire docs; otlphttp exporter README].

| Target | Traces URI (cloud default) | Other regions / self-hosted | Signals documented | Transport | Auth / headers |
|---|---|---|---|---|---|
| Langfuse [V] | `https://cloud.langfuse.com/api/public/otel/v1/traces` (EU) | `us.cloud.langfuse.com`, `jp.cloud.langfuse.com`, `hipaa.cloud.langfuse.com`. Self-hosted: `<host>/api/public/otel` (v3.22.0 or later) | traces only | HTTP/protobuf or HTTP/JSON. "gRPC is not supported yet." | `Authorization: Basic base64(<public key>:<secret key>)`. **`x-langfuse-ingestion-version: 4`**: without it, directly ingested OTel data can be delayed by up to 10 minutes. |
| LangSmith [V] | `https://api.smith.langchain.com/otel/v1/traces` (US) | Hosts `eu.api.smith.langchain.com`, `apac.api.smith.langchain.com`, `aws.api.smith.langchain.com` (the docs imply the same `/otel` path). Self-hosted: `<host>/api/v1/otel` | traces only | HTTP is documented. No gRPC endpoint is documented. | `x-api-key`. Then **either** `Langsmith-Project` **or** both `Langsmith-Agent-Id` and `Langsmith-Agent-Environment` (`local\|development\|staging\|production`). Mixing the two forms → 400. An archived agent → 409. |
| Braintrust [V] | `https://api.braintrust.dev/otel/v1/traces` | Self-hosted: `<Universal API URL>/otel`. Logs need data plane v2.12.0 or later; GenAI score validation needs v2.16.0 or later | traces, logs (`/otel/v1/logs`, HTTP JSON or protobuf). No metrics endpoint is documented. | Logs: HTTP. Traces: protocol not stated, so treat as HTTP. | `Authorization: Bearer <API key>`. `x-bt-parent: project_id:<id>` \| `project_name:<name>` \| `experiment_id:<id>`. The docs do not say whether the header is required, or what happens without it. Treat it as required [P]. |
| Logfire [V] | `https://logfire-us.pydantic.dev/v1/traces` | EU: `https://logfire-eu.pydantic.dev` | traces, metrics, logs | HTTP **and** gRPC | `Authorization: <write token>`. The alternative-clients page shows the raw token with no "Bearer". The Logfire Collector guide shows `Bearer ${env:LOGFIRE_TOKEN}`. Both appear in vendor docs, and which is canonical is unverified. Make the scheme a profile option. Default to raw, as the alternative-clients page shows [P]. 413 when a batch is over 100 MB or timestamps are out of range. |
| Aspire dashboard [V] | gRPC `http://localhost:18889`. HTTP `http://localhost:18890` (+`/v1/traces`) | Standalone container: 4317/4318 [K] | traces, metrics, logs | gRPC. OTLP/HTTP is protobuf only, and it is not enabled by default when the AppHost launches the dashboard. | `Dashboard:Otlp:AuthMode` is `Unsecured` (default, local development only), `ApiKey` (header `x-otlp-api-key`) or `ClientCertificate`. |
| Generic OTLP backend | as configured | n/a | per backend | per backend | per backend |

Collector exporter facts [V, otlphttp README]:

- `endpoint` is a base URL; the exporter appends `/v1/traces`, `/v1/metrics` or `/v1/logs`. `traces_endpoint`, `metrics_endpoint` and `logs_endpoint` override it per signal.
- `encoding` is `proto` (default) or `json`. gzip compression is on by default. Timeout defaults to 30 s.
- `retry_on_failure` and `sending_queue` are available. Traces, metrics and logs are stable.
- The `headers` key comes from the shared HTTP client config [K]. Load secrets from `${env:...}` [V: vendor collector examples].

Sources:
[Langfuse OTel](https://langfuse.com/integrations/native/opentelemetry) ·
[LangSmith OTel](https://docs.langchain.com/langsmith/trace-with-opentelemetry) ·
[Braintrust OTel](https://docs.braintrust.dev/integrations/sdk-integrations/opentelemetry) ·
[Logfire alternative clients](https://pydantic.dev/docs/logfire/guides/alternative-clients/) ·
[Logfire collector article](https://pydantic.dev/articles/logfire-opentelemetry-collector) ·
[Aspire dashboard config](https://aspire.dev/dashboard/configuration/) (source used: [aspire.dev repo](https://github.com/microsoft/aspire.dev/blob/main/src/frontend/src/content/docs/dashboard/configuration.mdx)) ·
[otlphttp exporter](https://github.com/open-telemetry/opentelemetry-collector/blob/main/exporter/otlphttpexporter/README.md)

## 3. GenAI semantic conventions: what is actually emitted and read

### 3.1 Specification state [V]

- The conventions moved to [open-telemetry/semantic-conventions-genai](https://github.com/open-telemetry/semantic-conventions-genai). It has no releases yet, and its Schema URL is "TODO".
- The docs link core semconv **v1.44.0**. Every GenAI attribute is **Development** status.
- Inference span ([client-inference.md](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/client-inference.md)): name `{gen_ai.operation.name} {gen_ai.request.model}`, kind `CLIENT`.
  - Required: `gen_ai.operation.name`, `gen_ai.provider.name`.
  - Usage: `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.usage.cache_read.input_tokens`, `gen_ai.usage.cache_write.input_tokens`, `gen_ai.usage.reasoning.output_tokens`.
  - Content (Opt-In): `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`. Content MAY be recorded as a JSON string when structured attributes are not supported.
  - **Prompt template identity**: `gen_ai.prompt.name` (Conditionally Required when a named prompt template is used), `gen_ai.prompt.version` (Conditionally Required when a name is set and a version is available), and `gen_ai.prompt.variable.<name>` (Opt-In; may be sensitive).
- Content capture: `OTEL_INSTRUMENTATION_GENAI_CAPTURE_CONTENT` = `NO_CONTENT` (default) \| `SPAN_ONLY` \| `EVENT_ONLY` \| `SPAN_AND_EVENT` ([gen-ai-spans.md](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-spans.md)).
- Evaluation event ([gen-ai-events.md](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/gen-ai-events.md)): `gen_ai.evaluation.result`, Development status.
  - Attributes: `gen_ai.evaluation.name` (Required), `gen_ai.evaluation.score.value` and `gen_ai.evaluation.score.label` (Conditionally Required), `gen_ai.evaluation.explanation` (Recommended), `gen_ai.response.id` (Recommended), `error.type`.
  - It "SHOULD be parented to GenAI operation span being evaluated when possible".

### 3.2 MEAI `OpenTelemetryChatClient` [S, dotnet/extensions main]

- It implements "Semantic Conventions for Generative AI systems **v1.41**", which is older than the v1.44 the GenAI docs link.
- Span `chat {modelId}`, kind Client.
- Attributes: `gen_ai.provider.name`, `gen_ai.conversation.id`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.usage.cache_read.input_tokens`, `gen_ai.usage.reasoning.output_tokens`.
- Metrics: `gen_ai.client.token.usage`, `gen_ai.client.operation.duration`, plus streaming time-to-first-chunk and time-per-chunk metrics.
- `gen_ai.input.messages`, `gen_ai.output.messages` and `gen_ai.system_instructions` are **span attributes** (not events). They are written only when `EnableSensitiveData` is true.
- **Mismatch**: `EnableSensitiveData` defaults from `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true`, not from the current `OTEL_INSTRUMENTATION_GENAI_CAPTURE_CONTENT` modes. Set the property explicitly from our profile [P].
- **Risk**: with sensitive data on, MEAI copies `ChatOptions.AdditionalProperties` and response `AdditionalProperties` onto the span.
- MEAI emits **no** `gen_ai.prompt.name` or `gen_ai.prompt.version`, and no evaluation event.

Sources: [OpenTelemetryChatClient.cs](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/OpenTelemetryChatClient.cs), [OpenTelemetryConsts.cs](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI/OpenTelemetryConsts.cs).

### 3.3 Vendor read-side mapping

Input and output content:

- **Langfuse**: the docs list only `gen_ai.prompt` / `gen_ai.completion`, `input.value` / `output.value` and `langfuse.observation.input` / `output`. The ingestion source reads `gen_ai.input.messages`, `gen_ai.output.messages` and `gen_ai.system_instructions` (prepended as a system message) [S].
- **LangSmith**: `gen_ai.input.messages` / `output.messages`, `gen_ai.prompt` / `completion`, `gen_ai.prompt.{n}.*`, OpenInference and TraceLoop forms [V].
- **Braintrust**: `gen_ai.input.messages` / `output.messages`, `gen_ai.prompt(_json)` / `completion(_json)` and message events. Non-empty message events take precedence over attributes [V].
- **Logfire**: not documented; the LLM panels page names only token and model attributes [V].

Model, provider and usage:

- **Langfuse**: model from `gen_ai.request.model` or `gen_ai.response.model`. Usage from `gen_ai.usage.*`, including cache_read and reasoning variants [S]. The source reads `gen_ai.system`; no `gen_ai.provider.name` was found [S].
- **LangSmith**: `gen_ai.*`. Current names take precedence over deprecated ones; `gen_ai.usage.total_tokens` is not semconv [V].
- **Braintrust**: `gen_ai.request.*` → metadata (provider prefix stripped). `gen_ai.usage.*` → metrics (`prompt_tokens`, `completion_tokens`, `tokens`, cache) [V].
- **Logfire**: token and cost "work with any instrumentation that follows the OTel GenAI span conventions". The example uses the older `gen_ai.system` [V]. Cost comes from `operation.cost` if present, otherwise from the `genai-prices` dataset.

Prompt identity:

- **Langfuse**: `langfuse.observation.prompt.name` / `version` (or `langfuse.prompt.*`). `gen_ai.prompt.name` is not mapped [V][S].
- **LangSmith**: `langsmith.metadata.<key>` (no prompt field documented) [V].
- **Braintrust**: `braintrust.metadata` [V].
- **Logfire**: not documented.

Span type and tree:

- **Langfuse**: `langfuse.observation.type`. **A root span is required** to create the trace [V].
- **LangSmith**: `langsmith.span.kind`, `langsmith.trace.name`, `langsmith.trace.session_id`. **A child whose parent never arrives is dropped** silently. The endpoint returns 200 before processing [V].
- **Braintrust**: `gen_ai.operation.name` `chat`→`llm`, `execute_tool`→`tool`, and `braintrust.span_attributes`. **A root span is required** for the trace to appear [V].
- **Logfire**: not documented.

Scores from OTel:

- **Langfuse**: none documented; none found in the ingestion source [V][S].
- **LangSmith**: none documented [V].
- **Braintrust**: `gen_ai.evaluation.result` event → `scores.<name>`, **finite 0–1 only**. `braintrust.scores` is also accepted [V].
- **Logfire**: pydantic-evals (Python) spans feed the Evals tab. No generic score ingestion is documented [V].

Sources:
[Langfuse OtelIngestionProcessor.ts @ a4467fd](https://github.com/langfuse/langfuse/blob/a4467fd418002ed0ee29771277aa9625484d8cb8/packages/shared/src/server/otel/OtelIngestionProcessor.ts) (2026-10-28) ·
[Braintrust attributes](https://docs.braintrust.dev/integrations/sdk-integrations/opentelemetry/attributes) ·
[Logfire LLM panels](https://pydantic.dev/docs/logfire/observe/llm-panels/) ·
[Logfire evals](https://pydantic.dev/docs/logfire/evaluate/evals) ·
[Aspire 13.2 GenAI visualizer](https://devblogs.microsoft.com/aspire/aspire-dashboard-improvements-export-and-telemetry/)

The Aspire dashboard's GenAI visualizer parses `gen_ai.system_instructions`, `gen_ai.input.messages` and `gen_ai.output.messages` span attributes [V, OTel blog and Aspire blog]. It is therefore the right local check that content capture works.

## 4. Eval score and feedback APIs (outside OTel)

| Target | Endpoint | Auth | Key fields | Idempotency / ordering | Linking to a traced execution |
|---|---|---|---|---|---|
| Langfuse [V] | `POST /api/public/scores` | Basic (public key : secret key) | `traceId`, optional `observationId`, or `sessionId`. `name`, `value`, `dataType` (`NUMERIC\|CATEGORICAL\|BOOLEAN\|TEXT`), `comment`, `configId` | `id` is an idempotency/update key. A score **may arrive before the trace**. | OTel trace ID → `traceId` [K: Langfuse uses the OTel trace ID]. **A spike must confirm the hex format and the span-ID → observationId mapping.** |
| LangSmith [V] | `POST /api/v1/feedback` | `X-API-Key` (or Bearer + `x-tenant-id`) | `key` (≤180 characters, required), `score`, `value`, `comment`, `run_id` / `trace_id` (UUID), `session_id` (required unless `address`), `feedback_source`, `id` (UUID) | `id` is a client UUID. Key-reserved → 403. | **Unverified.** Run IDs are UUIDs; the docs say native OTLP `parentSpanId` cannot reference a LangSmith run. A documented OTel-span → run_id mapping was not found. **Do not ship a LangSmith feedback adapter until a spike proves the addressing.** |
| Braintrust | OTel `gen_ai.evaluation.result` on the evaluated span, or `braintrust.scores.<name>` [V] | as OTLP | value in 0–1 | n/a | Same trace and span. A REST feedback API exists [K], not researched here. |
| Logfire | none documented | n/a | n/a | n/a | Evals are spans from pydantic-evals (Python only) [V] |

Sources: [Langfuse scores](https://langfuse.com/docs/evaluation/evaluation-methods/scores-via-sdk) · [LangSmith create feedback](https://docs.langchain.com/langsmith/smith-api/feedback/create-feedback.md)

## 5. Sampling, payload, and delivery constraints

- **Keep whole traces.** Langfuse and Braintrust need the root span. LangSmith drops orphans [V]. Use parent-based head sampling or Collector tail sampling. Never use per-vendor span filtering that can drop a root or an intermediate parent [P].
- **Delivery is not confirmation.** LangSmith returns 200 before processing [V]. OTLP retry and queueing are best effort [K]. The eval dataset must come from `IExecutionCollector`, not from vendor ingestion [P].
- **Size limits.** Logfire: 413 when a batch is over 100 MB [V]. Aspire: `MaxAttributeLength` and `MaxSpanEventCount` are unset by default; trace and log counts default to 100,000 [V]. Langfuse silently drops keys with the path segments `__proto__`, `constructor` or `prototype` [V]. The other vendors document no attribute or payload limits [V]. Semconv allows truncation and filtering of content, and `*_ref` external-content attributes. Do not embed signed-URL credentials in those refs [V].
- **Content is opt-in.** Default the profile to no content. Enable content per target, and per environment, explicitly [P].
- **Propagate filter keys.** Langfuse needs the user, session, tags and similar keys on **every** span to filter on them [V].

## 6. Recommended adapter architecture [P]

```text
LlmCmd executor ──► IExecutionCollector ──► durable store (source of truth for evals/replay)
      │
      ├─ command Activity (gen_ai.prompt.name/version, llm_cmd.* ids) ┐
      └─ MEAI OpenTelemetryChatClient spans + metrics                ├─► OTLP ─► OTel Collector ─┬─ pipeline/aspire     ─► Aspire dashboard
                                                                     ┘                           ├─ pipeline/langfuse   ─► transform ─► otlphttp
                                                                                                 ├─ pipeline/langsmith  ─► transform ─► otlphttp
                                                                                                 ├─ pipeline/braintrust ─► transform ─► otlphttp
                                                                                                 └─ pipeline/logfire    ─► otlphttp
Eval run ──► EvaluationResult ──► IEvalFeedbackSink (optional, per vendor REST) and/or gen_ai.evaluation.result event
```

### 6.1 Three separate contracts

1. **`IExecutionCollector`** (exists today): receives our immutable envelope. It has its own delivery contract and no dependency on OTel or any vendor.
2. **Telemetry export profile**: *configuration*, not code. It is a typed record from which the app or the Collector configuration is generated:
   - `TargetKind`: Aspire, Langfuse, LangSmith, Braintrust, Logfire or GenericOtlp.
   - `Region`/`BaseUri`.
   - `Signals` ⊆ the signals the target supports.
   - `Protocol`: HttpProtobuf (default) or Grpc, only where supported.
   - `Headers` as name → **secret reference**, never a value.
   - `ContentCapture`: None, SpanOnly or SpanAndEvent.
   - `Sampling` policy.
   - Vendor-specific required fields: Langfuse ingestion version; LangSmith project **or** agent id + environment; Braintrust parent; Logfire auth scheme.

   `Resolve(profile, secrets)` returns a closed result (`ConfiguredTarget | ConfigurationError`) with the exact full URI per signal and the header set. It rejects unsupported signal/protocol pairs. This matches the root agent's `CollectorAdapters` plan.
3. **`IEvalFeedbackSink`** (optional): `SubmitAsync(EvaluationResult, ExecutionLink) → FeedbackReceipt` (closed: `Accepted(id) | Rejected(reason) | Unsupported`).
   - `ExecutionLink` carries the trace ID, span ID, `gen_ai.response.id` and execution ID.
   - Each vendor adapter declares the link it requires: Langfuse needs the trace ID; LangSmith needs a run UUID (unverified).
   - Use our `EvaluationResult.Id` as the vendor idempotency key.

### 6.2 What is shared, and what is mapped per vendor

| Shared (emit once, in-process) | Per-vendor mapping (Collector `transform` per pipeline, or an optional in-process span enricher) |
|---|---|
| MEAI semconv spans and metrics | Langfuse: `langfuse.observation.prompt.name` / `version` ← `gen_ai.prompt.name` / `version`. `langfuse.trace.name`, `langfuse.session.id`, `langfuse.user.id`, propagated to all spans. Optionally `langfuse.observation.type`. |
| Command span: `gen_ai.prompt.name` = PromptId, `gen_ai.prompt.version` = revision, `llm_cmd.execution_id`, `llm_cmd.definition_hash`, `llm_cmd.effective_hash`, outcome | LangSmith: `langsmith.trace.name`, `langsmith.span.kind` (`chain` for the command span, `llm` for the MEAI span), `langsmith.metadata.<key>` for prompt and execution IDs. Use routing headers, not attributes, to pick the project or agent. |
| `gen_ai.evaluation.result` event (optional, when the evaluated span is still open or can be re-parented) | Braintrust: `braintrust.metadata`, `braintrust.span_attributes.type`. Scores normalized to 0–1, or kept as an unsupported scale. |
| OTLP transport, retries, queue, sampling at the Collector | Logfire: none required. Verify the LLM panel with MEAI attributes in a spike. |

Rules for this mapping:

- Never put secrets or raw parameter values in vendor metadata by default.
- `gen_ai.prompt.variable.*` is Opt-In only, because it carries parameter values.
- Keep IDs out of metric dimensions.

### 6.3 Unverified items to prove before claiming support

Wire tests (TUnit, owned by the root agent) check only the URI and headers, against a local fake OTLP server. Do not call vendors without credentials and an explicit budget. Items to prove:

1. Langfuse renders MEAI `gen_ai.input.messages` (the source says yes; the docs do not), and links prompts through `langfuse.observation.prompt.*`.
2. The Langfuse score `traceId` format equals the OTel hex trace ID.
3. The LangSmith run UUID addressing for OTel-ingested spans, which feedback requires.
4. Braintrust behaviour when `x-bt-parent` is missing.
5. The canonical Logfire header (raw or `Bearer`).
6. The Logfire LLM panel with the MEAI v1.41 attribute set.
7. The Aspire visualizer with MEAI content enabled through an explicit `EnableSensitiveData`.
8. Collector `transform` mapping from semconv to vendor attributes, with semconv spans unchanged in the other pipelines.
