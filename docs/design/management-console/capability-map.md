# Experiment lifecycle: capabilities and remaining gaps

Design gap review, 2026-11-05. Consolidated from the DMTS, product designer, customer advocate, and lead's source checks. This extends the [console proposal](README.md), [benchmark design](benchmarks.md), and [semantic-judge design](semantic-judging.md). It does not implement a console service, runner, storage adapter, or public runtime API.

Manifest, experiments, variants, immutable publications, datasets, benchmarks, grades, exact output versions, and traces are the core objects. The remaining work connects them into a usable loop: **author → backtest → compare → decide → publish → observe → diagnose → curate a regression case**.

Existing runtime mechanisms are not complete console features. Generated registrations, experiment resolution, attempt evidence, output/feedback contracts, composition claims, and telemetry are reusable foundations. The build registry, management API, durable control-plane stores, datasets, assessment store, evaluation workers, query model, and publication writer remain designed rather than implemented.

## Product capability map

| Capability | User-visible gap | Delivery boundary |
| --- | --- | --- |
| Application and build connectivity | Know the build, served commands, host capabilities, route policy, environment and partition; find an authorized worker for typed preflight/execution | Required for the first complete loop; adoption stays Unknown without host reports |
| Variant and version tracking | Diff exact configurations; choose a baseline; compare selected variants/publications/draft hashes; link results and decisions to their actual configuration | Required; the comparison model is designed, but its read model and surfaces are not built |
| Backtest and reassessment jobs | Launch, follow per-case generation/assessment progress, cancel, recover, and retain results across restart | Required; execution and reassessment are distinct job kinds |
| Replay-ready cases and assets | Retain typed inputs, documents and context with content identities; reveal missing/redacted inputs and unavailable dependencies before launch | Required for backtests and trace-to-case curation |
| Execution history and trace correlation | Search executions, including failures without outputs; follow candidate → execution → attempt → exact output → assessment and optional trace | Required read model; embedded full-span exploration remains an optional adapter |
| Workload origin and metrics | Separate production, backtest and evaluator work from primary/shadow role; never count synthetic work as production exposure | Required before combining historical or live data |
| Comparison protocol and dataset purpose | Run a contemporaneous baseline, pin repeats, aggregate within a case, freeze case selection, and separate tuning/calibration/acceptance populations | Basic protocol required; independent acceptance claims additionally require holdout/exposure policy |
| Review and adjudication operations | Assign and claim grading, judge audit, incident and reference-gap work; track blocked evidence, disputes, draft acceptance and rejection | Required; a draft case or judge finding is not an accepted reference |
| Promotion, rollout and adoption | Name exactly what promotion changes, retain the frozen decision evidence, publish with conflict handling, and report actual host adoption | Explicit publication/decision required; cross-environment automation can follow |
| Shared-use and paid-work controls | Authenticate actors/services; scope data and actions; record audit; limit launches, concurrency, retries and admissions; retain unknown accounting | Required before the corresponding shared or paid use |
| Storage and operations | Implement durable stores and query projections; define asset/revision retention, deletion impact, migration, backup and restore | Durability and explicit retention required; automated garbage collection can follow |
| CI, schedules and alerts | Consume a frozen decision report for regression gates, scheduled backtests, production watches and failure clustering | Later automation over the same manual protocol |

## Actions have different costs and identities

| Action | Meaning |
| --- | --- |
| Run backtest | Execute pinned candidates on frozen historical cases, then assess under a pinned benchmark. New candidate and possibly judge calls are explicit. |
| Reassess outputs | Assess exact retained original outputs under the selected protocol. No candidate generation; judge calls can still cost money. |
| Rerun | Create another generation run, linked to its parent and the pins it reuses or changes. Do not overwrite the old run. |
| Resume | Continue only work that authoritative durable state establishes is safe to dispatch. No stored output alone is insufficient. |
| Run shadow | Execute against live inputs independently of the caller result. It is not a frozen backtest population. |
| Promote | Make a specifically named configuration/deployment change, justified by a frozen report and target policy review. |

Backtests do not require a live production experiment, publication, or traffic allocation. The current runtime's `ExperimentCatalogView` still carries an `ExperimentId`; a worker can use that as definition identity without treating it as live exposure. The console must not force users to create a production rollout merely to evaluate draft candidates.

## Missing execution and recovery contract

The logical work identity is **job/run × candidate × case × generation repeat**. Assessments also identify the criterion and evaluator configuration/repeat. A candidate pins its build/manifest, command contract, variant and publication or draft content hash. Each actual send has its own `AttemptId`.

Use durable admission and claims before dispatch. Preserve complete attempt-start/completion evidence and attach exact outputs without re-running completed work. A lost completion may mean a paid send occurred. Separate definitely not sent, completed but unreconciled, and spend uncertain. Uncertain work needs reconciliation or an explicit authorized retry decision; it does not automatically return to the queue.

The runtime defines `IExecutionJournal`, but the public standalone invocation API cannot currently attach one: the journal argument is on an internal runtime method and composition supplies it through its bridge. Its helper skips writes when no journal is configured. Composition `SlotJournal` gates sends against an authoritative claim, and the reference composition store is process-local with no leases or fencing. These mechanisms do not provide a durable multi-worker backtest service on their own. The [delivery proposal](delivery-proposal.md) chooses a control-plane job journal/claim implementation and a minimal optional public binding, without creating synthetic compositions or changing real aggregate-grade semantics. It also requires opt-in strict candidate preparation because a forced selector cannot currently prevent a later default fallback before a send.

Job progress separates execution, output storage and assessment. Cancellation stops new admissions and requests cooperative cancellation; already sent calls may finish or remain uncertain. Resume, retry failed work, and start a new run are different actions. A job tray and run detail can expose progress without keeping the user on one page.

## Workload origin is separate from experiment role

Add authoritative run context such as **Production, Backtest, Evaluator**, with **Primary/Shadow** as a separate role. Backtest generation and judge executions need explicit parent/run links. A reassessment job is distinct from its child evaluator calls. Exact naming is a protocol decision; the dimensions must not be collapsed.

Current experiment evidence has `ExperimentId`, `VariantId`, role and comparison group, but no production/backtest/judge marker. A build-pinned evaluation worker can therefore produce ordinary Primary experiment evidence. Production queries must use the authoritative workload-origin record, not infer production from Primary or an experiment ID. Missing provenance is **Unknown origin**, not production or control.

An ingestion/query layer must retain execution, storage and failure states even when no output exists. Idempotent delivery and reconciliation handle duplicates and late events. Best-effort event sinks and traces can feed diagnostic projections, but neither can establish a complete send journal. The current feedback port reads an exact output reference and has no list/search API; keep an independent read model rather than weakening exact-read authority.

## Replay readiness and retention

Before launching, check: authorized frozen typed input; retained source/assets or a host reference that resolves to verified immutable content; available target build; typed preflight; contract/binding compatibility; approved execution and tool policy. Readiness states include Ready, Missing input/asset, Redacted, Build unavailable and Execution policy blocked.

A trace link, rendered/redacted prompt, signed URL or mutable external document is not a reproducible case input. A host document reference is useful only with content identity, access and retention checks. Store permitted source documents, attachments, excerpts and locators independently of diagnostic span retention. Show when deleted/unavailable assets make an old report inspectable but no longer reproducible.

Backtests run in an authorized evaluation environment. Commands that use tools, retrieval or external dependencies need a declared captured/fixture/live policy. Do not silently call production side-effecting tools or silently substitute missing dependencies. The first supported path can use tool-free commands and host-approved side-effect-free pipelines while retaining explicit unsupported states for other commands.

Trace curation follows **Trace → exact outputs → selected output → dataset draft**. Preserve the original output, input/source identity, incident reason, tenant scope and available trace link. Check duplicates using source/case lineage. An owned task moves through Needs evidence, Ready for review, Disputed, Accepted into draft or Rejected. Creating the task or draft case does not adjudicate its reference.

## Backtest comparability and dataset purpose

Prefer executing baseline and candidates in the same run window. Comparing today's candidate against weeks-old stored baseline outputs also measures provider/model or dependency drift. Historical-baseline comparison remains useful when labelled explicitly; show execution times and requested/reported model identities. A same-window run reduces one confound but does not guarantee a fixed provider model.

Freeze repeats and their aggregation. Reduce repetitions and collection items within each case before aggregating cases. Use the same per-criterion eligible case population across candidates. Flag unplanned configured-default fallback or build/signature mismatch rather than presenting the output as though the requested variant ran.

Datasets need purpose/split and exposure metadata: tuning/regression, judge calibration, and held-out acceptance. Keep versions, corrections and near-duplicates of the same source/document family in the same split; choose time boundaries where the task requires them. Record whether cases were used in prompt examples, calibration or human reveal. A revealed/tuned-on case does not remain an untouched independent holdout.

An exploratory backtest can run on a selected regression set. Label the claim accordingly: passing those cases does not establish production superiority. Independent acceptance requires the split/exposure policy and adequate independent-case/slice evidence. Judge-led primary or required-guardrail decisions additionally need reviewed task calibration, false-pass/disagreement evidence and a predeclared admission policy. LLM judging stays in the requested design scope; it depends on the same job, evidence and review foundation.

## Version history, decisions and promotion

Each exact candidate configuration can have multiple benchmark results. Compare selected variants or selected versions against an explicit baseline, under the same frozen protocol. Result links retain input/reference, criterion/evaluator, output and grades-as-of identities. Save a decision frame with **Pass, Fail or Inconclusive**, its author/rationale, and the reports used.

Frozen reports stay valid for their own frame. A newer dataset or benchmark does not automatically invalidate history. Contextual labels include Content changed since evaluation, Not evaluated under selected benchmark, Historical baseline, Evidence unavailable, or an explicitly defined evidence-expiry policy. Nothing automatically reruns or inherits a predecessor's score.

Promotion must name its effect:

- Change allocation to a variant while retaining the experiment.
- Publish a variant as the new control.
- Change the host's configured default through deployment.
- Fold content into the compiled command through a new build/revision.

Those are different operations. A stop or rollback is also explicit, with a reviewed publication/configuration change; all-zero weights are not a reliable stop in today's selector behavior. Cross-environment promotion retains content provenance but has target-specific publication/activation, compatibility, credentials and policy. Offline evidence does not automatically prove target-environment behavior.

Publication acknowledgement, current target pointer and per-node adoption remain separate. Host reports identify served catalogs, build and binding/revision; absent reports mean Unknown. A publication does not repin in-flight compositions. Retain a decision/audit trail rather than infer promotion intent from changed weights.

## Paid-work, access and operational boundaries

Define who may read retained data, curate cases, grade/adjudicate, launch candidates/judges, publish and operate bindings. Existing actor/run authorization is a foundation; it is not the console's complete authentication, service identity or namespace policy. Keep case/source tenant scope through datasets, assessments and judge access. Detailed enterprise roles can follow a small initial policy, but access enforcement is not deferred for shared use.

Review candidate and judge work separately at launch. Pin concurrency, attempts/retries, context/output limits, quotas and route policy. Protect production capacity from offline work. Show known estimates and unknown coverage; stop future admissions within declared limits. In-flight calls and unknown usage/prices prevent an exact monetary-ceiling or exactly-once billing promise.

Implement retention across the control-plane database, publication targets, ledgers, case assets and permitted judge responses. A Referenced by view and explicit retention rules must precede deletion of data needed by reports or pins. Define migration, backup/restore and reconciliation behavior. Automated reference counting/garbage collection is a later optimization, not a reason to claim indefinite retention.

The remaining measurement gaps include intended assignment/unit on configured-default executions and retained case/end-to-end elapsed time. Until they exist, online comparisons remain descriptive and case-latency guardrails remain unverifiable. Attempt evidence and spans cannot fill those gaps by inference.

## Delivery recommendation

The [six-increment delivery proposal](delivery-proposal.md) turns these gaps into concrete scope, architecture choices and acceptance gates. Its checks are planned, not implemented or passed.

Complete one supported loop before adding another primary navigation area:

1. Register the build and connect an authorized build-pinned worker.
2. Freeze replay-ready cases, benchmark protocol and candidate content; enforce scoped launch/access policy.
3. Run durable backtest/reassessment jobs with explicit workload origin and recovery states; retain exact outputs and assessments.
4. Compare variants/versions with baseline, coverage, evidence and a frozen decision record.
5. Publish explicitly, observe known/unknown adoption, and turn a trace-linked incident into an owned regression/reference review task.

Scoped LLM criteria and human calibration fit that loop. Independent holdout claims require their own data policy. CI gates, schedules, alerts, failure clustering, automatic promotion/garbage collection and statistical winner claims can follow. CI must consume the same immutable decision frame; cancelled, missing, unknown or unreconciled evidence cannot silently pass.

Keep Commands, Experiments, Traces, Datasets and Review. Put version history/diffs under Commands; backtest jobs and comparisons in the Experiments work area with command/benchmark links and no mandatory live experiment; exact output links under Traces; dataset purpose/readiness under Datasets; owned grading/reference/judge tasks under Review; bindings, access, audit and stores under Settings. A global job tray can show background progress.

## Source checks

- `Commands/CommandRegistration.cs`: generated registrations expose CLR types and metadata, not a complete portable manifest.
- `Context/OperationContext.cs`: best-effort `IExecutionEventSink`, awaited host `IExecutionJournal`, and null-journal behavior.
- `Commands/Composition/InvocationBridge.cs`: `SlotJournal` and indeterminate post-send completion.
- `Commands/Composition/CompositionStore.cs`: conditional slot transitions, no leases/fencing, process-local reference store.
- `Evaluation/Contracts.cs` and `Evaluation/InMemoryFeedbackLedger.cs`: exact reads, actor/run access, feedback operations and process-local durability limits.
- `Experiments/Evidence.cs` and `Commands/CommandRuntime.Experiments.cs`: actual configuration/role capture, capture-policy limits, missing workload-purpose and intended-arm fields.
- `Experiments/ShadowScheduler.cs`: process-local work delegates and bounded queue, not a durable batch runner.
- `Routing/ProfileRouter.cs`: requested/reported model, attempt identifiers, nullable usage and per-attempt duration.

All paths above are under `src/Zeeq.AgentCmd`. Team recommendations were reviewed for safe resume, historical report validity, explicit workload origin, mandatory access/paid-work boundaries and retained LLM-judge scope. No production code or accepted screen image was changed.
