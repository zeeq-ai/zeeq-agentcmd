<script setup lang="ts">
/**
 * An exact identity (ID, hash, revision, OutputRef). It may be shortened on screen, but the full
 * value is always in the tooltip and one click copies it, so an identity is never hidden.
 */
import { computed, ref } from 'vue'

const props = defineProps<{ value: string | number | null | undefined; label?: string; head?: number }>()

const copied = ref(false)
const text = computed(() => (props.value === null || props.value === undefined ? '' : String(props.value)))
const head = computed(() => props.head ?? 14)
const short = computed(() => (text.value.length > head.value + 4 ? `${text.value.slice(0, head.value)}…` : text.value))

async function copy(): Promise<void> {
  await navigator.clipboard.writeText(text.value)
  copied.value = true
  setTimeout(() => (copied.value = false), 1200)
}
</script>

<template>
  <span
    v-if="!text"
    class="value-unknown"
  >unknown</span>
  <UTooltip
    v-else
    :text="copied ? 'Copied' : `${label ? `${label}: ` : ''}${text} (click to copy)`"
  >
    <button
      type="button"
      class="inline-flex max-w-full items-center gap-1 rounded px-1 font-mono text-xs text-default hover:bg-elevated"
      :aria-label="`Copy ${label ?? 'identity'} ${text}`"
      @click.stop="copy"
    >
      <span class="truncate">{{ short }}</span>
      <UIcon
        :name="copied ? 'i-lucide-check' : 'i-lucide-copy'"
        class="size-3 shrink-0 text-muted"
      />
    </button>
  </UTooltip>
</template>
