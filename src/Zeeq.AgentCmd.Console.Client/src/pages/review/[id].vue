<script setup lang="ts">
import { computed, ref, shallowRef } from 'vue'
import type { ApiResult } from '../../api/client'
import { useRoute } from 'vue-router'
import { claimReview, decideReview, getReview, getExecution, getAssessment, acceptIncident, releaseReview, getWorkspace } from '../../api/endpoints'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import RawJson from '../../components/RawJson.vue'
import ResourceMeta from '../../components/ResourceMeta.vue'
import SemanticFindings from '../../components/SemanticFindings.vue'
const route = useRoute()
const id = computed(() => String((route.params as { id: string }).id))
const review = useResource((signal) => getReview(id.value, signal), [id])
const workspace = useResource((signal) => getWorkspace(signal))
const assessmentId = computed(() => review.result.value?.kind === 'ok' && typeof review.result.value.value.body.assessmentId === 'string' ? review.result.value.value.body.assessmentId : '')
const assessments = useResource((signal) => assessmentId.value ? getAssessment(assessmentId.value, signal) : Promise.resolve({ kind: 'notFound' as const }), [assessmentId])
const exactAssessment = computed(() => assessments.result.value?.kind === 'ok' ? assessments.result.value.value : undefined)
const executionId = computed(() => {
  const reviewExecution = review.result.value?.kind === 'ok' ? review.result.value.value.body.executionId : undefined
  const assessmentExecution = exactAssessment.value?.body.executionId
  return typeof reviewExecution === 'string' ? reviewExecution : typeof assessmentExecution === 'string' ? assessmentExecution : ''
})
const executions = useResource((signal) => executionId.value ? getExecution(executionId.value, signal) : Promise.resolve({ kind: 'notFound' as const }), [executionId])
const exactExecution = computed(() => executions.result.value?.kind === 'ok' ? executions.result.value.value : undefined)
const note = ref('')
const datasetDraftId = ref('')
const decision = ref<'Accept' | 'Reject' | 'NeedsEvidence'>('NeedsEvidence')
const failure = shallowRef<ApiResult<unknown>>()
const busy = ref(false)
async function act(action: 'claim' | 'decide' | 'acceptIncident' | 'release'): Promise<void> {
  const current = review.result.value
  if (!current || current.kind !== 'ok' || busy.value) { return }
  busy.value = true
  const result = action === 'claim' ? await claimReview(id.value, current.value.revision)
    : action === 'release' ? await releaseReview(id.value, current.value.revision)
    : action === 'acceptIncident' ? await acceptIncident(id.value, current.value.revision)
    : await decideReview(id.value, current.value.revision, decision.value, note.value, datasetDraftId.value.trim() || undefined)
  busy.value = false
  if (result.kind === 'ok') { failure.value = undefined; await review.reload() }
  else { failure.value = result }
}
</script>
<template>
  <PagePanel
    id="review-detail"
    title="Review evidence"
    icon="i-lucide-clipboard-check"
  >
    <template #leading>
      <UButton
        to="/review"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Back to reviews"
      />
    </template>
    <ApiState
      v-if="failure && failure.kind !== 'ok'"
      :result="failure"
      what="review action"
      @retry="review.reload"
    />
    <ApiState
      :result="review.result.value"
      :loading="review.loading.value"
      what="review"
      @retry="review.reload"
    >
      <template #default="{ value }">
        <div class="space-y-3">
          <ResourceMeta :resource="value" /><UAlert
            v-if="value.body.holdoutWithheld === true"
            color="warning"
            title="Holdout review withheld"
            description="Reveal the relevant acceptance case before inspecting or acting on its evidence."
          /><UTabs
            v-if="value.body.holdoutWithheld !== true"
            :items="[{ label: 'Evidence', slot: 'evidence' as const }, { label: 'Exact JSON', slot: 'json' as const }]"
            variant="link"
          >
            <template #evidence>
              <div class="space-y-2 text-xs">
                <p>Kind: {{ value.body.kind ?? 'unknown' }} · Status: {{ value.body.status ?? value.state }}</p><p>Execution: {{ value.body.executionId ?? 'unknown' }} · Assessment: {{ value.body.assessmentId ?? 'unknown' }} · Criterion: {{ value.body.criterionId ?? 'unknown' }}</p><p>Source locator and proposed value are shown as recorded; absence is unknown.</p><RawJson
                  :value="value.body.sourceLocator ?? null"
                  label="Source locator"
                  open
                /><RawJson
                  :value="value.body.proposedValue ?? null"
                  label="Proposed value"
                /><ApiState
                  v-if="executionId"
                  :result="executions.result.value"
                  :loading="executions.loading.value"
                  what="execution evidence"
                  @retry="executions.reload"
                >
                  <template #default>
                    <RawJson
                      :value="exactExecution?.body.outputRef ?? null"
                      label="Exact stored output reference"
                      open
                    /><RawJson
                      :value="exactExecution?.body.value ?? null"
                      label="Stored output"
                    />
                  </template>
                </ApiState><ApiState
                  v-if="assessmentId"
                  :result="assessments.result.value"
                  :loading="assessments.loading.value"
                  what="assessment evidence"
                  @retry="assessments.reload"
                >
                  <template #default>
                    <UAlert
                      v-if="exactAssessment?.body.holdoutWithheld === true"
                      color="warning"
                      title="Holdout assessment withheld"
                      description="Reveal the relevant acceptance case before inspecting evidence."
                    />
                    <UAlert
                      v-if="exactAssessment?.body.referenceDisputed === true"
                      color="warning"
                      title="Reference completeness disputed"
                      description="The assessment is unreconciled; no score or pass is inferred."
                    /><RawJson
                      :value="exactAssessment?.body.evaluator ?? null"
                      label="Evaluator provenance"
                    /><RawJson
                      :value="exactAssessment?.body.sourceCoverage ?? null"
                      label="Source coverage"
                    /><SemanticFindings
                      v-if="exactAssessment?.body.holdoutWithheld !== true"
                      :findings="exactAssessment?.body.findings"
                    />
                  </template>
                </ApiState>
              </div>
            </template>
            <template #json>
              <RawJson
                :value="value"
                label="Exact review envelope"
                open
              /><RawJson
                :value="exactExecution ?? null"
                label="Exact execution envelope"
              /><RawJson
                :value="exactAssessment ?? null"
                label="Exact assessment envelope"
              />
            </template>
          </UTabs>
          <div
            v-if="value.body.holdoutWithheld !== true"
            class="max-w-xl space-y-2"
          >
            <UButton
              label="Claim review"
              size="sm"
              :loading="busy"
              @click="act('claim')"
            />
            <template v-if="value.body.kind === 'Incident'">
              <p class="text-xs text-muted">
                Claim before accepting. Acceptance adds no reference answer; only the claim owner can accept or release.
              </p>
              <UButton
                label="Accept incident"
                size="sm"
                :loading="busy"
                :disabled="value.state !== 'Claimed' || workspace.result.value?.kind !== 'ok' || value.body.assignee !== workspace.result.value.value.identity.subject"
                @click="act('acceptIncident')"
              />
              <UButton
                label="Release claim"
                size="sm"
                color="neutral"
                variant="outline"
                :loading="busy"
                :disabled="value.state !== 'Claimed' || workspace.result.value?.kind !== 'ok' || value.body.assignee !== workspace.result.value.value.identity.subject"
                @click="act('release')"
              />
            </template>
            <template v-else>
              <UFormField label="Decision">
                <USelect
                  v-model="decision"
                  :items="['Accept', 'Reject', 'NeedsEvidence']"
                />
              </UFormField>
              <UFormField label="Dataset draft ID (for accepting a reference)">
                <UInput
                  v-model="datasetDraftId"
                  class="w-full"
                />
              </UFormField>
              <UFormField label="Note">
                <UTextarea
                  v-model="note"
                  class="w-full"
                />
              </UFormField>
              <UButton
                label="Submit decision"
                size="sm"
                :loading="busy"
                @click="act('decide')"
              />
            </template>
          </div>
        </div>
      </template>
    </ApiState>
  </PagePanel>
</template>
