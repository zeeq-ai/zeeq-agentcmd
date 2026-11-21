<script setup lang="ts">
/** Displays retained judge findings without treating a cited locator as verified entailment. */
import { computed } from 'vue'
import { isRecord } from '../composables/format'
import RawJson from './RawJson.vue'
const props = defineProps<{ findings: unknown }>()
const entries = computed(() => Array.isArray(props.findings) ? props.findings.filter(isRecord) : [])
</script>
<template>
  <div class="space-y-2">
    <p
      v-if="!entries.length"
      class="text-xs text-muted"
    >
      No structured semantic findings reported; evidence is unknown.
    </p>
    <div
      v-for="(entry, index) in entries"
      :key="index"
      class="rounded border border-default p-2 text-xs"
    >
      <p class="font-semibold">
        {{ entry.category ?? 'category unknown' }}
      </p>
      <p v-if="entry.rationale">
        {{ entry.rationale }}
      </p>
      <p>Candidate pointer: <span class="font-mono">{{ entry.candidatePointer ?? 'unknown' }}</span> · Reference pointer: <span class="font-mono">{{ entry.referencePointer ?? 'unknown' }}</span></p>
      <RawJson
        :value="entry.sourceLocator ?? null"
        label="Source locator (not an entailment verification)"
      />
    </div>
    <RawJson
      :value="findings ?? null"
      label="Exact findings JSON"
    />
  </div>
</template>
