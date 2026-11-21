# Semantic judges and incomplete references

Design extension, 2026-11-05. Reviewed with DMTS, the customer advocate, and the product designer. The user explicitly needs LLM judges for whole outputs, JSON subtrees, and collections, including improvements that return valid values absent from an earlier reference. This is planned behavior; no judge runner or model calls are implemented here.

## Decision

Support an explicitly configured **LLM judge** scorer alongside human and deterministic scorers. It judges a named criterion at a pinned scope. It is not a general smart-equality switch and cannot update references automatically.

Keep two independent questions visible:

- **Reference agreement:** which listed reference items are present with equivalent meaning?
- **Source grounding:** which output claims are relevant to the task and supported by the authorized source?

Absence from a reference is not evidence that an extraction is wrong. Presence in a reference is not proof that it is right. A prior model output is an example/baseline until its assertions and coverage have been reviewed. Record reference provenance and review status separately from its completeness setting.

## Scope, bindings, and equivalence

Judge targets can be a field, a JSON subtree, a collection, or the whole output. Preserve the exact original `OutputRef` or sealed aggregate, target pointer, and applicable manifest/contract. Reviewers can inspect the full output, but scoring does not silently expand beyond the configured scope.

Pin four inputs: output binding, optional reference binding, authorized source/context binding, and rubric. The source is retained task evidence, such as a frozen case document with a content identity. A rendered prompt, candidate claim, or unverified citation is not independent source authority. If variants change source data, the benchmark must explicitly define its authoritative task evidence and comparability; do not let a candidate replace the truth it is judged against.

Compare parsed values rather than serialized JSON text. Object-member order is irrelevant. Array order and duplicate semantics are explicit per collection: ordered list, unordered set, or unordered multiset. Nested arrays can use different rules. Configure string normalization, numeric tolerance, units/currency, dates/timezones, and task-equivalent paraphrases explicitly. Preserve negation, quantities, entities, qualifiers, and other task-relevant distinctions.

Use deterministic matching where it suffices. A collection judge can propose semantic matches for the remainder, with enough context to judge the selected criterion. Grounding still checks all relevant claims, including those matching the reference. A holistic subtree criterion can instead select a structured rubric level; it need not be forced into item matching.

The declared match unit is **item** or **atomic fact**. For item matching, enforce one-to-one assignment so a reference item cannot earn duplicate credit. Fact matching can establish that reference `["A and B"]` and candidate `["B", "A"]` express the same content. Pin the decomposition rule/prompt/version, apply it consistently to reference and candidates, and retain fact-to-original-item provenance. Model-generated decomposition is reviewable assessment data, not new ground truth. Enforce no double counting at the fact level.

## Reference completeness

Coverage is frozen per criterion and case and is identical for every candidate. The curator confirms the setting before candidate inspection; do not silently assume every reference is exhaustive.

| Reference mode | What can be reported | Treatment of unmatched candidate content |
| --- | --- | --- |
| Exhaustive for the declared task/source scope | Scoped precision/recall/F1 under the pinned matching rule | Strict reference mismatch remains in the frozen metric; a supported relevant extra disputes completeness and needs review |
| Partial | Known-reference coverage; source-grounded validity separately | No penalty solely because an item is absent from the reference; assess its support, relevance, duplicates, and contradictions |
| None | Source-grounded/task-rubric assessment and format checks | Assess against authorized source and task; reference coverage is N/A |

A partial reference cannot establish total recall or full extraction completeness. Do not call its matched/listed ratio total recall. Do not compute a gold-based false-positive count by treating every unlisted value as wrong. Empty collections and zero-denominator cases need an explicit pinned policy; `0/0` is not automatically a perfect score.

If an exhaustive reference is challenged, do not change it to Partial after seeing who benefits. Preserve the strict historical result and attach a clearly dated dispute. The new judge protocol should leave clean acceptance unresolved where the reference's correctness/completeness is in dispute. A disputed reference claim and a contradicted candidate claim are different findings.

## Extra items and source coverage

For every unmatched task-relevant claim, record one of the following findings, with judge/human provenance:

| Finding | Meaning |
| --- | --- |
| Additional — source-supported | The judge found supporting evidence in the reviewed scope; it can contribute under a predeclared metric, but is not automatically adjudicated truth |
| Additional — unsupported in reviewed source | Required support was not found in a sufficiently reviewed task-source scope; a declared extraction-grounding rule can fail |
| Additional — contradicted by source | Source evidence conflicts with the candidate assertion; report the contradiction explicitly |
| Additional — needs review | Evidence, source coverage, matching, or judgment is insufficient; neither failure nor credit is invented |
| Reference/source conflict | A reference assertion conflicts with source, or a supported relevant addition challenges an exhaustive reference |

Not found in the reviewed source is not the same as false in the world. If source is missing, redacted, truncated, or coverage is unknown, do not make an unsupported negative/completeness claim. A verifiable positive citation can still support a scoped finding when the source is partial; that does not prove global completeness or absence of counterevidence. Show what the judge actually saw: pages, excerpts, or byte/text scope and omissions.

Keep known-reference coverage, source-grounded validity, and supported discoveries separate. A valid new item can be recognized as a discovery without reducing agreement with a Partial reference. Do not give an unbounded bonus for more text or duplicate assertions: additions must be distinct, task-relevant, and supported under the frozen rule, with case-level aggregation. A grounding guardrail can prevent a high coverage score from hiding invented extras.

## Concrete example

Assume the authorized source supports task facts A, B, and C. The frozen Partial reference lists only `[A, B]`. Collection order is explicitly ignored, and equivalent paraphrases are allowed.

| Candidate | Known-reference coverage | Additional findings |
| --- | --- | --- |
| `[A]` | 1 of 2 | Missing known B |
| `[B, A, C]` | 2 of 2 | C is an additional source-supported discovery; reordering is allowed |
| `[B, A, C, D]` | 2 of 2 | C is supported; D fails grounding if sufficient reviewed source provides no support, or remains unresolved if evidence is insufficient |

The third candidate must not win merely by returning more values. A suitable protocol could use known-reference coverage as Primary, grounding as a required guardrail, and supported discoveries as a separate secondary metric. Other task-level primaries are possible if defined before results and calibrated. With an Exhaustive reference, supported C instead raises a reference-completeness dispute.

## Judge assessment and controls

Store structured findings, not just a free-form score: target and input/reference identities, matched item/fact mappings and types, source locators, contradictions, unresolved items, rubric level, concise rationale, and evaluator identity. Each fact links to the original output/reference location. The host validates result shape, declared matching cardinality, unique credit, and evidence locators. A quote existing at its cited location proves text presence, not entailment.

Compute metrics from validated findings or map declared rubric levels to scores. Keep evaluator errors, abstentions, unsupported coverage, and repeat disagreement distinct from zero. Self-reported judge confidence is not a calibrated probability or an automatic acceptance threshold. Automated results use assessment records; do not impersonate a human `GradeEvent` grader.

Pin the judge route, requested/reported model and version where available, prompt/rubric/result-schema versions, settings, context-selection/window policy, decomposition and matching rules, repetitions/aggregation, ordering, bounded retries, and failure policy. A model alias with unknown resolved version does not become an exact reproducibility claim. Retain permitted judge responses and evidence under the host capture policy; request concise findings rather than hidden reasoning.

Treat source and output as untrusted data. Keep rubric instructions separate, validate structured responses, and use an authorized judge route/data policy. Judge failure affects the assessment, not the original model execution's result. Use the existing provider-neutral execution/capture seams for judge calls, with separate evaluation identity and accounting.

Blind candidate/model identity where feasible. If pairwise judging is used, counterbalance presentation order under a preset schedule and retain individual judgments/disagreement. Do not retry until a favorable answer appears. Calibrate on human-labelled task examples before a judge drives a Primary or required acceptance rule; define the acceptance policy in advance. Selecting a different model family or repeating calls does not guarantee unbiased or deterministic judgments. Position, verbosity, and self-preference limitations have been documented in the primary [MT-Bench judge study](https://arxiv.org/abs/2306.05685).

Source grounding and reference agreement are deliberately separate. As related methodological evidence, [Ragas faithfulness](https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/faithfulness/) checks response claims against supplied context. That concept does not imply the supplied context/reference is complete or mandate a Ragas dependency.

## Adjudication and fair reassessment

An extra item or reference conflict creates a proposal with supporting/contradicting evidence. It is not a silent reference edit. A reviewer can Accept into dataset draft, Reject, or Request review. Hide which candidate proposed it where feasible, while retaining that provenance for audit.

Adjudication leads to a new dataset version and benchmark version. Reassess every compared candidate configuration under the same revised protocol and eligible case set. Reassessing exact retained original outputs does not require generating replacement model outputs; judge calls remain separate paid work. Never add new truth only to the candidate that discovered it, correct its output to improve the score, or overwrite old assessments/reports.

If actual task inputs/source content change rather than their reference or capture availability, review whether old outputs are still comparable. New source inputs can require new case/run identities and new model executions. Do not score an old output as though it had received a changed document.

## UI and launch changes

Extend the existing criterion slideover, not the main navigation. Expose scorer **LLM judge**, scope, separate output/reference/source bindings, reviewed source coverage, reference completeness/review status, ordering/duplicates/match-unit rules, and the pinned judge summary. Keep full prompts/settings in Advanced while Primary, evidence gaps, disputes, and unsaved changes remain visible.

The case comparison drawer has **Exact JSON diff** and **Semantic review** tabs. Semantic review shows Matched meaning, Missing known reference, Additional source-supported, Unsupported in reviewed source, Contradiction, and Unresolved, with concrete candidate/reference pointers and source excerpts. Show **Judge assessment — not human-verified** or the actual human-review status. Source locators resolve into retained, authorized evidence, not a quote invented by the judge.

**Review reference gap** opens the existing dataset-draft adjudication flow. The report retains exact variant/configuration pins, benchmark/reference/judge versions, eligible/assessable/matched counts, and grades-as-of. Protocol and source-coverage changes remain visible boundaries.

Saving/freezing configuration and ordinary template rendering make no model calls. **Preview judge on one case** and **Start judging** are explicit model-call actions with a launch review that covers context, calls, repeats/retries, routes, and estimated price coverage. Separate candidate-generation cost and judge cost; also show their combined evaluation-budget total, counting each attempt once. Unknown usage/prices stay unknown.

No live model calls, provider claims, new screenshots, or production APIs are introduced by this design update. Required implementation work includes retained case/source evidence, judge configuration/execution, structured assessment storage, evidence locators, calibration, authorization, and judge accounting.
