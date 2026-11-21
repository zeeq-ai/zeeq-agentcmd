<script setup lang="ts">
/**
 * One benchmark: the dataset it grades on and its explicit criteria. Exactly one criterion is
 * Primary; Llm criteria name a judge configuration that stays separate from any candidate.
 */
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import type { BenchmarkBody } from '../../api/types'
import EditableResourcePage from '../../components/EditableResourcePage.vue'
import IdentityChip from '../../components/IdentityChip.vue'
import RawJson from '../../components/RawJson.vue'

const route = useRoute('/benchmarks/[id]')
const id = computed(() => route.params.id)
const body = (value: unknown) => value as BenchmarkBody | undefined
</script>

<template>
  <EditableResourcePage
    :id="id"
    collection="benchmarks"
    back="/datasets?tab=benchmarks"
    noun="benchmark"
    icon="i-lucide-ruler"
  >
    <template #summary="{ resource }">
      <div class="flex flex-wrap items-center gap-2 text-xs">
        <span class="text-muted">Dataset</span>
        <ULink
          v-if="body(resource.body)?.datasetId"
          :to="`/datasets/${encodeURIComponent(body(resource.body)!.datasetId)}`"
          class="font-mono"
        >
          {{ body(resource.body)?.datasetId }}
        </ULink>
        <span
          v-else
          class="value-unknown"
        >unknown</span>
        <span class="text-muted">· repeats</span>
        <span>{{ body(resource.body)?.repeats ?? 'unknown' }}</span>
      </div>
      <UTable
        :data="body(resource.body)?.criteria ?? []"
        :columns="[
          { accessorKey: 'name', header: 'Criterion' },
          { id: 'role', header: 'Role' },
          { accessorKey: 'scope', header: 'Scope' },
          { id: 'bindings', header: 'Bindings' },
          { id: 'scorer', header: 'Scorer' }
        ]"
        empty="No criteria. A benchmark needs at least one, and exactly one primary."
      >
        <template #name-cell="{ row }">
          <span class="font-medium">{{ row.original.name }}</span>
          <span class="ml-1 font-mono text-xs text-muted">{{ row.original.id }} v{{ row.original.version }}</span>
        </template>
        <template #role-cell="{ row }">
          <UBadge
            v-if="row.original.id === body(resource.body)?.primaryCriterionId"
            label="Primary"
            color="primary"
            size="sm"
          />
          <span
            v-else
            class="text-xs text-muted"
          >Secondary</span>
        </template>
        <template #bindings-cell="{ row }">
          <div class="space-y-0.5 font-mono text-xs">
            <div><span class="text-muted">output</span> {{ row.original.outputBinding === '' ? '"" (whole)' : row.original.outputBinding }}</div>
            <div v-if="row.original.referenceBinding !== undefined">
              <span class="text-muted">reference</span> {{ row.original.referenceBinding || '""' }}
            </div>
            <div v-if="row.original.sourceBinding !== undefined">
              <span class="text-muted">source</span> {{ row.original.sourceBinding || '""' }}
            </div>
          </div>
        </template>
        <template #scorer-cell="{ row }">
          <span class="text-xs">
            {{ row.original.scorer?.kind }} v{{ row.original.scorer?.version }}
            <template v-if="row.original.scorer?.tolerance !== undefined"> ± {{ row.original.scorer.tolerance }}</template>
          </span>
          <span
            v-if="row.original.scorer?.kind === 'Llm'"
            class="ml-1 text-xs"
          >
            judge <IdentityChip
              :value="row.original.scorer.judgeConfigId"
              label="Judge config"
            />
          </span>
        </template>
      </UTable>
      <RawJson
        :value="body(resource.body)?.guardrails"
        :label="`Guardrails (${body(resource.body)?.guardrails?.length ?? 0})`"
      />
    </template>
  </EditableResourcePage>
</template>
