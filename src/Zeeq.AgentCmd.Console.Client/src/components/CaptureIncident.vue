<script setup lang="ts">
/** Capture pins the selected draft revision and creates an owned review without a reference answer. */
import { computed, ref, shallowRef } from 'vue'
import { captureIncident, listDatasets } from '../api/endpoints'
import type { IncidentCapture } from '../api/endpoints'
import type { ApiResult } from '../api/client'
import { useResource } from '../composables/useResource'
import ApiState from './ApiState.vue'
const props = defineProps<{ executionId: string }>()
const datasets = useResource((signal) => listDatasets(signal))
const datasetId = ref('')
const reason = ref('')
const busy = ref(false)
const result = shallowRef<ApiResult<IncidentCapture>>()
const drafts = computed(() => datasets.result.value?.kind === 'ok' ? datasets.result.value.value.filter(d => d.state.toLowerCase() === 'draft' && d.body?.purpose === 'Regression') : [])
const selected = computed(() => drafts.value.find(d => d.id === datasetId.value))
async function submit(): Promise<void> {
  if (!selected.value || !reason.value.trim() || busy.value) { return }
  busy.value = true
  result.value = await captureIncident(props.executionId, { datasetDraftId: selected.value.id, datasetRevision: selected.value.revision, reason: reason.value.trim() })
  busy.value = false
  if (result.value.kind === 'ok') { await datasets.reload() }
}
</script>
<template>
  <div class="space-y-2 rounded border border-default p-3">
    <h2 class="font-semibold">
      Add to regression draft
    </h2>
    <p class="text-xs text-muted">
      Capture retains the source input and creates an owned review. It does not create a reference answer. Missing inputs or unrevealed holdouts block capture.
    </p>
    <ApiState
      :result="datasets.result.value"
      :loading="datasets.loading.value"
      what="regression drafts"
      @retry="datasets.reload"
    >
      <template #default>
        <p
          v-if="!drafts.length"
          class="text-xs text-muted"
        >
          No regression drafts in the returned list.
        </p>
        <UFormField
          label="Regression draft"
          required
        >
          <USelect
            v-model="datasetId"
            :items="drafts.map(d => ({label: `${d.name} · ${d.id} · r${d.revision}`, value:d.id}))"
            placeholder="Select draft"
            class="w-full"
          />
        </UFormField>
        <UFormField
          label="Incident reason"
          required
        >
          <UTextarea
            v-model="reason"
            class="w-full"
          />
        </UFormField>
        <UButton
          label="Add to regression draft"
          size="sm"
          :disabled="!selected || !reason.trim()"
          :loading="busy"
          @click="submit"
        />
      </template>
    </ApiState>
    <ApiState
      v-if="result && result.kind !== 'ok'"
      :result="result"
      what="incident capture"
      retry-label="Dismiss"
      @retry="result = undefined"
    />
    <div
      v-if="result?.kind === 'ok'"
      class="text-xs"
    >
      <p>{{ result.value.alreadyPresent ? 'Existing source case reused.' : 'Incident case captured.' }} Case <span class="font-mono">{{ result.value.caseId }}</span>. No reference answer was created.</p><ULink :to="`/datasets/${encodeURIComponent(result.value.dataset.id)}`">
        Open draft
      </ULink> · <ULink :to="`/review/${encodeURIComponent(result.value.review.id)}`">
        Open review
      </ULink>
    </div>
  </div>
</template>
