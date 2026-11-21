<script setup lang="ts">
/** Envelope facts every stored resource has. Shown compactly; all identities stay copyable. */
import type { ApiResource } from '../api/types'
import IdentityChip from './IdentityChip.vue'
import StateBadge from './StateBadge.vue'
import { formatTime } from '../composables/format'

defineProps<{ resource: ApiResource<unknown> }>()
</script>

<template>
  <dl class="grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs">
    <dt class="text-muted">
      ID
    </dt>
    <dd>
      <IdentityChip
        :value="resource.id"
        label="ID"
        :head="40"
      />
    </dd>
    <dt class="text-muted">
      Kind
    </dt>
    <dd class="font-mono">
      {{ resource.kind }}
    </dd>
    <dt class="text-muted">
      Revision
    </dt>
    <dd class="font-mono">
      r{{ resource.revision }}
    </dd>
    <dt class="text-muted">
      State
    </dt>
    <dd><StateBadge :state="resource.state" /></dd>
    <dt class="text-muted">
      Content hash
    </dt>
    <dd>
      <IdentityChip
        :value="resource.contentHash"
        label="Content hash"
        :head="20"
      />
    </dd>
    <dt class="text-muted">
      Created
    </dt>
    <dd>{{ formatTime(resource.createdAt) }}</dd>
    <dt class="text-muted">
      Updated
    </dt>
    <dd>{{ formatTime(resource.updatedAt) }}</dd>
  </dl>
</template>
