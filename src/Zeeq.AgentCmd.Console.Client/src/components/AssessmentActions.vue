<script setup lang="ts">
/** Explicit assessment and review actions on the exact retained execution. */
import { computed, ref, shallowRef } from 'vue'
import { assessDeterministic, assessHuman, createReview, queueJudge, getWorkspace } from '../api/endpoints'
import type { ApiResult } from '../api/client'
import { useResource } from '../composables/useResource'
import ApiState from './ApiState.vue'
import RawJson from './RawJson.vue'
const props = defineProps<{ executionId: string }>()
const workspace = useResource((signal) => getWorkspace(signal))
const criterion = ref('')
const assessmentJobId = ref('')
const calibrationId = ref('')
const score = ref<number | undefined>()
const rationale = ref('')
const approved = ref(false)
const reviewKind = ref<'Grading' | 'ReferenceGap' | 'JudgeAudit'>('Grading')
const reviewName = ref('')
const busy = ref(false)
const result = shallowRef<ApiResult<unknown>>()
const scoreValid = computed(() => typeof score.value === 'number' && Number.isFinite(score.value) && score.value >= 0 && score.value <= 1)
async function submit(action: 'deterministic' | 'human' | 'judge' | 'review'): Promise<void> {
  if (busy.value || (action !== 'review' && !criterion.value.trim())) { return }
  if (action === 'human' && (!scoreValid.value || !rationale.value.trim())) { return }
  if (action === 'judge' && !approved.value) { return }
  if (action === 'review' && !reviewName.value.trim()) { return }
  const base = { executionId: props.executionId, criterionId: criterion.value.trim(), ...(assessmentJobId.value.trim() ? { assessmentJobId: assessmentJobId.value.trim() } : {}) }
  busy.value = true
  if (action === 'deterministic') { result.value = await assessDeterministic(base) }
  else if (action === 'human') { result.value = await assessHuman({ ...base, score: score.value!, rationale: rationale.value.trim() }) }
  else if (action === 'judge') { result.value = await queueJudge({ ...base, executionApproved: true, ...(calibrationId.value.trim() ? { calibrationId: calibrationId.value.trim() } : {}) }); approved.value = false }
  else { result.value = await createReview({ name: reviewName.value.trim(), body: { kind: reviewKind.value, executionId: props.executionId, ...(criterion.value.trim() ? { criterionId: criterion.value.trim() } : {}) } }) }
  busy.value = false
}
</script>
<template>
  <div class="space-y-3 rounded border border-default p-3">
    <h2 class="font-semibold">
      Assess exact execution
    </h2>
    <p class="text-xs text-muted">
      The server validates the frozen criterion and stored output. A judge request queues work; it can incur model charges when a worker runs it.
    </p>
    <div class="grid gap-2 md:grid-cols-2">
      <UFormField
        label="Criterion ID"
        required
      >
        <UInput
          v-model="criterion"
          class="w-full"
        />
      </UFormField>
      <UFormField label="Reassessment job ID (optional)">
        <UInput
          v-model="assessmentJobId"
          class="w-full"
        />
      </UFormField>
    </div>
    <UTabs
      :items="[{label:'Deterministic',slot:'deterministic' as const},{label:'Human',slot:'human' as const},{label:'LLM judge',slot:'judge' as const},{label:'Create review',slot:'review' as const}]"
      variant="link"
    >
      <template #deterministic>
        <div class="space-y-2">
          <p class="text-xs">
            Uses the pinned Exact or Numeric scorer on the stored output.
          </p><UButton
            label="Assess deterministically"
            :disabled="!criterion.trim()"
            :loading="busy"
            size="sm"
            @click="submit('deterministic')"
          />
        </div>
      </template>
      <template #human>
        <div class="space-y-2">
          <UFormField
            label="Score (0–1)"
            required
          >
            <UInput
              v-model.number="score"
              type="number"
              min="0"
              max="1"
              step="any"
            />
          </UFormField><UFormField
            label="Rationale"
            required
          >
            <UTextarea
              v-model="rationale"
              class="w-full"
            />
          </UFormField><UButton
            label="Record human assessment"
            :disabled="!criterion.trim() || !scoreValid || !rationale.trim()"
            :loading="busy"
            size="sm"
            @click="submit('human')"
          />
        </div>
      </template>
      <template #judge>
        <div class="space-y-2">
          <UFormField label="Calibration ID (if required)">
            <UInput
              v-model="calibrationId"
              class="w-full"
            />
          </UFormField><p
            v-if="workspace.result.value?.kind === 'ok' && !workspace.result.value.value.activeWorkers.length"
            class="text-xs text-warning"
          >
            No active worker is reported. The request can remain queued or blocked until a suitable worker runs.
          </p>
          <p
            v-if="workspace.result.value && workspace.result.value.kind !== 'ok'"
            class="text-xs text-warning"
          >
            Worker availability is unknown.
          </p>
          <p class="text-xs text-warning">
            Cost is unknown. This queues evaluator work; a configured worker may make a paid call.
          </p><UCheckbox
            v-model="approved"
            label="I approve paid judge execution for this output and criterion"
          /><UButton
            label="Queue judge evaluation"
            :disabled="!criterion.trim() || !approved"
            :loading="busy"
            size="sm"
            @click="submit('judge')"
          />
        </div>
      </template>
      <template #review>
        <div class="space-y-2">
          <UFormField
            label="Review name"
            required
          >
            <UInput
              v-model="reviewName"
              class="w-full"
            />
          </UFormField><UFormField label="Review kind">
            <USelect
              v-model="reviewKind"
              :items="['Grading','ReferenceGap','JudgeAudit']"
            />
          </UFormField><UButton
            label="Create review"
            :disabled="!reviewName.trim()"
            :loading="busy"
            size="sm"
            @click="submit('review')"
          />
        </div>
      </template>
    </UTabs>
    <ApiState
      v-if="result && result.kind !== 'ok'"
      :result="result"
      what="assessment action"
      retry-label="Dismiss"
      @retry="result = undefined"
    />
    <RawJson
      v-if="result?.kind === 'ok'"
      :value="result.value"
      label="Server receipt"
      open
    />
  </div>
</template>
