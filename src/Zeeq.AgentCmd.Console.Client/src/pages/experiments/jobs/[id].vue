<script setup lang="ts">
/**
 * One job: status, counts, candidates and items as the server stores them. Counts render exactly
 * as reported; a missing count is unknown, not zero. Cancel/resume actions arrive with server
 * support; until then they are shown disabled with the reason.
 */
import { computed, ref, shallowRef } from 'vue'
import type { ApiResult } from '../../../api/client'
import { useRoute } from 'vue-router'
import { useIntervalFn } from '@vueuse/core'
import { cancelJob, resumeJob, getJob, getWorkspace } from '../../../api/endpoints'
import { useResource } from '../../../composables/useResource'
import ApiState from '../../../components/ApiState.vue'
import JobEvidence from '../../../components/JobEvidence.vue'
import IdentityChip from '../../../components/IdentityChip.vue'
import PagePanel from '../../../components/PagePanel.vue'
import RawJson from '../../../components/RawJson.vue'
import ResourceMeta from '../../../components/ResourceMeta.vue'
import StateBadge from '../../../components/StateBadge.vue'

const route = useRoute('/experiments/jobs/[id]')
const id = computed(() => route.params.id)
const job = useResource((signal) => getJob(id.value, signal), [id])
const workspace = useResource((signal) => getWorkspace(signal))

const value = computed(() => (job.result.value?.kind === 'ok' ? job.result.value.value : undefined))
const status = computed(() => String(value.value?.body?.status ?? value.value?.state ?? ''))
const finished = computed(() => ['completed', 'succeeded', 'failed', 'cancelled', 'canceled'].includes(status.value.toLowerCase()))

useIntervalFn(() => {
  if (value.value && !finished.value) {
    void job.reload()
  }
}, 5000)

const noWorker = computed(() => {
  const info = workspace.result.value?.kind === 'ok' ? workspace.result.value.value : undefined
  return !!info && info.activeWorkers.length === 0 && !finished.value
})

const actionFailure = shallowRef<ApiResult<unknown>>()
const actionBusy = ref(false)
async function changeJob(action: 'cancel' | 'resume'): Promise<void> {
  if (!value.value || actionBusy.value) { return }
  actionBusy.value = true
  const result = await (action === 'cancel' ? cancelJob : resumeJob)(id.value, value.value.revision)
  actionBusy.value = false
  if (result.kind === 'ok') {
    actionFailure.value = undefined
    await job.reload()
  } else { actionFailure.value = result }
}

const total = computed(() => {
  const counts = value.value?.body?.counts
  if (!counts) {
    return undefined
  }
  return counts.total ?? Object.values(counts).reduce((sum, count) => sum + (typeof count === 'number' ? count : 0), 0)
})
const done = computed(() => {
  const counts = value.value?.body?.counts
  return counts ? (counts.completed ?? counts.succeeded ?? undefined) : undefined
})
</script>

<template>
  <PagePanel
    id="job"
    :title="value?.name ?? `Job ${id}`"
    icon="i-lucide-list-checks"
  >
    <template #leading>
      <UButton
        to="/experiments"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Back to experiments"
      />
    </template>
    <template #actions>
      <UButton
        v-if="value && !finished"
        size="sm"
        color="neutral"
        variant="outline"
        label="Cancel"
        :loading="actionBusy"
        @click="changeJob('cancel')"
      />
      <UButton
        v-if="value && ['cancelled', 'canceled', 'blocked'].includes(status.toLowerCase())"
        size="sm"
        color="neutral"
        variant="outline"
        label="Resume"
        :loading="actionBusy"
        @click="changeJob('resume')"
      />
      <UTooltip text="Re-score this job's stored outputs; opens the review page">
        <UButton
          v-if="value?.body?.kind === 'Backtest'"
          size="sm"
          color="neutral"
          variant="outline"
          icon="i-lucide-refresh-ccw"
          label="Reassess…"
          :to="{ path: '/experiments/launch', query: { kind: 'Reassess', sourceJobId: id } }"
        />
      </UTooltip>
    </template>

    <ApiState
      v-if="actionFailure && actionFailure.kind !== 'ok'"
      :result="actionFailure"
      what="job action"
      @retry="job.reload"
    />
    <ApiState
      :result="job.result.value"
      :loading="job.loading.value"
      what="the job"
      @retry="job.reload"
    >
      <template #default="{ value: current }">
        <div class="grid gap-3 lg:grid-cols-[1fr_20rem]">
          <div class="min-w-0 space-y-3">
            <div class="flex flex-wrap items-center gap-2">
              <UBadge
                :label="current.body?.kind ?? 'kind unknown'"
                variant="outline"
                color="neutral"
              />
              <StateBadge :state="status" />
              <UBadge
                v-if="current.body?.executionApproved"
                label="Execution approved"
                icon="i-lucide-shield-check"
                color="success"
                variant="subtle"
                size="sm"
              />
            </div>

            <UAlert
              v-if="noWorker"
              color="warning"
              variant="subtle"
              icon="i-lucide-hourglass"
              title="No active worker"
              description="The job stays queued or blocked until a typed worker that hosts these commands runs. Nothing has been sent to a model by this console."
            />

            <div>
              <UProgress
                v-if="total && done !== undefined"
                :model-value="done"
                :max="total"
                size="sm"
              />
              <div class="mt-1 flex flex-wrap gap-3 text-xs">
                <template v-if="current.body?.counts">
                  <span
                    v-for="(count, key) in current.body.counts"
                    :key="key"
                  ><span class="text-muted">{{ key }}</span> {{ count }}</span>
                </template>
                <span
                  v-else
                  class="value-unknown"
                >Counts not reported yet</span>
              </div>
            </div>

            <UTable
              v-if="!current.body?.holdoutWithheld"
              :data="current.body?.candidates ?? []"
              :columns="[
                { accessorKey: 'id', header: 'Candidate' },
                { accessorKey: 'name', header: 'Name' },
                { accessorKey: 'commandId', header: 'Command' },
                { accessorKey: 'variantId', header: 'Variant' },
                { accessorKey: 'publicationId', header: 'Publication' },
                { accessorKey: 'buildId', header: 'Build' }
              ]"
              empty="No candidates recorded."
            >
              <template #publicationId-cell="{ row }">
                <IdentityChip
                  :value="row.original.publicationId"
                  label="Publication"
                />
              </template>
              <template #buildId-cell="{ row }">
                <IdentityChip
                  :value="row.original.buildId"
                  label="Build"
                />
              </template>
            </UTable>

            <UAlert
              v-if="current.body?.holdoutWithheld"
              color="warning"
              title="Holdout evidence withheld"
              description="Reveal relevant acceptance cases to inspect their evidence. No assessment is started automatically."
            />
            <JobEvidence
              v-else
              :job="current"
            />
            <p class="text-xs text-muted">
              Spend: <span class="value-unknown">unknown</span>. The job body reports no usage or price yet; unknown is never shown as zero.
            </p>
          </div>
          <div class="space-y-3">
            <UCard :ui="{ body: 'p-3 sm:p-3' }">
              <ResourceMeta :resource="current" />
            </UCard>
            <UCard :ui="{ body: 'p-3 sm:p-3 text-xs space-y-1' }">
              <p>
                <span class="text-muted">Benchmark</span> <IdentityChip
                  :value="current.body?.benchmarkId"
                  label="Benchmark"
                />
              </p>
              <p>
                <span class="text-muted">Dataset</span> <IdentityChip
                  :value="current.body?.datasetId"
                  label="Dataset"
                />
              </p>
              <p v-if="current.body?.sourceJobId">
                <span class="text-muted">Source job</span>
                <ULink
                  :to="`/experiments/jobs/${encodeURIComponent(current.body.sourceJobId)}`"
                  class="font-mono"
                >
                  {{ current.body.sourceJobId }}
                </ULink>
              </p>
            </UCard>
            <RawJson
              :value="current"
              label="Stored envelope"
            />
          </div>
        </div>
      </template>
    </ApiState>
  </PagePanel>
</template>
