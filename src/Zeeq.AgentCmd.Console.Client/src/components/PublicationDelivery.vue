<script setup lang="ts">
/** Reviewed delivery pins a manual decision and target state; actions never provision a target. */
import { computed, ref, shallowRef, watch } from 'vue'
import { useScope } from '../composables/useScope'
import { listPublicationTargets, listPublicationDecisions, listPublicationOperations, savePublicationDecision, reviewPublication, deliverPublication, reconcilePublication, getPublicationAdoption } from '../api/endpoints'
import type { ApiResult } from '../api/client'
import type { ApiResource, PublicationBody } from '../api/types'
import { isRecord } from '../composables/format'
import { useResource } from '../composables/useResource'
import ApiState from './ApiState.vue'
import RawJson from './RawJson.vue'
const props = defineProps<{ publication: ApiResource<PublicationBody> }>()
const targets = useResource((signal) => listPublicationTargets(signal))
const decisions = useResource((signal) => listPublicationDecisions(signal))
const operations = useResource((signal) => listPublicationOperations(signal))
const targetId = ref('')
const action = ref('Activate')
const verdict = ref('Pass')
const comparisonId = ref('')
const candidateId = ref('')
const rationale = ref('')
const evidencePublicationId = ref('')
const decisionId = ref('')
const reviewed = ref(false)
const busy = ref(false)
const outcome = shallowRef<ApiResult<unknown>>()
const adoption = shallowRef<ApiResult<unknown>>()
const scope = useScope()
watch([targetId, decisionId], () => { reviewed.value = false })
watch(() => ({ ...scope }), () => { targetId.value = ''; decisionId.value = ''; outcome.value = undefined; adoption.value = undefined }, { deep: true })
const target = computed(() => targets.result.value?.kind === 'ok' ? targets.result.value.value.targets.find(t => t.profile.id === targetId.value) : undefined)
const available = computed(() => props.publication.state.toLowerCase() === 'frozen' && !!target.value && target.value.profile.buildId === props.publication.body.buildId)
const writerReady = computed(() => available.value && target.value?.capabilities.immutableHistory === true && target.value.capabilities.compareExchange === true)
const manualAllowed = computed(() => available.value && target.value?.profile.allowManualDecisions === true)
const matchingDecisions = computed(() => decisions.result.value?.kind === 'ok' ? decisions.result.value.value.filter(d => d.body.publicationId === props.publication.id && d.body.targetId === targetId.value) : [])
const decision = computed(() => matchingDecisions.value.find(d => d.id === decisionId.value))
const matchingOperations = computed(() => operations.result.value?.kind === 'ok' ? operations.result.value.value.filter(o => o.body.publicationId === props.publication.id && isRecord(o.body.profile) && o.body.profile.id === targetId.value) : [])
async function decide(): Promise<void> {
  if (!manualAllowed.value || !comparisonId.value.trim() || !candidateId.value.trim() || !rationale.value.trim() || busy.value || (action.value === 'RestoreConfiguredDefaults' && !evidencePublicationId.value.trim())) { return }
  busy.value = true
  outcome.value = await savePublicationDecision({ targetId: targetId.value, publicationId: props.publication.id, publicationRevision: props.publication.revision, publicationHash: props.publication.contentHash, buildId: props.publication.body.buildId, action: action.value, verdict: verdict.value, comparisonId: comparisonId.value.trim(), candidateId: candidateId.value.trim(), rationale: rationale.value.trim(), ...(action.value === 'RestoreConfiguredDefaults' ? { evidencePublicationId: evidencePublicationId.value.trim() } : {}) })
  busy.value = false
  if (outcome.value.kind === 'ok') { decisionId.value = (outcome.value.value as ApiResource).id; await decisions.reload() }
}
async function review(): Promise<void> {
  if (!writerReady.value || !decision.value || !reviewed.value || busy.value) { return }
  busy.value = true
  outcome.value = await reviewPublication(targetId.value, { decisionId: decision.value.id, decisionHash: decision.value.contentHash, wholePartitionReplacementReviewed: true })
  busy.value = false
  if (outcome.value.kind === 'ok') { reviewed.value = false; await operations.reload() }
}
async function operate(op: ApiResource, kind: 'deliver' | 'reconcile' | 'adoption'): Promise<void> {
  if (!available.value || (kind !== 'adoption' && !writerReady.value) || busy.value) { return }
  busy.value = true
  if (kind === 'adoption') { adoption.value = await getPublicationAdoption(op.id) }
  else { outcome.value = await (kind === 'deliver' ? deliverPublication : reconcilePublication)(op.id, op.revision); await operations.reload() }
  busy.value = false
}
</script>
<template>
  <div class="space-y-3 rounded border border-default p-3">
    <h2 class="font-semibold">
      Publication delivery
    </h2>
    <p class="text-xs text-muted">
      Only configured targets are shown. Manual decisions record <strong>automatedAcceptance: NotAsserted</strong>; no winner or automatic pass is inferred.
    </p>
    <ApiState
      :result="targets.result.value"
      :loading="targets.loading.value"
      what="publication targets"
      @retry="targets.reload"
    >
      <template #default="{ value }">
        <UAlert
          v-if="!value.targets.length"
          title="No publication target configured"
          description="Delivery remains unavailable until the host configures a target."
          color="neutral"
        />
        <UFormField label="Configured target">
          <USelect
            v-model="targetId"
            :items="value.targets.map(t => ({label:`${t.profile.id} · build ${t.profile.buildId}`, value:t.profile.id}))"
            placeholder="Select target"
            :disabled="!value.targets.length"
          />
        </UFormField>
        <p
          v-if="target"
          class="text-xs"
        >
          Binding {{ target.profile.bindingId }} · revision {{ target.profile.bindingRevision }} · {{ target.capabilities.provider }} · CAS {{ target.capabilities.compareExchange ? 'supported' : 'unavailable' }} · qualified {{ target.capabilities.qualified ? 'yes' : 'no' }}
        </p>
        <p
          v-if="target && !available"
          class="text-xs text-warning"
        >
          A frozen publication and matching target build are required.
        </p>
        <div
          v-if="target"
          class="space-y-2 rounded border border-default p-2"
        >
          <h3 class="font-semibold">
            Manual decision
          </h3><p class="text-xs">
            Publication {{ publication.id }} · r{{ publication.revision }} · hash <span class="font-mono">{{ publication.contentHash }}</span> · build {{ publication.body.buildId }}
          </p><p
            v-if="!target.profile.allowManualDecisions"
            class="text-xs text-warning"
          >
            This target does not permit manual decisions.
          </p>
          <div class="grid gap-2 md:grid-cols-2">
            <UFormField label="Action">
              <USelect
                v-model="action"
                :items="['Activate','ChangeAllocation','ChangeControl','Rollback','RestoreConfiguredDefaults']"
              />
            </UFormField><UFormField label="Manual verdict">
              <USelect
                v-model="verdict"
                :items="['Pass','Fail','Inconclusive']"
              />
            </UFormField><UFormField
              label="Comparison ID"
              required
            >
              <UInput v-model="comparisonId" />
            </UFormField><UFormField
              label="Candidate ID"
              required
            >
              <UInput v-model="candidateId" />
            </UFormField><UFormField
              v-if="action === 'RestoreConfiguredDefaults'"
              label="Evidence publication ID"
              required
            >
              <UInput v-model="evidencePublicationId" />
            </UFormField><UFormField
              label="Rationale"
              required
            >
              <UTextarea v-model="rationale" />
            </UFormField>
          </div>
          <UButton
            label="Save manual decision"
            size="sm"
            :disabled="!manualAllowed || !comparisonId.trim() || !candidateId.trim() || !rationale.trim() || (action === 'RestoreConfiguredDefaults' && !evidencePublicationId.trim())"
            :loading="busy"
            @click="decide"
          />
        </div>
        <ApiState
          v-if="target"
          :result="decisions.result.value"
          :loading="decisions.loading.value"
          what="decisions"
          @retry="decisions.reload"
        >
          <template #default>
            <UFormField label="Exact saved decision">
              <USelect
                v-model="decisionId"
                :items="matchingDecisions.map(d => ({label:`${d.id} · ${d.body.verdict ?? 'unknown'} · ${d.contentHash}`,value:d.id}))"
                placeholder="Select decision"
              />
            </UFormField>
            <UCheckbox
              v-model="reviewed"
              label="I reviewed the whole partition replacement; omitted commands use configured defaults"
            />
            <UButton
              label="Review target and create delivery intent"
              size="sm"
              :disabled="!writerReady || !decision || !reviewed"
              :loading="busy"
              @click="review"
            />
          </template>
        </ApiState>
        <ApiState
          v-if="target"
          :result="operations.result.value"
          :loading="operations.loading.value"
          what="publication operations"
          @retry="operations.reload"
        >
          <template #default>
            <div
              v-for="op in matchingOperations"
              :key="op.id"
              class="space-y-1 rounded border border-default p-2 text-xs"
            >
              <p>{{ op.id }} · r{{ op.revision }} · {{ op.state }}</p><RawJson
                :value="op.body"
                label="Exact target, decision and expected pointer"
              /><div class="flex flex-wrap gap-2">
                <UButton
                  label="Deliver"
                  size="xs"
                  :disabled="!writerReady || op.state !== 'Reviewed'"
                  :loading="busy"
                  @click="operate(op, 'deliver')"
                /><UButton
                  label="Reconcile"
                  size="xs"
                  color="neutral"
                  variant="outline"
                  :disabled="!writerReady || op.state === 'Reviewed'"
                  :loading="busy"
                  @click="operate(op, 'reconcile')"
                /><UButton
                  label="Read adoption"
                  size="xs"
                  color="neutral"
                  variant="outline"
                  :disabled="!available"
                  :loading="busy"
                  @click="operate(op, 'adoption')"
                />
              </div>
            </div>
          </template>
        </ApiState>
      </template>
    </ApiState>
    <ApiState
      v-if="outcome && outcome.kind !== 'ok'"
      :result="outcome"
      what="publication action"
      retry-label="Dismiss"
      @retry="outcome = undefined"
    /><RawJson
      v-if="outcome?.kind === 'ok'"
      :value="outcome.value"
      label="Server receipt"
      open
    />
    <ApiState
      v-if="adoption && adoption.kind !== 'ok'"
      :result="adoption"
      what="adoption"
      retry-label="Dismiss"
      @retry="adoption = undefined"
    /><RawJson
      v-if="adoption?.kind === 'ok'"
      :value="adoption.value"
      label="Reporting-host adoption (not fleet-wide)"
      open
    />
  </div>
</template>
