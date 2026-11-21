<script setup lang="ts">
/** Show only evidence that explicitly names this job and candidate; missing linkage stays unknown. */
import { computed, ref } from 'vue'
import { listAssessments, listExecutions, listReviews } from '../api/endpoints'
import type { Job, JsonObject, JsonValue } from '../api/types'
import { useResource } from '../composables/useResource'
import ApiState from './ApiState.vue'
import RawJson from './RawJson.vue'
import SemanticFindings from './SemanticFindings.vue'
const props = defineProps<{ job: Job }>()
const executions = useResource((signal) => listExecutions(signal))
const assessmentList = useResource((signal) => listAssessments(signal))
const reviews = useResource((signal) => listReviews(signal))
const selected = ref('')
const candidates = computed(() => props.job.body?.candidates ?? [])
const candidateId = computed(() => selected.value || candidates.value[0]?.id || '')
function matches(body: JsonObject): boolean { return body.jobId === props.job.id && body.candidateId === candidateId.value }
const executionRecords = computed(() => executions.result.value?.kind === 'ok' ? executions.result.value.value.filter(e => matches(e.body)) : [])
const reviewRecords = computed(() => reviews.result.value?.kind === 'ok' ? reviews.result.value.value.filter(e => executionRecords.value.some(execution => typeof execution.body.executionId === 'string' && e.body.executionId === execution.body.executionId)) : [])
const assessments = computed(() => assessmentList.result.value?.kind === 'ok' ? assessmentList.result.value.value.filter(a => matches(a.body) || (a.body.candidateId === candidateId.value && executionRecords.value.some(e => typeof e.body.executionId === 'string' && e.body.executionId === a.body.executionId))) : [])
function evidence(body: JsonObject): JsonValue { return body.findings ?? body.evidence ?? body.assessment ?? null }
</script>
<template>
  <div class="space-y-3">
    <p class="text-xs text-muted">
      Compare the exact candidate pins below. Evidence is linked only when a stored record explicitly names this job and candidate; missing evidence is unknown.
    </p>
    <UTable
      :data="candidates"
      :columns="[{ accessorKey: 'id', header: 'Candidate' }, { accessorKey: 'name', header: 'Name' }, { accessorKey: 'buildId', header: 'Build' }, { accessorKey: 'publicationId', header: 'Publication' }, { accessorKey: 'commandId', header: 'Command' }, { accessorKey: 'variantId', header: 'Variant' }]"
    />
    <UFormField label="Evidence for candidate">
      <USelect
        v-model="selected"
        :items="candidates.map(c => ({ label: `${c.name} (${c.id})`, value: c.id }))"
        :placeholder="candidateId || 'No candidates'"
      />
    </UFormField>
    <ApiState
      :result="executions.result.value"
      :loading="executions.loading.value"
      what="executions"
      @retry="executions.reload"
    >
      <template #default>
        <ApiState
          :result="assessmentList.result.value"
          :loading="assessmentList.loading.value"
          what="assessments"
          @retry="assessmentList.reload"
        >
          <template #default>
            <ApiState
              :result="reviews.result.value"
              :loading="reviews.loading.value"
              what="reviews"
              @retry="reviews.reload"
            >
              <template #default>
                <p
                  v-if="!executionRecords.length && !reviewRecords.length && !assessments.length"
                  class="text-xs text-muted"
                >
                  No explicitly linked execution or review evidence found.
                </p>
                <UTabs
                  v-else
                  :items="[{ label: 'Findings and evidence', slot: 'evidence' as const }, { label: 'Exact JSON', slot: 'json' as const }]"
                  variant="link"
                >
                  <template #evidence>
                    <div class="space-y-2">
                      <div
                        v-for="(assessment, index) in assessments"
                        :key="index"
                      >
                        <p class="text-xs font-mono">
                          Criterion {{ assessment.body.criterionId }} · {{ assessment.body.outcome ?? 'unknown' }} · score {{ assessment.body.referenceDisputed === true ? 'unreconciled' : assessment.body.score ?? 'unknown' }}
                        </p><UAlert
                          v-if="assessment.body.holdoutWithheld === true"
                          color="warning"
                          title="Holdout assessment withheld"
                          description="Reveal the relevant acceptance case before inspecting evidence."
                        /><UAlert
                          v-if="assessment.body.referenceDisputed === true"
                          color="warning"
                          title="Reference completeness disputed"
                          description="The assessment is unreconciled; no score or pass is inferred."
                        /><RawJson
                          :value="assessment.body.evaluator ?? null"
                          label="Evaluator provenance"
                        /><RawJson
                          :value="assessment.body.sourceCoverage ?? null"
                          label="Source coverage"
                        /><SemanticFindings
                          v-if="assessment.body.holdoutWithheld !== true"
                          :findings="assessment.body.findings"
                        />
                      </div><div
                        v-for="record in [...executionRecords, ...reviewRecords]"
                        :key="record.id"
                      >
                        <p class="text-xs font-mono">
                          {{ record.kind }} · {{ record.id }} · r{{ record.revision }}
                        </p><RawJson
                          :value="evidence(record.body)"
                          label="Findings / evidence (null means not recorded)"
                          open
                        />
                      </div>
                    </div>
                  </template>
                  <template #json>
                    <div class="space-y-2">
                      <RawJson
                        v-for="(assessment, index) in assessments"
                        :key="`assessment-${index}`"
                        :value="assessment"
                        label="Exact assessment"
                      /><RawJson
                        v-for="record in [...executionRecords, ...reviewRecords]"
                        :key="record.id"
                        :value="record"
                        :label="`${record.kind} ${record.id}`"
                      />
                    </div>
                  </template>
                </UTabs>
              </template>
            </ApiState>
          </template>
        </ApiState>
      </template>
    </ApiState>
    <RawJson
      :value="job.body?.items"
      label="Exact job items"
    />
  </div>
</template>
