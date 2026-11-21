<script setup lang="ts" generic="TResource extends ApiResource<any>">
/**
 * Compact table of stored resources. The envelope columns (name, exact ID, state, revision,
 * updated) are always present; pages add body columns. A row click opens the detail route.
 */
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import type { ApiResource } from '../api/types'
import { formatTime } from '../composables/format'
import IdentityChip from './IdentityChip.vue'
import StateBadge from './StateBadge.vue'

const props = defineProps<{
  rows: TResource[]
  /** Detail route prefix, such as "/datasets". */
  to?: string
  extra?: TableColumn<TResource>[]
  empty?: string
}>()

const router = useRouter()

const columns = computed<TableColumn<TResource>[]>(() => [
  { accessorKey: 'name', header: 'Name' },
  { accessorKey: 'id', header: 'ID' },
  { accessorKey: 'state', header: 'State' },
  ...(props.extra ?? []),
  { accessorKey: 'revision', header: 'Rev', cell: ({ row }) => `r${row.original.revision}` },
  { accessorKey: 'updatedAt', header: 'Updated', cell: ({ row }) => formatTime(row.original.updatedAt) }
])

function open(_event: Event, row: { original: TResource }): void {
  if (props.to) {
    void router.push(`${props.to}/${encodeURIComponent(row.original.id)}`)
  }
}
</script>

<template>
  <UTable
    :data="rows"
    :columns="columns"
    :empty="empty ?? 'Nothing stored yet.'"
    sticky
    :class="to ? '[&_tbody_tr]:cursor-pointer' : ''"
    @select="open"
  >
    <template #name-cell="{ row }">
      <span class="font-medium">{{ row.original.name }}</span>
    </template>
    <template #id-cell="{ row }">
      <IdentityChip
        :value="row.original.id"
        label="ID"
      />
    </template>
    <template #state-cell="{ row }">
      <StateBadge :state="row.original.state" />
    </template>
  </UTable>
</template>
