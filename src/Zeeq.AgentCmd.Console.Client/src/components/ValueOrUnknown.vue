<script setup lang="ts">
/** Shows a value, or an explicit "unknown" so a missing usage, price or count never reads as zero. */
defineProps<{ value: unknown; suffix?: string }>()
</script>

<template>
  <span
    v-if="value === undefined || value === null || value === ''"
    class="value-unknown"
  >unknown</span>
  <span v-else>{{ value }}{{ suffix ?? '' }}</span>
</template>
