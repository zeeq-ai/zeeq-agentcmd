/**
 * The single network boundary of the console.
 *
 * Every call to the management API goes through `request`, so scope headers, credentials and
 * failure classification stay consistent. The console never stores secrets: the server session
 * cookie or a trusted reverse proxy authenticates each call (`credentials: 'include'`).
 *
 * `request` never throws for HTTP or network failures. It returns a closed `ApiResult` so each
 * screen must render authentication, availability and conflict failures as failures, not as
 * empty data.
 */

/** Scope sent with every call. An unset member is omitted; the server decides its default. */
export interface ApiScope {
  application?: string
  environment?: string
  partition?: string
}

import type { ApiProblem } from './types'

/** RFC 9457 problem details. The server keeps SQL and other sensitive detail out of them. */
export type ProblemDetails = ApiProblem

/** Closed outcome of one API call. */
export type ApiResult<TValue> =
  | { kind: 'ok'; value: TValue; revision?: number; listLimit?: number }
  | { kind: 'unauthenticated'; problem?: ProblemDetails }
  | { kind: 'forbidden'; problem?: ProblemDetails }
  | { kind: 'notFound'; problem?: ProblemDetails }
  | { kind: 'conflict'; problem?: ProblemDetails; currentRevision?: number }
  | { kind: 'validation'; status: number; problem?: ProblemDetails }
  | { kind: 'unavailable'; status?: number; message: string; problem?: ProblemDetails }

/** The failure members of `ApiResult`. */
export type ApiFailure = Exclude<ApiResult<unknown>, { kind: 'ok' }>

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
  body?: unknown
  query?: Record<string, string | number | undefined>
  /** Optimistic concurrency: the resource revision the edit is based on. Sent as `If-Match: "<n>"`. */
  ifMatch?: number
  signal?: AbortSignal
}

/** Management API root. Same-origin by default so the session cookie applies. */
export const apiBase: string = (import.meta.env.VITE_AGENTCMD_API_BASE ?? '/api/v1').replace(/\/+$/, '')

let currentScope: ApiScope = {}

/** Sets the scope for later calls. Only `useScope` calls this. */
export function setApiScope(scope: ApiScope): void {
  currentScope = { ...scope }
}

/** Returns a copy of the scope that later calls send. */
export function getApiScope(): ApiScope {
  return { ...currentScope }
}

function buildUrl(path: string, query?: RequestOptions['query']): string {
  const url = `${apiBase}${path.startsWith('/') ? path : `/${path}`}`
  if (!query) {
    return url
  }

  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== '') {
      params.set(key, String(value))
    }
  }

  const text = params.toString()
  return text ? `${url}?${text}` : url
}

function scopeHeaders(scope: ApiScope): Record<string, string> {
  const headers: Record<string, string> = {}
  if (scope.application) {
    headers['X-AgentCmd-Application'] = scope.application
  }
  if (scope.environment) {
    headers['X-AgentCmd-Environment'] = scope.environment
  }
  if (scope.partition) {
    headers['X-AgentCmd-Partition'] = scope.partition
  }
  return headers
}

function parseEtag(etag: string | undefined): number | undefined {
  const match = etag?.match(/^(?:W\/)?"(\d+)"$/)
  return match ? Number(match[1]) : undefined
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text) {
    return undefined
  }

  try {
    return JSON.parse(text) as unknown
  } catch {
    return text
  }
}

function asProblem(body: unknown): ProblemDetails | undefined {
  if (body && typeof body === 'object' && !Array.isArray(body)) {
    return body as ProblemDetails
  }
  if (typeof body === 'string' && body.trim()) {
    return { detail: body.slice(0, 2000) }
  }
  return undefined
}

/** Sends one request and classifies the outcome. Aborts propagate as `unavailable`. */
export async function request<TValue>(path: string, options: RequestOptions = {}): Promise<ApiResult<TValue>> {
  const headers: Record<string, string> = {
    Accept: 'application/json, application/problem+json',
    ...scopeHeaders(currentScope)
  }
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  if (options.ifMatch !== undefined) {
    headers['If-Match'] = `"${options.ifMatch}"`
  }

  let response: Response
  try {
    response = await fetch(buildUrl(path, options.query), {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: 'include',
      signal: options.signal
    })
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    return { kind: 'unavailable', message: `Network error: ${message}` }
  }

  const body = await readBody(response)
  const etag = response.headers.get('ETag') ?? undefined

  if (response.ok) {
    const limitHeader = response.headers.get('X-AgentCmd-List-Limit')
    const limit = limitHeader ? Number(limitHeader) : undefined
    return { kind: 'ok', value: body as TValue, revision: parseEtag(etag), listLimit: limit && Number.isFinite(limit) ? limit : undefined }
  }

  const problem = asProblem(body)
  switch (response.status) {
    case 401:
      return { kind: 'unauthenticated', problem }
    case 403:
      return { kind: 'forbidden', problem }
    case 404:
      return { kind: 'notFound', problem }
    case 409:
    case 412:
    case 428:
      return {
        kind: 'conflict',
        problem,
        currentRevision: problem?.revision ?? parseEtag(etag)
      }
    case 400:
    case 422:
      return { kind: 'validation', status: response.status, problem }
    default:
      return {
        kind: 'unavailable',
        status: response.status,
        message: problem?.title ?? `HTTP ${response.status} ${response.statusText}`.trim(),
        problem
      }
  }
}

/** Short, exact label for a failure. */
export function describeFailure(failure: ApiFailure): string {
  switch (failure.kind) {
    case 'unauthenticated':
      return 'Not signed in (401)'
    case 'forbidden':
      return 'Not permitted in this scope (403)'
    case 'notFound':
      return 'Not found (404)'
    case 'conflict':
      return 'Revision conflict: the resource changed since you loaded it'
    case 'validation':
      return `Rejected by the server (${failure.status})`
    case 'unavailable':
      return failure.status ? `Server unavailable (${failure.status})` : 'Management API unreachable'
  }
}
