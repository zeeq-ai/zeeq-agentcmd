<script setup lang="ts">
/** Collapsed raw JSON for the exact stored value; secondary detail is disclosed on demand. */
import { computed } from 'vue'

const props = defineProps<{ value: unknown; label?: string; open?: boolean }>()
const text = computed(() => JSON.stringify(props.value, null, 2) ?? 'undefined')
</script>

<template>
  <UCollapsible
    :default-open="open"
    class="rounded border border-default"
  >
    <UButton
      :label="label ?? 'Raw JSON'"
      color="neutral"
      variant="ghost"
      size="xs"
      block
      trailing-icon="i-lucide-chevron-down"
      class="justify-between"
      :ui="{ trailingIcon: 'group-data-[state=open]:rotate-180 transition-transform' }"
    />
    <template #content>
      <pre class="max-h-96 overflow-auto border-t border-default bg-muted p-2 font-mono text-xs">{{ text }}</pre>
    </template>
  </UCollapsible>
</template>
