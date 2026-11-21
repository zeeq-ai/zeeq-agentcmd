<script setup lang="ts">
/** Resource or job state. Unrecognized states render verbatim in neutral, never mapped to success. */
import { computed } from 'vue'

const props = defineProps<{ state: string | undefined | null }>()

type BadgeColor = 'success' | 'warning' | 'error' | 'info' | 'neutral' | 'primary'

const color = computed<BadgeColor>(() => {
  const value = (props.state ?? '').toLowerCase()
  if (['frozen', 'published', 'completed', 'succeeded', 'success', 'done'].includes(value)) {
    return 'success'
  }
  if (['draft', 'queued', 'pending', 'open'].includes(value)) {
    return 'neutral'
  }
  if (['running', 'claimed', 'active'].includes(value)) {
    return 'info'
  }
  if (['blocked', 'held', 'paused', 'superseded', 'spenduncertain'].includes(value)) {
    return 'warning'
  }
  if (['failed', 'failure', 'cancelled', 'canceled', 'rejected'].includes(value)) {
    return 'error'
  }
  return 'neutral'
})
</script>

<template>
  <UBadge
    v-if="state"
    :label="state"
    :color="color"
    variant="subtle"
    size="sm"
  />
  <span
    v-else
    class="value-unknown"
  >unknown</span>
</template>
