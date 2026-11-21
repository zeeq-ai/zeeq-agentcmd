<script setup lang="ts">
/** One console page: a compact navbar with title and actions, an optional toolbar and a scrolling body. */
defineProps<{ id: string; title: string; icon?: string }>()
</script>

<template>
  <UDashboardPanel :id="id">
    <template #header>
      <UDashboardNavbar
        :title="title"
        :icon="icon"
      >
        <template #leading>
          <slot name="leading" />
        </template>
        <template #right>
          <slot name="actions" />
        </template>
      </UDashboardNavbar>
      <UDashboardToolbar v-if="$slots.toolbar">
        <slot name="toolbar" />
      </UDashboardToolbar>
    </template>
    <template #body>
      <slot />
    </template>
  </UDashboardPanel>
</template>
