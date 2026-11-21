<script setup lang="ts">
/** Read-only report uses the server's exact protocol, coverage and equal-case reduction. */
import { computed, ref, shallowRef } from 'vue'
import { getComparisonSummary } from '../api/endpoints'
import type { ApiResult } from '../api/client'
import type { ComparisonReport } from '../api/types'
import ApiState from './ApiState.vue'
import RawJson from './RawJson.vue'
import IdentityChip from './IdentityChip.vue'
const props = defineProps<{ id: string }>()
const result = shallowRef<ApiResult<ComparisonReport>>()
const loading = ref(false)
const sortedCriteria = computed(() => {
  const report = result.value?.kind === 'ok' ? result.value.value : undefined
  return [...(report?.criteria ?? [])].sort((a, b) => Number(b.criterionId === report?.primaryCriterionId) - Number(a.criterionId === report?.primaryCriterionId))
})
function hasPrimary(report: ComparisonReport): boolean { return !!report.primaryCriterionId && report.criteria.some(criterion => criterion.criterionId === report.primaryCriterionId) }
function candidateLabel(report: ComparisonReport, key: string): string {
  const candidate = report.candidates.find(item => item.key === key)
  return candidate ? `${candidate.candidateId} · ${candidate.variantId}` : 'candidate unknown'
}
async function load(): Promise<void> { loading.value = true; result.value = await getComparisonSummary(props.id); loading.value = false }
</script>
<template>
  <div class="space-y-2 rounded border border-default p-2">
    <UButton
      :label="`Open comparison ${id}`"
      size="xs"
      variant="outline"
      color="neutral"
      :loading="loading"
      @click="load"
    />
    <ApiState
      v-if="result"
      :result="result"
      :loading="loading"
      what="comparison summary"
      @retry="load"
    >
      <template #default="{ value }">
        <div class="space-y-3 text-xs">
          <p>
            Dataset hash <IdentityChip
              :value="value.datasetHash"
              label="Dataset hash"
            /> · Benchmark hash <IdentityChip
              :value="value.benchmarkHash"
              label="Benchmark hash"
            />
          </p>
          <p>
            Baseline: {{ value.baselineCandidateId }} <span
              v-if="value.baselineAmbiguous"
              class="text-warning"
            >— ambiguous across exact configurations</span>
          </p>
          <p v-if="value.independentAcceptanceEligible !== null && value.independentAcceptanceEligible !== undefined">
            Current independent acceptance: {{ value.independentAcceptanceEligible ? 'eligible' : 'not eligible (exposure recorded)' }}. Historical scores are unchanged.
          </p>
          <UAlert
            v-if="value.holdoutWithheld"
            color="warning"
            title="Some holdout evidence is withheld"
            description="Reveal the relevant acceptance cases to inspect per-case evidence. Aggregate metrics remain as returned by the server."
          />
          <p class="text-muted">
            Candidates are those represented by the selected assessments. Scores average complete repeats within each case, then weight cases equally. Matched means use only common scored cases. No winner is inferred.
          </p>
          <UTable
            :data="value.candidates"
            :columns="[{accessorKey:'candidateId',header:'Candidate'}, {accessorKey:'key',header:'Exact key'}, {accessorKey:'buildId',header:'Build'}, {accessorKey:'buildHash',header:'Build hash'}, {accessorKey:'publicationId',header:'Publication'}, {accessorKey:'publicationHash',header:'Publication hash'}, {accessorKey:'commandId',header:'Command'}, {accessorKey:'variantId',header:'Variant'}]"
          >
            <template #key-cell="{ row }">
              <IdentityChip
                :value="row.original.key"
                label="Candidate key"
                :head="8"
              />
            </template>
            <template #buildId-cell="{ row }">
              <IdentityChip
                :value="row.original.buildId"
                label="Build ID"
                :head="8"
              />
            </template>
            <template #buildHash-cell="{ row }">
              <IdentityChip
                :value="row.original.buildHash"
                label="Build hash"
                :head="8"
              />
            </template>
            <template #publicationId-cell="{ row }">
              <IdentityChip
                :value="row.original.publicationId"
                label="Publication ID"
                :head="8"
              />
            </template>
            <template #publicationHash-cell="{ row }">
              <IdentityChip
                :value="row.original.publicationHash"
                label="Publication hash"
                :head="8"
              />
            </template>
          </UTable>
          <p
            v-if="!hasPrimary(value)"
            class="text-warning"
          >
            Primary criterion is unavailable in this report.
          </p>
          <details
            v-for="criterion in sortedCriteria"
            :key="criterion.criterionId"
            class="space-y-1"
            :open="!hasPrimary(value) || criterion.criterionId === value.primaryCriterionId"
          >
            <summary class="cursor-pointer font-semibold">
              {{ criterion.criterionId }} · v{{ criterion.criterionVersion }} <UBadge
                v-if="criterion.criterionId === value.primaryCriterionId"
                label="Primary"
                size="sm"
              />
            </summary><p>{{ value.holdoutWithheld ? 'Visible common scored cases' : 'Common scored cases' }}: {{ criterion.commonCaseIds.length }} <span class="font-mono">{{ criterion.commonCaseIds.join(', ') }}</span></p><UTable
              :data="criterion.candidates"
              :columns="[{accessorKey:'candidateKey',header:'Candidate · exact key'}, {accessorKey:'expectedCases',header:'Expected'}, {accessorKey:'eligibleCases',header:'Eligible'}, {accessorKey:'scoredCases',header:'Scored'}, {accessorKey:'missingCases',header:'Missing'}, {accessorKey:'unreconciledCases',header:'Unreconciled'}, {accessorKey:'mean',header:'Mean'}, {accessorKey:'matchedMean',header:'Matched mean'}]"
            >
              <template #candidateKey-cell="{ row }">
                <span>{{ candidateLabel(value, row.original.candidateKey) }}</span>
                <IdentityChip
                  :value="row.original.candidateKey"
                  label="Candidate key"
                  :head="8"
                />
              </template>
              <template #mean-cell="{row}">
                {{ row.original.mean ?? 'unknown' }}
              </template><template #matchedMean-cell="{row}">
                {{ row.original.matchedMean ?? 'unknown' }}
              </template>
            </UTable><RawJson
              :value="criterion.candidates.map(c => ({ candidateKey: c.candidateKey, cases: c.cases }))"
              label="Case outcomes and assessment IDs"
            />
          </details>
          <div v-if="value.guardrails.length">
            <h3 class="font-semibold">
              Guardrails
            </h3><p
              v-for="rule in value.guardrails"
              :key="rule.id"
            >
              {{ rule.id }} · {{ rule.status }}
            </p>
          </div><p
            v-else
            class="text-muted"
          >
            No guardrails reported.
          </p>
          <RawJson
            :value="value"
            label="Exact summary JSON"
          />
        </div>
      </template>
    </ApiState>
  </div>
</template>
