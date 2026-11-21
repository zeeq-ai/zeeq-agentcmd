/**
 * Management API resource types, transcribed from the backend contract (2026-11-05).
 *
 * The envelope is exact. Bodies are typed from the contract but stay defensive: the server stores
 * `body` as a JSON value, so screens render missing members as "unknown" and always offer the raw
 * JSON. The envelope uses Kubb-generated OpenAPI types. Module body types remain authored until the
 * server publishes typed schemas for them.
 */

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue }
export type JsonObject = { [key: string]: JsonValue }

import type { ManagementResource } from '../generated/types/ManagementResource'

/** Body semantics come from the module contract; envelope fields come from OpenAPI. */
export interface ApiResource<TBody = JsonValue> extends Omit<ManagementResource, 'body'> {
  body: TBody
}

/** POST create and PUT update request. POST /builds may also carry `id`. */
export interface ResourceWrite<TBody = JsonObject> {
  id?: string
  name: string
  body: TBody
}

/** Problem details returned by the API. `revision` accompanies a 409 conflict. */
export interface ApiProblem {
  type?: string
  title?: string
  status?: number
  code?: string
  detail?: string
  revision?: number
  errors?: Record<string, string[]>
}

/** Build bodies come from a registered manifest; the console treats them as opaque but readable. */
export interface ManifestCommand {
  id?: string
  revision?: number
  commandId?: string
  commandRevision?: number
  contractSignature?: string
  definitionHash?: string
  runtimeOverrides?: { name?: string; jsonName?: string }[]
  input?: { schema?: JsonValue }
  output?: { schema?: JsonValue }
  baselineTemplates?: { system?: string; user?: string } | null
  overridableParameters?: string[]
  inputSchema?: JsonValue
  outputSchema?: JsonValue
  systemTemplate?: string
  userTemplate?: string
}
export interface BuildBody {
  buildId?: string
  formatVersion?: number
  catalogId?: string
  manifest?: BuildBody
  commands?: ManifestCommand[]
}

export interface VariantBody {
  id: string
  weight: number
  systemMode?: 'inherit' | 'remove' | 'replace'
  systemTemplate?: string | null
  userTemplate: string
  parameters: JsonObject
  route?: {
    key: { id: string }
    provider: string
    model: string
    temperature?: number
    defaults?: { temperature?: number; maxOutputTokens?: number; topP?: number; topK?: number; reasoningEffort?: string; reasoningOutput?: string }
  }
}

export interface PublicationCommandBody {
  commandId: string
  commandRevision: number
  contractSignature: string
  experimentId: string
  controlVariantId: string
  variants: VariantBody[]
}

export interface PublicationBody {
  buildId: string
  commands: PublicationCommandBody[]
}

export type ReferenceCompleteness = 'Partial' | 'Exhaustive' | 'None'

export interface DatasetCaseBody {
  id: string
  sourceFamilyId: string
  input: JsonObject
  assets: JsonValue[]
  tags: string[]
  reference?: { value: JsonValue; completeness: ReferenceCompleteness }
  exposed?: boolean
  holdoutWithheld?: boolean
}

export interface DatasetBody {
  purpose: 'Regression' | 'Calibration' | 'Acceptance'
  cases: DatasetCaseBody[]
}

export type CriterionScope = 'Field' | 'Subtree' | 'Collection' | 'Whole'
export type ScorerKind = 'Human' | 'Exact' | 'Numeric' | 'Llm'

export interface CriterionBody {
  id: string
  version: number
  name: string
  scope: CriterionScope
  outputBinding: string
  referenceBinding?: string
  sourceBinding?: string
  matching?: { order: 'Preserve' | 'Ignore'; duplicates: 'Count' | 'Collapse'; unit: 'Item' | 'AtomicFact'; paraphrases: boolean; decompositionConfigId?: string }
  scorer: { kind: ScorerKind; version: string; tolerance?: number; judgeConfigId?: string }
}

export interface GuardrailBody { id: string; metric: string; operator: 'Gte' | 'Lte' | 'Eq'; threshold: number; minimumCoverage: number; required: boolean }

export interface BenchmarkBody {
  datasetId: string
  criteria: CriterionBody[]
  primaryCriterionId: string
  repeats: number
  guardrails: GuardrailBody[]
}

export type JobKind = 'Backtest' | 'Reassess'

export interface JobCandidate {
  id: string
  name: string
  buildId: string
  publicationId: string
  commandId: string
  variantId: string
}

/** POST /jobs body. Saving never launches a model call by itself. */
export interface JobRequestBody {
  kind: JobKind
  datasetId: string
  benchmarkId: string
  candidates: JobCandidate[]
  executionApproved: true
  sourceJobId?: string
}

/** Job body as stored. Status, items and counts are server-owned; their exact shapes are pending. */
export interface JobBody extends Partial<JobRequestBody> {
  holdoutWithheld?: boolean
  status?: string
  items?: JsonValue[]
  counts?: Record<string, number>
}

export type Workspace = import('../generated/types/WorkspaceDefinition').WorkspaceDefinition
export type Build = ApiResource<BuildBody>
export type Publication = ApiResource<PublicationBody>
export type Dataset = ApiResource<DatasetBody>
export type Benchmark = ApiResource<BenchmarkBody>
export type Job = ApiResource<JobBody>
export type Execution = ApiResource<JsonObject>
export type Review = ApiResource<JsonObject>

export type Assessment = ApiResource<JsonObject>
export type Comparison = ApiResource<JsonObject>

/** Comparison summary is server-computed against the saved evidence and frozen protocol. */
export interface ComparisonReport {
  comparisonId: string
  primaryCriterionId?: string | null
  datasetHash: string
  benchmarkHash: string
  baselineCandidateId: string
  baselineAmbiguous: boolean
  independentAcceptanceEligible: boolean | null
  holdoutWithheld?: boolean
  candidates: { key: string; candidateId: string; buildId: string; buildHash: string; publicationId: string; publicationHash: string; commandId: string; variantId: string }[]
  criteria: { criterionId: string; criterionVersion: number; commonCaseIds: string[]; candidates: { candidateKey: string; expectedCases: number; eligibleCases: number; scoredCases: number; missingCases: number; unreconciledCases: number; mean: number | null; matchedMean: number | null; cases: { caseId: string; status: string; score: number | null; assessmentIds: string[] }[] }[] }[]
  guardrails: { id: string; status: string }[]
}
