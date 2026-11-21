import { reactive, watch } from 'vue'
import type { Router } from 'vue-router'
import { setApiScope, type ApiScope } from '../api/client'

/**
 * The scope (application / environment / partition) that every API call sends as headers.
 *
 * The default `local/dev/default` matches the server's explicit loopback-only Local mode. It is a
 * convenience for that mode, not an authentication mechanism: the server still decides access.
 * The scope lives in memory and the URL query (so a link reproduces the view); nothing is
 * written to localStorage.
 */
export const defaultScope: Required<ApiScope> = {
  application: 'local',
  environment: 'dev',
  partition: 'default'
}

const scope = reactive<Required<ApiScope>>({ ...defaultScope })
setApiScope(scope)

watch(
  scope,
  (value) => {
    setApiScope(value)
  },
  // Synchronous so headers change before any loader watcher refetches.
  { deep: true, flush: 'sync' }
)

/** Returns the shared, reactive scope. */
export function useScope() {
  return scope
}

/** Reads a scope from route query members, falling back to the current values. */
export function scopeFromQuery(query: Record<string, unknown>): Required<ApiScope> {
  const pick = (key: keyof ApiScope): string => {
    const value = query[key]
    return typeof value === 'string' && value.trim() ? value.trim() : scope[key]
  }
  return { application: pick('application'), environment: pick('environment'), partition: pick('partition') }
}

function isDefault(value: Required<ApiScope>): boolean {
  return (Object.keys(defaultScope) as (keyof ApiScope)[]).every((key) => value[key] === defaultScope[key])
}

/**
 * Keeps the scope in the URL query. The first navigation adopts a scope from its query; later
 * navigations carry a non-default scope forward, so links and reloads keep the same headers.
 */
export function installScopeSync(router: Router): void {
  let adopted = false
  router.beforeEach((to) => {
    if (!adopted) {
      adopted = true
      Object.assign(scope, scopeFromQuery(to.query))
    }
    if (isDefault(scope) || to.query.partition) {
      return true
    }
    return { ...to, query: { ...to.query, ...scope } }
  })
}

/** Applies a new scope and records it in the current URL. */
export async function applyScope(router: Router, next: Required<ApiScope>): Promise<void> {
  Object.assign(scope, next)
  const current = router.currentRoute.value
  const query = { ...current.query }
  for (const key of Object.keys(defaultScope)) {
    delete query[key]
  }
  await router.replace({ path: current.path, query: isDefault(next) ? query : { ...query, ...next } })
}
