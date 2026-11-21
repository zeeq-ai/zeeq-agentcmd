<script setup lang="ts">
/** Calibration admission uses exact human/judge pairs and server-owned outcomes. */
import { ref, shallowRef } from 'vue'
import { createCalibration, listCalibrations, getCalibration } from '../../api/endpoints'
import type { ApiResult } from '../../api/client'
import type { Assessment } from '../../api/types'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import RawJson from '../../components/RawJson.vue'
const list = useResource((signal) => listCalibrations(signal))
const selected = shallowRef<ApiResult<Assessment>>()
const result = shallowRef<ApiResult<Assessment>>()
const judgeConfigurationId = ref('')
const criterionId = ref('')
const maximumScoreDifference = ref(0.1)
const minimumAgreement = ref(0.8)
const pairs = ref([{ humanAssessmentId: '', judgeAssessmentId: '' }])
const busy = ref(false)
async function read(id: string): Promise<void> { selected.value = await getCalibration(id) }
async function submit(): Promise<void> {
  if (busy.value || !judgeConfigurationId.value.trim() || !criterionId.value.trim() || !pairs.value.length || pairs.value.some(p => !p.humanAssessmentId.trim() || !p.judgeAssessmentId.trim()) || !Number.isFinite(maximumScoreDifference.value) || !Number.isFinite(minimumAgreement.value) || maximumScoreDifference.value < 0 || maximumScoreDifference.value > 1 || minimumAgreement.value < 0 || minimumAgreement.value > 1) { return }
  busy.value = true
  result.value = await createCalibration({ judgeConfigurationId: judgeConfigurationId.value.trim(), criterionId: criterionId.value.trim(), pairs: pairs.value.map(p => ({ humanAssessmentId: p.humanAssessmentId.trim(), judgeAssessmentId: p.judgeAssessmentId.trim() })), maximumScoreDifference: maximumScoreDifference.value, minimumAgreement: minimumAgreement.value })
  busy.value = false
  if (result.value.kind === 'ok') { await list.reload() }
}
</script>
<template>
  <PagePanel
    id="calibrations"
    title="Judge calibration"
    icon="i-lucide-scale"
  >
    <template #leading>
      <UButton
        to="/review"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Back to review"
      />
    </template>
    <p class="text-xs text-muted">
      Calibration compares retained human and judge assessments on the same cases. It does not run a model.
    </p>
    <ApiState
      :result="list.result.value"
      :loading="list.loading.value"
      what="calibrations"
      @retry="list.reload"
    >
      <template #default="{ value }">
        <p
          v-if="!value.length"
          class="text-xs text-muted"
        >
          No calibrations in this list.
        </p><div
          v-for="item in value"
          :key="item.id"
          class="flex items-center gap-2 text-xs"
        >
          <UButton
            :label="item.name"
            variant="link"
            size="xs"
            @click="read(item.id)"
          /><span class="font-mono">{{ item.id }} · r{{ item.revision }} · {{ item.state }}</span>
        </div>
      </template>
    </ApiState>
    <ApiState
      v-if="selected"
      :result="selected"
      what="calibration"
      retry-label="Dismiss"
      @retry="selected = undefined"
    >
      <template #default="{ value }">
        <RawJson
          :value="value"
          label="Exact calibration record"
          open
        />
      </template>
    </ApiState>
    <div class="space-y-2 rounded border border-default p-3">
      <h2 class="font-semibold">
        Evaluate calibration
      </h2>
      <div class="grid gap-2 md:grid-cols-2">
        <UFormField
          label="Judge configuration ID"
          required
        >
          <UInput
            v-model="judgeConfigurationId"
            class="w-full"
          />
        </UFormField><UFormField
          label="Criterion ID"
          required
        >
          <UInput
            v-model="criterionId"
            class="w-full"
          />
        </UFormField><UFormField label="Maximum score difference (0–1)">
          <UInput
            v-model.number="maximumScoreDifference"
            type="number"
            min="0"
            max="1"
            step="any"
          />
        </UFormField><UFormField label="Minimum agreement (0–1)">
          <UInput
            v-model.number="minimumAgreement"
            type="number"
            min="0"
            max="1"
            step="any"
          />
        </UFormField>
      </div>
      <div
        v-for="(pair, index) in pairs"
        :key="index"
        class="flex flex-wrap items-end gap-2"
      >
        <UFormField label="Human assessment ID">
          <UInput v-model="pair.humanAssessmentId" />
        </UFormField><UFormField label="Judge assessment ID">
          <UInput v-model="pair.judgeAssessmentId" />
        </UFormField><UButton
          label="Remove"
          size="xs"
          color="neutral"
          variant="ghost"
          @click="pairs.splice(index, 1)"
        />
      </div>
      <UButton
        label="Add pair"
        size="xs"
        color="neutral"
        variant="outline"
        @click="pairs.push({ humanAssessmentId: '', judgeAssessmentId: '' })"
      /><UButton
        label="Evaluate calibration"
        size="sm"
        :loading="busy"
        :disabled="!judgeConfigurationId.trim() || !criterionId.trim() || !pairs.length || pairs.some(p => !p.humanAssessmentId.trim() || !p.judgeAssessmentId.trim()) || !Number.isFinite(maximumScoreDifference) || maximumScoreDifference < 0 || maximumScoreDifference > 1 || !Number.isFinite(minimumAgreement) || minimumAgreement < 0 || minimumAgreement > 1"
        @click="submit"
      />
      <ApiState
        v-if="result && result.kind !== 'ok'"
        :result="result"
        what="calibration"
        retry-label="Dismiss"
        @retry="result = undefined"
      /><RawJson
        v-if="result?.kind === 'ok'"
        :value="result.value"
        label="Calibration result"
        open
      />
    </div>
  </PagePanel>
</template>
