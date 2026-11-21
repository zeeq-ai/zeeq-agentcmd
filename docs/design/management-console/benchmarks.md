# Benchmarks and primary metrics

Design decision, 2026-11-05. Reviewed with the DMTS, customer advocate, and product designer. This extends the [console design](README.md). It implements no scoring engine, API, or command attribute.

## Decision

Grade explicitly selected criteria. Do not grade every output field by default. A new schema field starts **Not scored**. Required structural and typed validation still applies to the entire output.

A field identifies where to look. A criterion defines the question and how to judge it. A metric defines how criterion results or operational observations are summarized. A benchmark version chooses its primary metric, secondaries, and acceptance rules.

The manifest is structural authority. The benchmark is evaluation policy. Do not put a permanent Primary flag on a schema property: one field can support several criteria, and different benchmarks can prioritize different questions about the same output. No new C# attributes are needed for v1. Future manifest suggestions would be advisory; hints must never change a frozen benchmark or start judging work.

## Minimal model

| Object | Responsibility |
| --- | --- |
| Command manifest | Structure, serializer names, types, requiredness, and capabilities; not business priority |
| Dataset version | Frozen cases, input snapshots, reference answers/adjudication, and provenance |
| Benchmark version | Frozen protocol: dataset/population, selected criteria, bindings, evaluators, metrics, primary, guardrails, and policies |
| Evaluation run | Benchmark version and candidate build/manifest, variant, publication or draft content hash, plus execution/grading configuration |
| Assessment | A criterion result for an identified case/candidate/output, including score or a non-score outcome and evaluator provenance |

Criteria have stable task-level IDs and versioned meanings, such as `invoice.payable_amount_correct`. A benchmark retains the exact criterion definitions it uses. A separately editable global criteria catalog is not required for the first screen; reusable definitions can be copied with provenance without floating references to newer rubrics.

### Metric roles

- **Primary:** exactly one confirmed headline metric in a frozen v1 benchmark. It can summarize a field, collection, or explicit multi-field/whole-output criterion. It is not necessarily the average value of a field.
- **Secondary:** separate diagnostic scores. Do not silently average them into the primary or use them to declare a different winner.
- **Guardrail rules:** named metrics with explicit thresholds, direction, evidence coverage, and acceptance behavior. A rule can reference the primary metric itself. Validity, storage, latency, and cost guardrails need not target output fields.
- **Not scored:** visible fields that create no quality-assessment work. This is different from Pending, Not applicable, and a real zero.

If several primary fields matter together, define one task criterion, such as **amount within tolerance AND currency correct**. Do not invent weights or pool unrelated scores. Arbitrary weighted composites and multiple unqualified primaries are outside the initial default.

An explicit **All required invoice fields correct** criterion is possible. It is a named rule tied to a contract version, not an automatic mean over every current field. An all-field reference diff remains useful as a diagnostic without becoming a benchmark score.

## Targets and scorers

The editor uses a pinned manifest to select a field, collection, multiple fields, or whole output. Each criterion records its question, rubric/comparator, scale, scorer version/configuration, applicability, and reference requirements. Select scorers explicitly: a string does not imply case folding; an array does not imply set matching.

[Semantic judges and incomplete references](semantic-judging.md) adds explicit LLM-judge scopes, declared equivalence, source grounding, and Exhaustive/Partial/None reference modes. A valid addition absent from a Partial reference is not an absence-only error. Judge-supported additions are reviewable findings, not automatic reference updates.

Keep **Output binding** and **Reference binding** separate:

| Binding | Amount | Currency |
| --- | --- | --- |
| Revision 7 output | `/total` | `/currency` |
| Revision 8 output | `/Totals/Amount` | `/Totals/Currency` |
| Frozen reference in revision 7 shape | `/total` | `/currency` |

Mappings are reviewed against output signatures/manifests and frozen with the benchmark. Missing mappings are **Unmapped**, not N/A. Changed wire names/shapes require binding review. Changed business meaning requires a new criterion definition/version. Cross-contract input compatibility retains the separate gate in the console plan.

Whole output uses the empty JSON Pointer `""`. Collections use their collection pointer plus explicit matching, ordering, duplicate/missing-item, and scoring rules. `/lineItems/*/amount` is not a concrete JSON Pointer. An item grade may use `/lineItems/2/amount`, but index 2 does not identify the same business item across outputs. Per-item evidence is optional; assessment can operate on the whole collection.

Reduce item results within a case first, then aggregate cases. The default gives each eligible invoice equal weight. Repeated generations also reduce within the case under the pinned policy. A 100-line invoice must not receive 100 votes against a one-line invoice by accident.

## Versioning

Review and freeze the benchmark before its first evaluation launch. Pin:

- Dataset version, population/subset, reference identities, cohorts/slices, and per-criterion eligibility/applicability.
- Criterion IDs/definitions and output/reference mappings for supported contracts.
- Evaluator identity/version/settings, rubric/comparator, and explicit normalization/tolerance.
- Primary/secondary metrics, aggregation unit/direction, case weighting, and repetition policy.
- Guardrail thresholds, required coverage, and acceptance rules.
- Missing-target/reference, invalid-output, execution-failure, evaluator-failure, N/A, and unavailable-evidence policies.

Changing these creates a new benchmark version. A new build with a supported unchanged contract can be another candidate; changing evaluation meaning or adding an unmapped contract needs review/versioning. Candidate publication identity belongs to run pins, not a benchmark's mutable latest value.

Reports retain benchmark/run identity and **grades as of** time. Frozen reports retain their included assessment identities; live reports label later grading updates. Viewing a declared secondary is allowed without editing the benchmark, but does not replace its primary decision. New post-hoc criteria or primary changes are exploratory and become a new draft; they never rewrite prior reports. Freezing a new version after seeing results does not make earlier results an independent confirmation.

## Eligibility, missing data, and guardrails

Decide eligibility per criterion from pinned inputs/reference policy before seeing outputs. Both arms use the same eligible cases. If 88 of 120 cases have the reference required by an amount comparator, show **Eligible 88 · Ineligible 32 — no reference**. Do not silently use human grading for the other 32 under another protocol. A deliberate fallback evaluator needs a pinned definition and visible provenance.

An eligible case with an unmapped candidate, failed execution, missing field, or unavailable output stays visible. Do not drop it to improve scores. Report dataset total, eligible/applicable, attempted/executed, valid response, stored, assessed/graded, and matched pairs with explicit units. Validity, storage success, and grade coverage are distinct.

Quality means normally summarize scored cases with coverage shown. A task-success metric can explicitly count known execution/invalid-output/required-field failures as failures. This is a declared decision rule, not a fabricated zero GradeEvent for an output that does not exist. Pending grades, evaluator errors, and unavailable evidence remain unresolved. Below the pinned evidence requirements, show **Insufficient evidence**.

Assessment states include Scored, declared Task failure, Pending, Not applicable, Target missing, Unmapped, No stored output, Evaluator error, Ineligible/no reference, and Unreconciled. N/A follows the frozen applicability rule; it is not an escape for poor results. Unreconciled results show **Not counted — reconciliation pending**.

Guardrails return Pass, Fail, or Unknown/unverifiable. Unknown is not a pass. Known-cost-per-priced-case must show its priced denominator; it does not establish a total cost bound for unknown cases. An all-case cost rule needs sufficient complete evidence or a declared defensible bound.

Case/end-to-end p95 requires defined, retained case/execution elapsed evidence. Current `Attempt.DurationSeconds` measures one provider attempt. Summing it cannot establish full-case latency: backoff, other work, and concurrency change the meaning. Until the needed host evidence exists, that latency guardrail is **Unverifiable**. Best-effort spans do not substitute for its accounting source.

## Exact output identity and existing grades

Model comparison selects the pinned original model output or sealed aggregate by default. A correction is human-produced, not another model execution. It remains reviewable and can be adjudicated as a reference in a dataset draft. It must not replace an original output to improve historical model scores. Screen 5's correction-grading image is an ad-hoc review example; benchmark grading names the original target and pinned criterion. In model-comparison context, show **Corrections do not count toward model comparison**.

The current ledger supports exact `OutputRef`, a concrete JSON Pointer, finite score `[0,1]`, rationale, authenticated grader, and explicit supersession. It lacks benchmark/criterion/evaluator identity and an N/A score. `GradeRequest.ExpectedValue` means the current observed value for conflict detection, not the reference answer. A missing field cannot be graded at an unresolved pointer; a human can grade its existing parent or whole output with criterion context explicit.

Add a host-owned assessment record in the control plane with its own identity, benchmark version/run/case/candidate, criterion/bindings, exact input/reference identity, evaluator/configuration, outcome, and score when applicable. Optionally link a human GradeEvent by event ID and exact OutputRef. Missing outputs, N/A, and evaluator errors can have assessments without numeric ledger grades. Deterministic results need not impersonate human feedback.

Two-store delivery requires idempotency/reconciliation. An accepted grade with an incomplete assessment link is not silently counted. Preserve actor/run access and the grade receipt. Benchmark supersession checks criterion/evaluator context in addition to the ledger's same-grader/target/pointer rule. Never join scores by pointer alone or encode benchmark identity into an authenticated grader name.

**Quick grade** remains ad-hoc feedback and is excluded by default. **Grade for benchmark vN** supplies its frozen criterion/rubric. No automatic cross-benchmark reuse in v1. Reuse within a benchmark must retain exact output, input/reference, criterion, evaluator configuration, and source assessment. Changed references or rubrics invalidate equivalence. Do not reinterpret free-form feedback as if it followed a new rubric.

## Invoice example

Illustrative **Invoice payable amount v1**, dataset **invoices-regression v4**:

| Purpose | Selection |
| --- | --- |
| Primary | Payable amount correct: total plus currency, with explicit reference, currency, and tolerance rules |
| Secondary | Vendor correct; optional line-item completeness with declared collection matching |
| Guardrails | Predeclared validity/storage coverage; cost or elapsed rules when their evidence is available |
| Not scored | Explanations, confidence, internal IDs, and other fields unless selected |

The amount/currency criterion can bind to revision 7's flat fields or revision 8's nested fields. The headline is task correctness, not an average of vendor, total, currency, notes, and every future field.

## UI and actions

### Variant and version comparisons

Benchmark results must surface on variants and their configuration history, not only on a benchmark detail page. Grades remain attached to exact outputs; reports aggregate those assessments for an explicitly selected candidate and benchmark context.

A candidate identifies application/command, build/manifest and contract revision, experiment and variant, and an immutable publication revision or draft content hash. Preserve the effective configuration identity and route/fallback provenance on its outputs. A variant ID reused in a newer publication is a separately identified candidate configuration. Do not pool its versions by name.

Use explicit labels such as **short at publication 2026-11-03.2** and **short at publication 2026-11-05.1**. Distinguish configuration publication, compiled command revision, benchmark version, dataset version, and exact output version. A standalone mutable Prompt version entity is still unnecessary.

| Surface | Required benchmark and grade information |
| --- | --- |
| Variant list/detail | Selected benchmark/version and dataset, Primary result, assessed/eligible coverage, guardrail state, exact configuration/build pins, evaluation time and grades-as-of; links to runs and exact graded outputs |
| Variant configuration history | Immutable publication or draft hash per entry, its benchmark evidence and freshness, and **Compare selected versions** |
| Experiment comparison | Control/baseline and candidate pins, one shared benchmark/dataset protocol, Primary and secondary deltas on matched cases, guardrails, failed/pending/unmapped cases, and exact-output drill-down |
| Benchmark detail / Runs | Candidate-by-run matrix across variants and supported command builds, with links back to each exact variant configuration |
| Review queue / output inspector | Candidate variant/configuration, benchmark version, criterion/evaluator and reference identity, exact output target, and grade/supersession history |
| Production trends | Variant/configuration series split at publication changes, benchmark/protocol boundaries, coverage and grades-as-of; production remains descriptive under current assignment-evidence limits |

**Compare variants** and **Compare versions** use the same comparison model. One compares different variant IDs; the other can compare the same variant across publications, drafts, or builds. Multiple candidates can be selected, with an explicit baseline. Build/schema changes require the reviewed cross-contract bindings and input-compatibility gate.

Default comparable scores use the same frozen benchmark version, dataset/case population, evaluator policy, and reference identities. Show both per-candidate results and matched-pair coverage; incomplete cases remain visible. Different benchmark versions can be inspected side by side as a protocol/history comparison, but do not present an unqualified score delta or winner. Reassessment under one selected protocol creates new, traceable assessment results.

When a variant configuration changes after evaluation, show **Configuration changed since evaluation** and retain the old result under its exact pin. A new configuration without results shows **Not evaluated on benchmark vN**. Never silently carry a previous configuration's score into the current row. A variant can have results for several benchmarks; the benchmark selector and version stay visible rather than assigning one universal score to the variant.

These are required follow-up surfaces for the benchmark UI extension. The nine existing mockups show candidate pins and comparison scaffolding, but do not yet render all of these benchmark-specific panels.

### Benchmark authoring and review

Keep the five main navigation areas. Add **Benchmarks** in the command workspace, linked from datasets, experiments, studies, and evaluation setup. Benchmark tabs are **Overview · Criteria · Runs · History**.

The Criteria tab shows a manifest schema tree beside explicit criterion rows. The tree shows **Used by N criteria**, not an exclusive role per field. A metric section selects Primary, secondaries, and guardrail rules. Unselected fields collapse under **Not scored**. A slideover separates output/reference bindings and advanced evaluator settings.

Flow: **Create benchmark draft → choose manifest and frozen cases → add criteria/scorers → choose primary and guardrails → validate coverage/bindings → review and freeze version → evaluate candidates or grade exact outputs → inspect results**. Freezing neither publishes runtime configuration nor starts model calls. **Evaluate on benchmark** opens setup/review; paid generation or future judge calls require explicit launch.

Primary, failed/unknown required guardrails, missing bindings, and unsaved changes remain visible. Reports default to the frozen Primary. Benchmark queues prioritize it while showing the full output. Trends show protocol/evaluator versions, relevant counts/coverage, and grades-as-of; protocol changes form visible boundaries.

Scorer designs include human rubrics, versioned deterministic comparators, and explicitly configured scoped LLM judges. Judge model/prompt/settings, input/reference scope, repetitions, provenance, cost, calibration, and launch authorization are specified in the semantic-judging extension. Comparator behavior, judge execution, case matching, thresholds, and launch checks need implementation/contract tests; none is claimed to exist today.

## Team outcome and source checks

DMTS, customer advocate, and designer agree on explicit criteria, one confirmed Primary, separate guardrails, immutable protocols, exact output authority, and no new C# attributes. The lead resolved field roles into criterion/metric selection and retained separate reference bindings, optional grade links, equal-case aggregation, and Unknown guardrail states.

Source checked: `Evaluation/Contracts.cs`, `Evaluation/InMemoryFeedbackLedger.cs` (conflict/supersession), `Evaluation/JsonPointer.cs` (existing-value/root pointers), and `Routing/ProfileRouter.cs` (attempt timing). The nine existing images are unchanged. The benchmark screen is a design extension, not a newly rendered or implemented screen.
