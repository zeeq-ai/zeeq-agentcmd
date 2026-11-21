<script setup lang="ts">
/** Datasets (frozen inputs with optional references) and the benchmarks that grade outputs on them. */
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import { listBenchmarks, listDatasets } from '../../api/endpoints'
import type { Benchmark, Dataset } from '../../api/types'
import { arrayOf } from '../../composables/format'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import ResourceTable from '../../components/ResourceTable.vue'

const route = useRoute()
const router = useRouter()
const tab = computed({
  get: () => (route.query.tab === 'benchmarks' ? 'benchmarks' : 'datasets'),
  set: (value: string) => void router.replace({ query: { ...route.query, tab: value } })
})

const datasets = useResource((signal) => listDatasets(signal))
const benchmarks = useResource((signal) => listBenchmarks(signal))

const datasetColumns: TableColumn<Dataset>[] = [
  { id: 'purpose', header: 'Purpose', cell: ({ row }) => row.original.body?.purpose ?? 'unknown' },
  { id: 'cases', header: 'Cases', cell: ({ row }) => arrayOf(row.original.body?.cases).length },
  {
    id: 'references',
    header: 'With reference',
    cell: ({ row }) => {
      const cases = arrayOf<{ holdoutWithheld?: boolean; reference?: { completeness?: string } }>(row.original.body?.cases)
      return (row.original.body?.purpose === 'Acceptance' && cases.some(item => item.holdoutWithheld !== false)) ? 'withheld' : cases.filter(item => item.reference && item.reference.completeness !== 'None').length
    }
  }
]
const benchmarkColumns: TableColumn<Benchmark>[] = [
  { id: 'dataset', header: 'Dataset', cell: ({ row }) => row.original.body?.datasetId ?? 'unknown' },
  { id: 'criteria', header: 'Criteria', cell: ({ row }) => arrayOf(row.original.body?.criteria).length },
  { id: 'primary', header: 'Primary', cell: ({ row }) => row.original.body?.primaryCriterionId ?? 'unknown' }
]
</script>

<template>
  <PagePanel
    id="datasets"
    title="Datasets"
    icon="i-lucide-database"
  >
    <template #actions>
      <UButton
        v-if="tab === 'datasets'"
        size="sm"
        icon="i-lucide-file-plus"
        label="New draft dataset"
        to="/datasets/new"
      />
      <UButton
        v-else
        size="sm"
        icon="i-lucide-file-plus"
        label="New draft benchmark"
        to="/benchmarks/new"
      />
    </template>
    <template #toolbar>
      <UTabs
        v-model="tab"
        :items="[
          { label: 'Datasets', value: 'datasets', icon: 'i-lucide-database' },
          { label: 'Benchmarks', value: 'benchmarks', icon: 'i-lucide-ruler' }
        ]"
        :content="false"
        variant="link"
        size="sm"
      />
    </template>

    <ApiState
      v-if="tab === 'datasets'"
      :result="datasets.result.value"
      :loading="datasets.loading.value"
      what="datasets"
      @retry="datasets.reload"
    >
      <template #default="{ value }">
        <UEmpty
          v-if="value.length === 0"
          icon="i-lucide-database"
          title="No datasets in this scope"
          description="A dataset holds case inputs, optional references with their completeness, and tags. Create a draft, add cases as JSON, then freeze it so a benchmark can depend on it."
          :actions="[{ label: 'New draft dataset', icon: 'i-lucide-file-plus', to: '/datasets/new' }]"
        />
        <ResourceTable
          v-else
          :rows="value"
          to="/datasets"
          :extra="datasetColumns"
        />
      </template>
    </ApiState>

    <ApiState
      v-else
      :result="benchmarks.result.value"
      :loading="benchmarks.loading.value"
      what="benchmarks"
      @retry="benchmarks.reload"
    >
      <template #default="{ value }">
        <UEmpty
          v-if="value.length === 0"
          icon="i-lucide-ruler"
          title="No benchmarks in this scope"
          description="A benchmark names a frozen dataset, explicit criteria with output/reference/source bindings, and exactly one primary criterion. Nothing is scored by default: you choose each scorer."
          :actions="[{ label: 'New draft benchmark', icon: 'i-lucide-file-plus', to: '/benchmarks/new' }]"
        />
        <ResourceTable
          v-else
          :rows="value"
          to="/benchmarks"
          :extra="benchmarkColumns"
        />
      </template>
    </ApiState>
  </PagePanel>
</template>
