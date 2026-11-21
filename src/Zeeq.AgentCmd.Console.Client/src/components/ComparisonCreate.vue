<script setup lang="ts">
/** Saves only explicitly selected assessment identities; no score is computed in the browser. */
import { computed, ref, shallowRef } from 'vue'
import { listAssessments, saveComparison } from '../api/endpoints'
import type { ApiResult } from '../api/client'
import { useResource } from '../composables/useResource'
import ApiState from './ApiState.vue'
import RawJson from './RawJson.vue'
const emit = defineEmits<{ saved: [] }>()
const assessments = useResource((signal) => listAssessments(signal))
const selected = ref<string[]>([])
const baseline = ref('')
const busy = ref(false)
const result = shallowRef<ApiResult<unknown>>()
const candidates = computed(() => assessments.result.value?.kind === 'ok' ? [...new Set(assessments.result.value.value.filter(a => selected.value.includes(a.id)).map(a => a.body.candidateId).filter((v): v is string => typeof v === 'string' && !!v))] : [])
async function submit(): Promise<void> {
  if (!selected.value.length || !candidates.value.includes(baseline.value) || busy.value) { return }
  busy.value = true
  result.value = await saveComparison({ assessmentIds: selected.value, baselineCandidateId: baseline.value })
  busy.value = false
  if (result.value.kind === 'ok') { emit('saved') }
}
</script>
<template>
  <div class="space-y-2 rounded border border-default p-3">
    <h2 class="font-semibold">
      Save comparison
    </h2>
    <p class="text-xs text-muted">
      Select exact retained assessments and an explicit baseline. The server checks that they share a frozen protocol.
    </p>
    <ApiState
      :result="assessments.result.value"
      :loading="assessments.loading.value"
      what="assessments"
      @retry="assessments.reload"
    >
      <template #default="{ value }">
        <p
          v-if="!value.length"
          class="text-xs text-muted"
        >
          No assessments in this list.
        </p>
        <div class="max-h-56 space-y-1 overflow-auto">
          <UCheckbox
            v-for="a in value"
            :key="a.id"
            :model-value="selected.includes(a.id)"
            :label="`${a.id} · ${a.body.candidateId ?? 'candidate unknown'} · ${a.body.criterionId ?? 'criterion unknown'} · ${a.body.outcome ?? a.state}${a.body.holdoutWithheld === true ? ' · holdout evidence withheld; reveal needed' : ''}`"
            @update:model-value="selected = $event ? [...selected, a.id] : selected.filter(id => id !== a.id)"
          />
        </div>
        <UFormField
          label="Baseline candidate"
          required
        >
          <USelect
            v-model="baseline"
            :items="candidates"
            placeholder="Select baseline"
          />
        </UFormField>
        <UButton
          label="Save comparison"
          size="sm"
          :loading="busy"
          :disabled="!selected.length || !candidates.includes(baseline)"
          @click="submit"
        />
      </template>
    </ApiState>
    <ApiState
      v-if="result && result.kind !== 'ok'"
      :result="result"
      what="comparison"
      retry-label="Dismiss"
      @retry="result = undefined"
    /><RawJson
      v-if="result?.kind === 'ok'"
      :value="result.value"
      label="Stored comparison"
      open
    />
  </div>
</template>
