<script setup lang="ts">
/** Exposure state is tracked separately from the frozen content revision. */
import { computed } from 'vue'
import { getDatasetExposure } from '../api/endpoints'
import { useResource } from '../composables/useResource'
import ApiState from './ApiState.vue'
const props = defineProps<{ id: string; revision: number }>()
const datasetId = computed(() => props.id)
const revision = computed(() => props.revision)
const exposure = useResource((signal) => getDatasetExposure(datasetId.value, signal), [datasetId, revision])
defineExpose({ reload: exposure.reload })
</script>
<template>
  <ApiState
    :result="exposure.result.value"
    :loading="exposure.loading.value"
    what="exposure status"
    @retry="exposure.reload"
  >
    <template #default="{ value }">
      <UBadge
        :color="value.independentAcceptanceEligible ? 'success' : 'warning'"
        variant="subtle"
        :label="value.independentAcceptanceEligible ? 'Independent acceptance eligible' : 'Independent acceptance not eligible'"
      /><p class="text-xs text-muted">
        Exposed cases: {{ value.exposedCaseIds.length }}<span
          v-if="value.exposedCaseIds.length"
          class="font-mono"
        > · {{ value.exposedCaseIds.join(', ') }}</span>
      </p>
    </template>
  </ApiState>
</template>
