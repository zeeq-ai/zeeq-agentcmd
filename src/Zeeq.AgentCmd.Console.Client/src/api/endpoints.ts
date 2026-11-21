/**
 * Typed endpoint functions for the management API, one per server route.
 *
 * Writes follow the server contract: creates POST `{ name, body }` and return 201; edits PUT the
 * same shape with a mandatory If-Match revision; freezes POST an empty body with If-Match. Build
 * registration is immutable, so builds have no update. Saving a job never launches model work by
 * itself; a worker must claim it.
 */
import { request, type ApiResult } from './client'
import type {
  Benchmark,
  BenchmarkBody,
  Build,
  Dataset,
  DatasetBody,
  Execution,
  Job,
  JobRequestBody,
  JsonObject,
  Publication,
  PublicationBody,
  ResourceWrite,
  Review,
  Assessment,
  Comparison,
  Workspace
} from './types'

const path = (collection: string, id: string, action?: string): string =>
  `/${collection}/${encodeURIComponent(id)}${action ? `/${action}` : ''}`

export const getWorkspace = (signal?: AbortSignal): Promise<ApiResult<Workspace>> =>
  request<Workspace>('/workspace', { signal })

export const listBuilds = (signal?: AbortSignal) => request<Build[]>('/builds', { signal })
export const getBuild = (id: string, signal?: AbortSignal) => request<Build>(path('builds', id), { signal })
export const registerBuild = (write: ResourceWrite<JsonObject>) =>
  request<Build>('/builds', { method: 'POST', body: write })

export const listPublications = (signal?: AbortSignal) => request<Publication[]>('/publications', { signal })
export const getPublication = (id: string, signal?: AbortSignal) =>
  request<Publication>(path('publications', id), { signal })
export const createPublication = (write: ResourceWrite<PublicationBody>) =>
  request<Publication>('/publications', { method: 'POST', body: write })
export const updatePublication = (id: string, revision: number, write: ResourceWrite<PublicationBody>) =>
  request<Publication>(path('publications', id), { method: 'PUT', body: write, ifMatch: revision })
export const freezePublication = (id: string, revision: number) =>
  request<Publication>(path('publications', id, 'freeze'), { method: 'POST', ifMatch: revision })

export const listDatasets = (signal?: AbortSignal) => request<Dataset[]>('/datasets', { signal })
export const getDataset = (id: string, signal?: AbortSignal) => request<Dataset>(path('datasets', id), { signal })
export const createDataset = (write: ResourceWrite<DatasetBody>) =>
  request<Dataset>('/datasets', { method: 'POST', body: write })
export const updateDataset = (id: string, revision: number, write: ResourceWrite<DatasetBody>) =>
  request<Dataset>(path('datasets', id), { method: 'PUT', body: write, ifMatch: revision })
export const freezeDataset = (id: string, revision: number) =>
  request<Dataset>(path('datasets', id, 'freeze'), { method: 'POST', ifMatch: revision })

export const listBenchmarks = (signal?: AbortSignal) => request<Benchmark[]>('/benchmarks', { signal })
export const getBenchmark = (id: string, signal?: AbortSignal) =>
  request<Benchmark>(path('benchmarks', id), { signal })
export const createBenchmark = (write: ResourceWrite<BenchmarkBody>) =>
  request<Benchmark>('/benchmarks', { method: 'POST', body: write })
export const updateBenchmark = (id: string, revision: number, write: ResourceWrite<BenchmarkBody>) =>
  request<Benchmark>(path('benchmarks', id), { method: 'PUT', body: write, ifMatch: revision })
export const freezeBenchmark = (id: string, revision: number) =>
  request<Benchmark>(path('benchmarks', id, 'freeze'), { method: 'POST', ifMatch: revision })

export const listJobs = (signal?: AbortSignal) => request<Job[]>('/jobs', { signal })
export const getJob = (id: string, signal?: AbortSignal) => request<Job>(path('jobs', id), { signal })
export const createJob = (write: ResourceWrite<JobRequestBody>) =>
  request<Job>('/jobs', { method: 'POST', body: write })

export const listExecutions = (signal?: AbortSignal) => request<Execution[]>('/executions', { signal })
export const getExecution = (id: string, signal?: AbortSignal) => request<Execution>(path('executions', id), { signal })
export const listReviews = (signal?: AbortSignal) => request<Review[]>('/reviews', { signal })
export const getReview = (id: string, signal?: AbortSignal) => request<Review>(path('reviews', id), { signal })

/** Editable collections share a create/update/freeze shape; the generic editor uses this map. */
export const editable = {
  publications: { get: getPublication, create: createPublication, update: updatePublication, freeze: freezePublication },
  datasets: { get: getDataset, create: createDataset, update: updateDataset, freeze: freezeDataset },
  benchmarks: { get: getBenchmark, create: createBenchmark, update: updateBenchmark, freeze: freezeBenchmark }
} as const

export type EditableCollection = keyof typeof editable

export const cancelJob = (id: string, revision: number) =>
  request<Job>(path('jobs', id, 'cancel'), { method: 'POST', ifMatch: revision })
export const resumeJob = (id: string, revision: number) =>
  request<Job>(path('jobs', id, 'resume'), { method: 'POST', ifMatch: revision })
export const claimReview = (id: string, revision: number) =>
  request<Review>(path('reviews', id, 'claim'), { method: 'POST', ifMatch: revision })
export const decideReview = (id: string, revision: number, decision: 'Accept' | 'Reject' | 'NeedsEvidence', note: string, datasetDraftId?: string) =>
  request<Review>(path('reviews', id, 'decide'), { method: 'POST', ifMatch: revision, body: { decision, note, ...(datasetDraftId ? { datasetDraftId } : {}) } })

export const listAssessments = (signal?: AbortSignal) => request<Assessment[]>('/assessments', { signal })
export const getAssessment = (id: string, signal?: AbortSignal) => request<Assessment>(path('assessments', id), { signal })
export const listComparisons = (signal?: AbortSignal) => request<Comparison[]>('/comparisons', { signal })
export const getComparison = (id: string, signal?: AbortSignal) => request<Comparison>(path('comparisons', id), { signal })

// The Kubb beta currently renders nullable OpenAPI 3.1 string unions as null-only.
// Preserve the generated required fields while correcting those optional wire fields here.
export type DeterministicInput = Omit<import('../generated/types/DeterministicAssessmentRequest').DeterministicAssessmentRequest, 'assessmentJobId'> & { assessmentJobId?: string }
export type HumanInput = Omit<import('../generated/types/HumanAssessmentRequest').HumanAssessmentRequest, 'assessmentJobId'> & { assessmentJobId?: string }
export type JudgeInput = Omit<import('../generated/types/JudgeEvaluationRequest').JudgeEvaluationRequest, 'assessmentJobId' | 'calibrationId'> & { assessmentJobId?: string; calibrationId?: string }
export type CalibrationInput = import('../generated/types/CalibrationRequest').CalibrationRequest
export type ComparisonInput = import('../generated/types/SaveComparisonRequest').SaveComparisonRequest
export const assessDeterministic = (body: DeterministicInput) => request<Assessment>('/assessments/deterministic', { method: 'POST', body })
export const assessHuman = (body: HumanInput) => request<Assessment>('/assessments/human', { method: 'POST', body })
export const queueJudge = (body: JudgeInput) => request<Job>('/assessments/llm', { method: 'POST', body })
export const createReview = (body: ResourceWrite<JsonObject>) => request<Review>('/reviews', { method: 'POST', body })
export const listCalibrations = (signal?: AbortSignal) => request<Assessment[]>('/calibrations', { signal })
export const getCalibration = (id: string, signal?: AbortSignal) => request<Assessment>(path('calibrations', id), { signal })
export const createCalibration = (body: CalibrationInput) => request<Assessment>('/calibrations', { method: 'POST', body })
export const saveComparison = (body: ComparisonInput) => request<Comparison>('/comparisons', { method: 'POST', body })
export const getComparisonSummary = (id: string, signal?: AbortSignal) => request<import('./types').ComparisonReport>(path('comparisons', id, 'summary'), { signal })
export type DatasetExposure = import('../generated/types/DatasetExposure').DatasetExposure
export const getDatasetExposure = (id: string, signal?: AbortSignal) => request<DatasetExposure>(path('datasets', id, 'exposure'), { signal })
export const revealDatasetCase = (id: string, caseId: string, reason: string) => request<import('./types').DatasetCaseBody>(`${path('datasets', id)}/cases/${encodeURIComponent(caseId)}/reveal`, { method: 'POST', body: { reason } })
export type IncidentCapture = { dataset: Dataset; review: Review; caseId: string; alreadyPresent: boolean }
export const captureIncident = (id: string, body: { datasetDraftId: string; datasetRevision: number; reason: string }) => request<IncidentCapture>(path('executions', id, 'capture-incident'), { method: 'POST', body })
export const acceptIncident = (id: string, revision: number) => request<Review>(path('reviews', id, 'accept-incident'), { method: 'POST', ifMatch: revision })
export const releaseReview = (id: string, revision: number) => request<Review>(path('reviews', id, 'release'), { method: 'POST', ifMatch: revision })
export type PublicationTargets = import('../generated/types/PublicationTargetsView').PublicationTargetsView
export type PublicationDecisionInput = Omit<import('../generated/types/PublicationDecisionRequest').PublicationDecisionRequest, 'evidencePublicationId'> & { evidencePublicationId?: string }
export type PublicationReviewInput = import('../generated/types/PublicationReviewRequest').PublicationReviewRequest
export type PublicationAdoption = import('../generated/types/PublicationAdoption').PublicationAdoption
export const listPublicationTargets = (signal?: AbortSignal) => request<PublicationTargets>('/publication-targets', { signal })
export const listPublicationDecisions = (signal?: AbortSignal) => request<import('./types').ApiResource<JsonObject>[]>('/decisions', { signal })
export const savePublicationDecision = (body: PublicationDecisionInput) => request<import('./types').ApiResource<JsonObject>>('/decisions/publication', { method: 'POST', body })
export const reviewPublication = (target: string, body: PublicationReviewInput) => request<import('./types').ApiResource<JsonObject>>(path('publication-targets', target, 'review'), { method: 'POST', body })
export const listPublicationOperations = (signal?: AbortSignal) => request<import('./types').ApiResource<JsonObject>[]>('/publication-operations', { signal })
export const deliverPublication = (id: string, revision: number) => request<import('./types').ApiResource<JsonObject>>(path('publication-operations', id, 'deliver'), { method: 'POST', ifMatch: revision })
export const reconcilePublication = (id: string, revision: number) => request<import('./types').ApiResource<JsonObject>>(path('publication-operations', id, 'reconcile'), { method: 'POST', ifMatch: revision })
export const getPublicationAdoption = (id: string, signal?: AbortSignal) => request<PublicationAdoption>(path('publication-operations', id, 'adoption'), { signal })
