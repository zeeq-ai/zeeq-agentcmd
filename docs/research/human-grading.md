# Human grading and correction across follow-up requests

Owner: System Architect. Status: design research, no implementation. Date: 2026-11-01.
Scope: the identity model, idempotency, context propagation, the system of record, field addressing and vendor feedback mapping for the root agent's `spikes/HumanGrading`.

Legend:
- **[V]** = verified in a primary source at the stated ref.
- **[S]** = official docs.
- **[K]** = my knowledge, not re-verified.
- **[P]** = proposal.

## 1. Identity model [P]

| Id | Issued by | Lifetime / cardinality | Purpose |
|---|---|---|---|
| `TenantId` | auth | — | authorization scope for every lookup below |
| `CaseId` (optional) | caller / dataset | many runs | business or eval case (ticket, dataset row). Never used for authorization |
| `RunId` | server, at first execution (UUIDv7/ULID) | **stable** across retries, regenerations and follow-ups | the logical "one user-visible answer" handle; the key for grades |
| `ExecutionId` | executor, per `Run()` | 1 per attempt | the existing execution record; a retry or regenerate gets a new one under the same `RunId` |
| `TraceId`/`SpanId` | OTel | 1 per execution | telemetry correlation only; *stored* in the ledger, never derived from or used as identity |
| `OutputVersionId` | server | immutable | one output artifact. `v1` = model output (from an ExecutionId); `vN` = human-corrected derivative with `ParentVersionId` |
| `EventId` | server | 1 per ledger event | grade, correction or retraction event |
| `IdempotencyKey` | client | 1 per intended action | dedupes HTTP retries (section 2) |
| `GraderId` + `Rubric(name, version)` | auth / config | — | who graded, against what |

Rules:
- [P] Never derive `RunId` from `TraceId` or `ExecutionId`. Traces are sampled, re-rooted and retention-limited. Executions multiply on retry.
- [P] A grade always targets `(RunId, OutputVersionId)`. A grade without the version can silently attach to a regenerated output.
- [P] `OutputVersionId` = SHA-256 over the canonical output bytes (e.g. RFC 8785 JCS for JSON **[K]**) plus the output schema id and version. Server-issued immutable ids are equivalent if the bytes are stored write-once. With content addressing, a duplicate submit of the same corrected output yields the same version.
- [P] Outputs are immutable artifacts. A correction never edits `vN`. Applying a correction produces `vN+1` (parent `vN`) and a `CorrectionApplied` event.

## 2. Ledger events, idempotency and revisions [P]

Append-only events per `RunId`:
- `OutputRecorded(version, executionId, traceId, spanId)`
- `GradeRecorded(version, rubric, value | label, comment)`
- `CorrectionApplied(baseVersion, ops[], newVersion)`
- `GradeRetracted(eventId)`
- `GradeSuperseded(eventId, byEventId)`

Idempotency:
- A unique index on `(TenantId, RunId, IdempotencyKey)`.
  - Same key and same payload hash → return the original `EventId` (no new event).
  - Same key and a different payload → an `IdempotencyConflict` result (closed result type, no exception).
- Do not rely on HTTP method semantics alone. A browser double-submit or proxy retry of a POST must be safe.

Revisions:
- A per-run monotonic `Revision`. Writers send `expectedRevision` (or `If-Match` with an ETag) → a `RevisionConflict` result on mismatch.
- "Current grade" is a projection: the latest non-retracted, non-superseded event per `(RunId, OutputVersionId, Rubric, GraderId)`. Never update rows in place.
- Keep the projection rebuildable from events. Vendor exports and OTel emission read from events (section 4).

## 3. Field identity: JSON Pointer correctness

- [K] RFC 6901 (<https://www.rfc-editor.org/rfc/rfc6901>): `~` and `/` in member names must be escaped as `~0`/`~1` (decode `~1` before `~0`). `""` is the whole document. `-` names a non-existent element past the end of an array. Pointers address JSON values only, never substrings.
- [P] A correction op is `{ path, op: replace|add|remove, expected: <old value or hash>, value }`, evaluated **against `baseVersion` only**. This mirrors RFC 6902 `test`-then-`replace` **[K]**.
  - Reject the whole correction (`CorrectionConflict`) when any `expected` mismatches, the path does not resolve, or the result fails the output schema.
  - Apply all ops or none.
- [P] Array indices are unstable across versions (reordering, regeneration). For list outputs, the generated structured-output schema should carry a stable element key. Corrections then reference `/items/<index>` *plus* `expected.key`. Alternatively, define a key-addressed pointer extension. Do not accept `-` except for `add`.
- [P] Store pointers in their escaped RFC 6901 form exactly as received, plus a normalized decoded segment array for querying. Validate syntax at the API edge.
- [P] Free-text outputs have no JSON structure. Either correct the whole value (`path: ""` or the string member), or use a separate text-span model with an explicit unit (Unicode scalar offsets) and a quoted `expected` substring. Do not overload JSON Pointer for this.
- [P] Grades can target a field (`path`) as well as the whole output. Field grades use the same pointer and `expected` discipline.

## 4. The system of record vs OTLP

- [P] **The durable ledger is the system of record.** OTLP is a lossy, sampled, retention-limited projection, so it can lose grades:
  - tail sampling can drop them;
  - vendor retention expires them;
  - Collector transforms can rewrite them.
  - Use an **outbox**: the ledger transaction writes the event and an outbox row. A worker emits telemetry and vendor feedback with retries and deterministic ids.
- [S] `gen_ai.evaluation.result` (semantic-conventions-genai, `docs/gen-ai/gen-ai-events.md`, main; status Development):
  - required `gen_ai.evaluation.name`;
  - conditionally required `gen_ai.evaluation.score.value` (double) and `…score.label`;
  - recommended `gen_ai.evaluation.explanation` and `gen_ai.response.id`;
  - conditionally required `error.type`.
  - The event "SHOULD be parented to the GenAI operation span being evaluated when possible". If the span id is not available, set `gen_ai.response.id`.
- [P] For a human grade that arrives days or months later, emit the event as a *log record* that carries the **stored** source TraceId/SpanId. That is correlation on the event, not a new span parented into the old trace. Also add `llmcmd.run.id`, `llmcmd.output.version_id` and `gen_ai.response.id` (when known), so the event is still joinable after the source span expires.
  - Vendor behaviour for late events whose span is out of retention is **unverified**.
- [P] The follow-up HTTP request gets its own trace: the normal W3C `traceparent` handling for that request. Its handler span (or a child `llmcmd.grade` span) carries an `ActivityLink` to the stored source `SpanContext`, plus `llmcmd.run.id` attributes.
  - [S] OTel trace API (<https://opentelemetry.io/docs/specs/otel/trace/api/#link>): links can be added at creation or later, but "`Link`s added after `Span` creation may not be considered by Samplers". Adding them at creation is preferred.
  - [P] Therefore start a dedicated child activity with `links:` at creation. Do not call `AddLink` on the ASP.NET Core server activity. Never use the stored context as a *parent*.

## 5. Authorized context propagation

- [P] `RunId`/`OutputVersionId` in a URL or body are **identifiers, not authority**. Every follow-up must authorize against the ledger:
  - the run's `TenantId` matches the caller's tenant;
  - the caller has the grader/corrector role for that run, case or project.
  - Return `NotFound` for unauthorized runs, so ids cannot be probed.
- [P] For grading links given to external humans: use a signed, short-TTL capability token scoped to `(TenantId, RunId, allowed actions)`. Store the token's `jti` with the event for audit.
- [S] W3C Baggage (CR Snapshot 2024-05-30, <https://www.w3.org/TR/baggage/>, §4–5):
  - baggage "may carry sensitive information";
  - "keys and values can be propagated to other systems";
  - owners should ensure "`baggage` isn't present in requests that cross trust-boundaries".
  - [P] Consequence: never read `TenantId`, `GraderId`, `RunId` or roles from incoming baggage. Never put grader identity or PII into baggage. Strip baggage at the public edge.
- [P] Treat an incoming `traceparent` from untrusted clients as telemetry-only. A client can choose any trace id and inject its spans into someone else's trace. [K] W3C Trace Context allows restarting the trace at a trust boundary. In ASP.NET Core this is a propagator or edge-proxy policy (verify against the .NET 11 API before implementation).
- [P] Ledger events record `GraderId` from the authenticated principal only, and the request's own `TraceId` for audit.

## 6. Vendor feedback mapping

| Vendor | Feedback API (verified ref) | Target id needed | Idempotency | Correction field | Status |
|---|---|---|---|---|---|
| Langfuse | `POST /api/public/scores` [S] <https://langfuse.com/docs/evaluation/evaluation-methods/scores-via-sdk> | `traceId` (required for trace/observation scores); `observationId` + `traceId` for a span | [S] `id` alone is **not** an upsert. A score is replaced only when `id`, `name` **and** timestamp all match. Send the complete score | none (comment only) | Mapping OTel trace/span id → Langfuse trace/observation id: **unverified** (see collector-adapters.md); verify before enabling |
| LangSmith | `Client.create_feedback(run_id, key, score, value, trace_id, correction, comment, …, feedback_id, project_id)` [V] langchain-ai/langsmith-sdk @ `290f06a628dffb2ca690e389dc48b629f5623312`, `python/langsmith/client.py` L8321 | `run_id` (and `trace_id` = root run) | [V] `feedback_id`: "If not provided, a random UUID will be generated". [P] Use UUIDv5(EventId). Behaviour on a duplicate id is **unverified** | [V] `correction (dict)`: "The proper ground truth for this run." | **BLOCKED**: the OTel span → LangSmith run UUID mapping is unverified |
| Braintrust | `POST /v1/project_logs/{project_id}/feedback` [V] braintrustdata/braintrust-openapi @ `3384e4be354ec957292f986fcf3e3a4009907651` (`FeedbackProjectLogsItem`) | `id` (required) = "the row `id` returned by `POST /v1/project_logs/{project_id}/insert`" | not documented; `scores` (0–1) "will be merged into the existing scores" | [V] `expected` (ground truth); plus `comment`, `metadata`, `source` (`external`/`app`/`api`), `tags` | **BLOCKED** for OTel-ingested spans: span → row-id mapping unverified. The OTel `gen_ai.evaluation.result` path (collector-adapters.md) is the alternative; late-event attachment is unverified |
| Logfire | none documented | — | — | — | OTel-only (`gen_ai.evaluation.result` log record) |

[P] Adapter contract: the outbox worker calls an optional `IEvalFeedbackSink` per vendor, with:
- a deterministic vendor id derived from `EventId`;
- a `0..1` normalization only where the vendor requires it (Braintrust). Keep the raw value in the ledger.

A vendor whose target-id mapping is unverified resolves to `ConfigurationError("feedback mapping unverified")`, not to a guess.

## 7. Spike checks (root-owned) [P]
1. A retry and a regenerate under one `RunId` create two `ExecutionId`s. A grade on `v1` stays on `v1` after `v2` exists.
2. The same `IdempotencyKey` and payload returns the same `EventId`. The same key with a different payload returns `IdempotencyConflict`.
3. A stale `expectedRevision` returns `RevisionConflict`, and no event is written.
4. Pointer cases:
   - `~0`/`~1` escapes round-trip;
   - an `expected` mismatch rejects the whole op set;
   - a schema-invalid result is rejected;
   - `-` is rejected for `replace`.
5. Follow-up span: a new trace id, an `ActivityLink` to the stored context present at start, and `Parent` ≠ the stored span.
6. Incoming baggage with `tenant`/`grader` keys is ignored. Authorization uses the principal only.
7. Outbox: kill the worker mid-send. On restart, no event is duplicated at the fake vendor sink (deterministic ids).
