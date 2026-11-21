<script setup lang="ts">
/**
 * Experiments: backtest and reassess jobs, and the rollout view. Launching always goes through
 * the review page; there is no one-click run.
 */
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import { getWorkspace, listComparisons, listJobs } from '../../api/endpoints'
import type { Job } from '../../api/types'
import { arrayOf } from '../../composables/format'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import ResourceTable from '../../components/ResourceTable.vue'
import RawJson from '../../components/RawJson.vue'
import ComparisonCreate from '../../components/ComparisonCreate.vue'
import ComparisonSummary from '../../components/ComparisonSummary.vue'

const route = useRoute()
const router = useRouter()
const tab = computed({
  get: () => (route.query.tab === 'rollout' ? 'rollout' : route.query.tab === 'compare' ? 'compare' : 'jobs'),
  set: (value: string) => void router.replace({ query: { ...route.query, tab: value } })
})

const selectedJobs = ref<string[]>([])
const jobs = useResource((signal) => listJobs(signal))
const comparisons = useResource((signal) => listComparisons(signal))
const workspace = useResource((signal) => getWorkspace(signal))

const columns: TableColumn<Job>[] = [
  { id: 'kind', header: 'Kind', cell: ({ row }) => row.original.body?.kind ?? 'unknown' },
  { id: 'status', header: 'Status', cell: ({ row }) => row.original.body?.status ?? 'unknown' },
  { id: 'candidates', header: 'Candidates', cell: ({ row }) => arrayOf(row.original.body?.candidates).length },
  { id: 'benchmark', header: 'Benchmark', cell: ({ row }) => row.original.body?.benchmarkId ?? 'unknown' }
]
</script>

<template>
  <PagePanel
    id="experiments"
    title="Experiments"
    icon="i-lucide-flask-conical"
  >
    <template #actions>
      <UButton
        size="sm"
        color="neutral"
        variant="outline"
        icon="i-lucide-refresh-ccw"
        label="Reassess…"
        to="/experiments/launch?kind=Reassess"
      />
      <UButton
        size="sm"
        icon="i-lucide-rocket"
        label="New backtest…"
        to="/experiments/launch?kind=Backtest"
      />
    </template>
    <template #toolbar>
      <UTabs
        v-model="tab"
        :items="[
          { label: 'Backtests & reassessments', value: 'jobs', icon: 'i-lucide-list-checks' },
          { label: 'Compare versions', value: 'compare', icon: 'i-lucide-git-compare' },
          { label: 'Rollout', value: 'rollout', icon: 'i-lucide-radio-tower' }
        ]"
        :content="false"
        variant="link"
        size="sm"
      />
    </template>

    <ApiState
      v-if="tab === 'jobs'"
      :result="jobs.result.value"
      :loading="jobs.loading.value"
      what="jobs"
      @retry="jobs.reload"
    >
      <template #default="{ value }">
        <UEmpty
          v-if="value.length === 0"
          icon="i-lucide-flask-conical"
          title="No backtests yet"
          description="A backtest runs frozen publication variants over a frozen benchmark's dataset and scores them with its criteria. You need a registered build, a publication, a dataset and a frozen benchmark. Saving a job never calls a model; a worker runs it after you approve."
          :actions="[
            { label: 'New backtest…', icon: 'i-lucide-rocket', to: '/experiments/launch?kind=Backtest' },
            { label: 'Datasets & benchmarks', color: 'neutral', variant: 'outline', to: '/datasets' }
          ]"
        />
        <ResourceTable
          v-else
          :rows="value"
          to="/experiments/jobs"
          :extra="columns"
        />
      </template>
    </ApiState>

    <ApiState
      v-else-if="tab === 'compare'"
      :result="jobs.result.value"
      :loading="jobs.loading.value"
      what="jobs"
      @retry="jobs.reload"
    >
      <template #default="{ value }">
        <div class="space-y-3">
          <h2 class="font-semibold">
            Stored comparisons
          </h2>
          <ApiState
            :result="comparisons.result.value"
            :loading="comparisons.loading.value"
            what="comparisons"
            @retry="comparisons.reload"
          >
            <template #default="{ value: stored }">
              <p
                v-if="!stored.length"
                class="text-xs text-muted"
              >
                No stored comparisons in this list.
              </p><ComparisonSummary
                v-for="comparison in stored"
                :id="comparison.id"
                :key="comparison.id"
              />
            </template>
          </ApiState>
          <ComparisonCreate @saved="comparisons.reload" />
          <h2 class="font-semibold">
            Candidate versions
          </h2><p class="text-xs text-muted">
            Choose retained jobs to compare exact candidate pins. Different benchmarks or datasets are separate protocols; scores are not combined or inferred.
          </p>
          <UCheckbox
            v-for="item in value"
            :key="item.id"
            :model-value="selectedJobs.includes(item.id)"
            :label="`${item.name} · ${item.id} · r${item.revision}`"
            @update:model-value="selectedJobs = $event ? [...selectedJobs, item.id] : selectedJobs.filter(id => id !== item.id)"
          />
          <div
            v-for="item in value.filter(job => selectedJobs.includes(job.id))"
            :key="item.id"
            class="space-y-2 rounded border border-default p-3"
          >
            <ULink
              :to="`/experiments/jobs/${encodeURIComponent(item.id)}`"
              class="font-semibold"
            >
              {{ item.name }} · {{ item.id }}
            </ULink>
            <p class="text-xs">
              Benchmark: {{ item.body?.benchmarkId ?? 'unknown' }} · Dataset: {{ item.body?.datasetId ?? 'unknown' }} · State: {{ item.body?.status ?? item.state }}
            </p>
            <UTable
              :data="item.body?.candidates ?? []"
              :columns="[{ accessorKey: 'id', header: 'Candidate' }, { accessorKey: 'name', header: 'Name' }, { accessorKey: 'buildId', header: 'Build' }, { accessorKey: 'publicationId', header: 'Publication' }, { accessorKey: 'commandId', header: 'Command' }, { accessorKey: 'variantId', header: 'Variant' }]"
            />
            <RawJson
              :value="item.body?.items ?? null"
              label="Stored results and assessments"
            /><RawJson
              :value="item"
              label="Exact job JSON"
            />
          </div>
        </div>
      </template>
    </ApiState>
    <div
      v-else
      class="max-w-3xl space-y-3"
    >
      <UAlert
        color="neutral"
        variant="subtle"
        icon="i-lucide-info"
        title="Rollout is not available from the management API yet"
        description="Live A/B weights and shadow sampling come from the publication a runtime target serves. The server has no rollout or adoption endpoint, so this view cannot show which publication production runs. Adoption stays unknown until a host reports it."
      />
      <ApiState
        :result="workspace.result.value"
        :loading="workspace.loading.value"
        what="the workspace"
        @retry="workspace.reload"
      >
        <template #default="{ value }">
          <p class="text-xs">
            <span class="text-muted">Publication targets reported by the server: </span>
            <template v-if="value.capabilities.publicationTargets.length">
              <UBadge
                v-for="target in value.capabilities.publicationTargets"
                :key="target"
                :label="target"
                size="sm"
                variant="outline"
                class="mr-1"
              />
            </template>
            <span v-else>none configured</span>
          </p>
        </template>
      </ApiState>
    </div>
  </PagePanel>
</template>
