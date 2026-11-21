import { onBeforeUnmount, ref, shallowRef, watch, type Ref } from 'vue'
import type { ApiResult } from '../api/client'
import { useScope } from './useScope'

/**
 * Loads one API result and reloads it when the scope changes.
 *
 * The state is the closed `ApiResult` itself, never a default value: a screen renders the failure
 * kind instead of treating a 401 or an outage as an empty list. A newer load aborts an older one
 * so a slow response cannot overwrite a newer scope.
 */
export function useResource<TValue>(
  loader: (signal: AbortSignal) => Promise<ApiResult<TValue>>,
  dependencies: Ref<unknown>[] = []
) {
  const result = shallowRef<ApiResult<TValue>>()
  const loading = ref(false)
  let controller: AbortController | undefined

  async function reload(): Promise<void> {
    controller?.abort()
    const current = new AbortController()
    controller = current
    loading.value = true

    const next = await loader(current.signal)
    if (current.signal.aborted) {
      return
    }

    result.value = next
    loading.value = false
  }

  const scope = useScope()
  // Never show the previous identity or scope while a new exact resource is loading.
  watch([() => ({ ...scope }), ...dependencies], () => { result.value = undefined; void reload() }, { immediate: true, deep: true })

  onBeforeUnmount(() => {
    controller?.abort()
  })

  return { result, loading, reload }
}
