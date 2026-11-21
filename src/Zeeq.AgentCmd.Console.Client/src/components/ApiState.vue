<script setup lang="ts" generic="TValue">
/**
 * Renders a closed `ApiResult`: a skeleton on first load, an explicit failure panel for every
 * failure kind, and the default slot only for `ok`. Screens must not render failures as empty data.
 */
import { computed } from 'vue'
import { describeFailure, type ApiFailure, type ApiResult } from '../api/client'

const props = defineProps<{
  result: ApiResult<TValue> | undefined
  loading?: boolean
  /** Label of what failed to load, such as "builds". */
  what?: string
  retryLabel?: string
}>()

const emit = defineEmits<{ retry: [] }>()

defineSlots<{ default(props: { value: TValue }): unknown }>()

const failure = computed<ApiFailure | undefined>(() =>
  props.result && props.result.kind !== 'ok' ? props.result : undefined
)

const hint = computed(() => {
  switch (failure.value?.kind) {
    case 'unauthenticated':
      return 'Sign in through the server session or the reverse proxy, then retry. The console holds no credentials.'
    case 'forbidden':
      return 'Your identity has no permission in this application / environment / partition.'
    case 'unavailable':
      return 'The management API did not answer. Check that it runs and that the proxy points to it.'
    default:
      return undefined
  }
})
</script>

<template>
  <div
    v-if="!result && loading"
    class="space-y-1.5"
  >
    <USkeleton class="h-6 w-full" />
    <USkeleton class="h-6 w-full" />
    <USkeleton class="h-6 w-2/3" />
  </div>
  <UAlert
    v-else-if="failure"
    color="error"
    variant="subtle"
    icon="i-lucide-circle-alert"
    :title="`${what ? `Could not load ${what}: ` : ''}${describeFailure(failure)}`"
    :actions="[{ label: retryLabel ?? 'Retry', icon: 'i-lucide-rotate-ccw', color: 'neutral', variant: 'outline', size: 'xs', loading, onClick: () => emit('retry') }]"
  >
    <template #description>
      <div class="space-y-1 text-xs">
        <p v-if="failure.problem?.title || failure.problem?.detail">
          <span
            v-if="failure.problem?.code"
            class="font-mono"
          >{{ failure.problem.code }} · </span>
          {{ failure.problem?.detail ?? failure.problem?.title }}
        </p>
        <p
          v-if="failure.kind === 'unavailable'"
          class="font-mono"
        >
          {{ failure.message }}
        </p>
        <ul
          v-if="failure.problem?.errors"
          class="list-disc pl-4"
        >
          <li
            v-for="(messages, field) in failure.problem.errors"
            :key="field"
          >
            <span class="font-mono">{{ field }}</span>: {{ messages.join('; ') }}
          </li>
        </ul>
        <p v-if="failure.kind === 'conflict'">
          Server revision: {{ failure.currentRevision === undefined ? 'unknown' : `r${failure.currentRevision}` }}. Reload before retrying; local inputs are retained.
        </p>
        <p
          v-if="hint"
          class="text-muted"
        >
          {{ hint }}
        </p>
      </div>
    </template>
  </UAlert>
  <template v-else-if="result?.kind === 'ok'">
    <UAlert
      v-if="result.listLimit && Array.isArray(result.value) && result.value.length >= result.listLimit"
      color="warning"
      variant="subtle"
      icon="i-lucide-list-filter"
      :title="`${what ? `${what}: ` : ''}List limited to ${result.listLimit} records`"
      description="Results and counts may be incomplete. Exact IDs can still be read directly."
      class="mb-2"
    />
    <slot :value="result.value" />
  </template>
</template>
