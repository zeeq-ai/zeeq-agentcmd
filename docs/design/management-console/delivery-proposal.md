# Proposal to close the management-console gaps

Approved delivery plan, 2026-11-05. The [repository README](../../../README.md#management-console) records the delivered API/client, setup and explicit limits. This document preserves the six-increment acceptance intent; a listed gate is not a support claim by itself. It retains the [capability map](capability-map.md), [benchmark](benchmarks.md) and [semantic-judge](semantic-judging.md) decisions.

## Outcome and implementation shape

Deliver one complete loop: author an exact variant configuration, backtest baseline and candidate on frozen cases, assess with declared criteria, compare their evidence, publish a reviewed change, observe adoption, and turn a trace-linked incident into an owned regression/reference task.

Use one modular .NET control-plane application with a compact Vue/Nuxt UI frontend. Its feature modules own catalogs, authored configuration, datasets/assets, jobs, assessments, publications and review. Start with one API host and an in-process job/outbox service. Keep an independent worker entry point available for PostgreSQL deployments after multi-instance claims pass their contract tests. Do not require separate network services for each module.

Application-owned evaluation workers ship with the application's selected build and typed command registrations. They use a separate evaluation runtime and fixed evaluation source, with explicit job/candidate scope. They never replace the production source, mutate a production default, or load arbitrary application assemblies supplied through the console.

Keep four infrastructure boundaries distinct:

- Transactional management persistence: SQLite on persistent local disk for one console instance; PostgreSQL for qualified shared/multi-instance use. Both implement the same behavioral contracts, including revisions, claims, receipts and outbox transitions.
- Assets: a separate content-identified store for permitted input documents, attachments, source excerpts and evaluator evidence. Persistent local filesystem is the first adapter; object storage is an optional later adapter.
- Runtime publications: retain `IExperimentSnapshotSource`; add a separate narrow publisher capability. Qualify PostgreSQL read/publish/history first. The existing File reader remains development/current-only with no CAS/history promise. Console persistence does not determine the runtime publication target.
- Telemetry: an optional trace adapter/deep-link configuration. Exact output/attempt evidence remains authoritative; the console works when full spans are unavailable.

Choose storage and worker configuration at bootstrap. A database change requires migration/restart; a runtime-source binding initially requires host deployment/restart. Store secret references in management profiles; adapters resolve credentials on the server. No secrets belong in manifests, browser responses or job payloads.

## Decisions that remove the current blockers

### Durable jobs use their own claims

Use a control-plane work item keyed by run, candidate, case and generation repeat. Its claim has an owner/token and epoch. Bind a journal instance to that exact claim; do not put ownership authority in trace tags or create synthetic compositions just to run leaf commands. Real compositions retain their own membership, pins, claims and exact sealed aggregate.

Accept an attempt start durably only while the claim is current. This acknowledgement gates the provider send. Keep valid completion receipts for known attempts even when the lease has expired; authenticate their origin and deduplicate by claim/attempt identity. An expired epoch must not erase an actual completion. Reconciliation decides attachment/counting and retains conflicts explicitly.

No stored output does not prove no send. A start without reliable completion is Spend uncertain and held. Resume dispatches only work that complete authoritative state establishes is safe. Cancellation stops new admissions and requests cooperative cancellation; it does not promise to reverse already sent calls. There is no exactly-once billing or guaranteed exact monetary ceiling.

The public `IExecutionJournal` interface already exists, but the public standalone invocation API cannot currently bind one. `CommandRuntime.ExecuteAsync` takes a journal internally; composition supplies it through its bridge. Add a small optional invocation/worker binding that captures a job-bound journal without changing ordinary `WithParameters(...).ExecuteAsync()` calls. Carry it with the immutable invocation/prepared work and explicit execution context; concurrent jobs must not replace a global runtime journal. Preserve client ownership, cancellation and the existing MEAI pipeline. Cover both routed and direct send paths; the router journals only with captured execution context, so that context must be mandatory for worker work.

### Evaluation must execute the requested candidate

Add an opt-in strict evaluation preparation policy. The worker pins build/manifest, command contract, immutable snapshot/content hash and variant, or explicitly names a configured-default candidate and its expected fingerprint. If resolution would select another configuration, fail with a closed reason before any send. Production keeps its existing configured-default fallback behavior.

A forced selector alone is insufficient today: stale signatures, revisions or preparation failures can bypass/follow selection and retry preparation with the configured default. Known incompatible candidates must not enter the paid queue, and strict preparation must protect the actual call. Validate returned evidence against the requested pin too. In legacy/non-strict data, an unplanned default is Spent, not comparable; its cost remains visible and its output is not attributed to the requested candidate.

The source is fixed per runtime, so this requires a separate evaluation worker runtime/process configuration, not a per-call source swap inside production. Give the evaluation source/cache its correct namespace and immutable candidate lookup; preserve existing source-ID cache isolation. No raw user-supplied CLR type or assembly path becomes an execution capability.

### Workload origin is authoritative metadata

Persist purpose such as Production, Backtest or Evaluator, independently of Primary/Shadow role. Link evaluator calls and reassessment jobs to their parent run/outputs. Missing legacy origin is Unknown. Production queries require explicit production provenance; a Primary role or `ExperimentId` does not establish it.

Register/claim the workload before any candidate or judge call. Retain execution/failure records even when output storage fails. Reuse exact `OutputRef`, producer and `AttemptId` identities; query projections reconcile duplicates and late receipts without turning best-effort telemetry into a journal.

## Six delivery increments

Each increment includes its API/read model, compact UI and focused checks. The increments are dependencies, not dates or effort estimates. Auth, namespace/data access and paid-launch controls ship before the operations they protect.

### 1. Establish the control plane and persistence

Build the modular API, generated OpenAPI frontend client, application/environment/partition scope, actor/service authorization, revision checks and mutation audit. Persist draft/publication metadata, jobs/claims/receipts, assessment identities and outbox transitions. Implement SQLite and PostgreSQL adapters with the same semantics and explicit supported topology. Add the asset port and local persistent-disk implementation. Provide migration, backup/restore and retention configuration.

UI: Settings storage/capability overview and scoped workspace shell. Storage selection is bootstrap configuration, not a per-partition live database picker. Prepare APIs and stores for later features without creating a generic CRUD repository for all authority transitions.

**Gate:** restart preserves identities/content; two stale edits cannot both commit; scope violations are denied; a mutation and its outbox intent commit together; the same behavioral store suite passes for each claimed adapter. SQLite is single-instance/local disk; PostgreSQL multi-instance support stays unclaimed until tested.

### 2. Connect builds, manifests, drafts and typed workers

Have the generator emit deterministic manifest data/accessors; a build/MSBuild export step writes the portable artifact. Add authenticated immutable build registration, catalog diffs, separately tracked served catalogs/host reports and command capabilities. Mark opaque shapes rather than infer them. Export input/output schemas/signatures, placeholders and override metadata for supported shapes.

Add a generated typed dispatch/registration path in an optional hosting component. The application selects which commands and capabilities the worker exposes. Prefer authenticated outbound work claims/results from private worker deployments; an optional protected host manifest endpoint serves the same representation. Typed preflight is a no-provider-call worker job tied to the selected build and current host policy.

Add the optional standalone journal binding and strict candidate policy described above. Store immutable draft snapshots/content hashes. A backtest can use a draft without creating a live production rollout or publishing it.

UI: Commands registry/contract view, permitted variant editor, exact configuration history/diff and worker/preflight readiness. Approximate local preview stays distinct from authoritative typed preflight.

**Gate:** export starts no app/provider work; duplicate build/digest registration is idempotent and conflicting content is rejected. Wrong build, stale revision/signature, invalid override/input, blank required render or denied policy produces no provider call in strict mode. Preflight identifies its exact build. Production defaults, bindings and caches are unchanged.

### 3. Freeze replay-ready cases and benchmark protocols

Implement dataset drafts/frozen versions, case/source lineage, authorized typed input and immutable asset identities. Support authored/adjudicated references with provenance and per-criterion/case completeness. Add dataset purpose/split/exposure records for tuning/regression, judge calibration and acceptance; group related sources/corrections in one split. Historical queries are discovery sources that must materialize into frozen cases before a reproducible backtest.

Implement benchmark drafts/versions with explicit criterion definitions/bindings, evaluator configurations, one primary metric, secondaries, guardrail rules, repeats, eligibility and missing-data policies. Use separate output, reference and source bindings. Keep ordinary ad-hoc grades outside benchmark results unless explicitly reviewed under the protocol.

Readiness checks cover input/source retention, build availability, bindings and approved external dependency/tool policy. Support tool-free or host-approved side-effect-free execution first. Missing dependencies are explicit, never silently replaced with fixtures.

UI: Datasets/Case and Evaluation history tabs, benchmark Criteria editor/slideover, readiness states and explicit reference Inspect/Save. Cloning/freezing/validating makes no model calls.

**Gate:** frozen inputs/references/protocols cannot change in place; missing or unauthorized source/asset blocks the corresponding operation; related cases cannot silently cross independent splits. The benchmark selects exactly one primary and reports missing bindings/reference coverage without fabricating scores. No semantic judge dispatch occurs when saving configuration.

### 4. Execute durable backtests and reassessments

Implement Backtest and Reassess job kinds plus parent-run lineage. A backtest generates under pinned candidates and benchmark; reassessment consumes exact original retained outputs and does not regenerate candidates. Pin repeats and choose a contemporaneous baseline by default. A historical baseline is explicit and records model/time differences.

Use transactional claims, claim-bound attempt journals, immutable receipts/output evidence, reconciliation and separated execution/storage/assessment states. Add late/duplicate ingestion, authoritative origin, searchable execution history and pagination/read models. Include failed executions with no output. Add paid launch review, route/concurrency/attempt limits, cooperative cancel and safe resume. Price/usage gaps remain unknown; candidate and evaluator costs remain separately inspectable and combined only with attempt deduplication.

UI: Backtests under the Experiments work area, links from commands/benchmarks, per-case progress/failures, origin badges, a global job tray and explicit Cancel/Resume/Rerun/Reassess labels. No live experiment is required for these jobs.

**Gate:** racing workers cannot both receive a valid stale/current start permit; an expired owner cannot start another attempt. Valid late completions remain reconcilable. Started-without-completion work is held and never automatically resent. Routed and direct sends both journal correctly. Duplicate receipts do not duplicate outcomes/accounting. Backtest/evaluator executions never enter production exposure metrics.

### 5. Assess, calibrate, review and compare exact variants/versions

Implement assessment records with optional exact human-grade links. Add human rubrics, declared deterministic comparators and scoped LLM judges for field/subtree/collection/whole-output criteria. The evaluator uses an authorized provider-neutral pipeline and records its own attempts, pinned configuration, structured findings, source coverage/citations, errors and unresolved results.

Operationalize judge calibration on separate human-labelled examples before it drives primary/required acceptance decisions. Validate matching/unique credit and evidence locators; compute metrics under the frozen aggregation rule. Missing, unmapped, N/A, evaluator error and unreconciled states do not become zero or pass. Unknown guardrails remain unresolved.

Add owned grading, reference-gap and judge-audit tasks with claim/release and decision provenance. Accepting a discovery changes only a dataset draft. Freezing revised dataset/benchmark versions leads to reassessment of every compared candidate's eligible original outputs; it does not rewrite old scores or require new generation by itself.

UI: the proposed Compare case and Criteria & judge views, variant-by-run matrix, selected-version comparison, explicit baseline, saved Pass/Fail/Inconclusive decision reports, item-to-source evidence and compact review queues. Exact JSON, semantic findings and secondary metadata remain separate tasks/disclosures.

**Gate:** reordered/semantically equivalent arrays follow declared rules; supported extras in partial references are not absence-only errors; unsupported/unknown/contradicted findings stay distinct. An exhaustive reference challenge raises a dispute. Two criteria on the same pointer never share assessments accidentally. Human corrections cannot raise original model scores. Reference updates produce new fair assessments while frozen reports remain intact. Judge-led acceptance is blocked until its calibration/evidence policy is satisfied.

### 6. Publish, observe adoption and close the incident loop

Implement a narrow PostgreSQL publication writer/reader with immutable content, current-pointer concurrency control and history. Keep it independent of the selected management-store provider. Persist publication/outbox intent first; read the target's actual expected pointer, activate with that precondition and reconcile a lost acknowledgement. A divergent/unreadable target requires review/Unknown, not automatic overwrite. Keep File development capabilities explicit; do not label it CAS verified.

Add an immutable decision link and explicit actions for allocation change, new control, host-configured-default deployment and compiled-command/build change. Review affected commands, target build/policy, guards and expected fallback. Rollback/stop is a new reviewed action and does not cancel pinned/in-flight work.

Add host build/binding/publication reports, intended assignment/unit and fallback-stage evidence, retained execution/case elapsed timing and execution/attempt trace anchors. Separate publication acknowledgement from reported node adoption. Retain Unknown for missing reports/legacy fields; production remains descriptive until stronger analysis requirements are met.

Add trace deep links and an evidence-driven attempt timeline; full-span querying remains optional. From an exact output, capture an authorized replay-ready incident case into a dataset draft with duplicate checks, assignee and review state. A span is never a grade target and span absence is never proof of no send.

UI: reviewed publication impact, target delivery/adoption states, production monitor, optional telemetry backend, exact output links and owned incident-to-case review. Keep the existing five main navigation areas.

**Gate:** concurrent publishers conflict correctly; target activation followed by an acknowledgement crash reconciles without blindly overwriting a changed target. Existing pins remain unchanged. A real host report can show adoption, absence cannot. Rollout → exact output → attempt/trace → dataset draft retains identity, source access and review state. Available/unavailable spans do not change authoritative accounting.

## Verification and operational release gates

Use TUnit and deterministic fake clients for ordinary offline protocol/authorization/scoring checks. Add fault-injection cases at journal start, post-send completion, receipt/output attachment, assessment linking and target activation. Test claim races, stale epochs, late completions, duplicate/out-of-order events, cancellation and restart. Verify source/binding isolation and exact sealed aggregates for real compositions. Preserve package-consumer/AOT behavior and document every authored public production API.

SQLite tests use isolated persistent temporary files and restart scenarios. PostgreSQL integration and other container cases are `[Explicit]` and selected separately from offline verification. Aspire owns a full-stack dev/test environment when selected; a Testcontainers fixture owns its isolated adapter environment when selected. Never give two owners the same container lifecycle.

When an S3 adapter is introduced, use the requested on-demand Floci emulator through Aspire or generic .NET Testcontainers. Keep emulator/provider support claims separate. The same explicit contract suite must also pass against each real provider before support is claimed, including concurrent `If-Match`, create-once `If-None-Match` and stale-token failures. Redis/S3 targets are extension work, not prerequisites for the local backtest loop.

No model/secret-dependent/live-provider tests run without their existing explicit opt-in boundary. No providers, secrets, containers, migrations or remote writes are started by this proposal.

The first release is accepted by one fixture-backed complete workflow plus the relevant explicit adapter suites: register build; clone a variant; freeze cases/benchmark; launch baseline/candidate work; interrupt/recover it; inspect exact outputs; judge and review a supported discovery; compare versions; publish a reviewed change; report known/unknown adoption; capture a trace-linked regression task. Every failure/unknown state must remain visible. This verifies the loop without claiming causal online lift or exact provider billing.

## What follows the first complete loop

Add CI regression gates, schedules, alerts, failure clustering and automated promotion only over the same immutable job/report protocol. CI consumes exact candidate/benchmark/report pins and Pass/Fail/Inconclusive; pending, missing, cancelled or unreconciled evidence cannot pass. Extend asset/publication adapters only when the behavioral contracts qualify them. Statistical winner claims and independent holdout claims require their own declared evidence policies rather than a generic winner badge.

## Decisions closed versus deployment configuration

This proposal chooses control-plane job claims/journals rather than synthetic leaf compositions; an isolated app-owned build-pinned worker; optional journal and strict-candidate runtime additions; SQLite local/PostgreSQL shared store semantics; local asset storage first; PostgreSQL as the first qualified runtime publication target; explicit workload origin; separate backtest/reassessment; and LLM judging inside the initial working loop.

Deployment-specific inputs remain configuration: host identity/namespace, registered build and worker endpoint/pull identity, source/retention consent, approved provider routes, launch/concurrency limits, calibration/guardrail thresholds, reviewer/publisher permissions and initial trace deep links. Missing required settings block the corresponding operation with a precise readiness reason; they do not trigger an unsafe default or block unrelated offline work.

The DMTS reviewed the job-claim and worker choices against `CommandRuntime.cs`, `Invocation.cs`, `ProfileRouter.cs`, `OperationContext.cs`, the composition bridge/journal, experiment preparation and startup DI. The earlier designer and customer-advocate gap audit supplies the workflow and acceptance requirements. No implementation completion or further UI acceptance is claimed here.
