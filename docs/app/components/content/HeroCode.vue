<script setup lang="ts">
import { useClipboard } from '@vueuse/core'

const example = useTemplateRef<HTMLElement>('example')
const { copy, copied } = useClipboard()

async function copyExample() {
  const code = example.value?.querySelector('pre code')?.textContent
  if (code) {
    await copy(code)
  }
}
</script>

<template>
  <div class="min-w-0 overflow-hidden rounded-lg border border-default bg-elevated/60">
    <div class="flex items-center justify-between gap-4 border-b border-default px-4 py-2">
      <span class="flex min-w-0 items-center gap-2 text-sm text-muted">
        <UIcon
          name="i-vscode-icons-file-type-csharp"
          class="size-4 shrink-0"
        />
        <span>ExtractInvoice.cs + call site</span>
      </span>
      <UButton
        :icon="copied ? 'i-lucide-copy-check' : 'i-lucide-copy'"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Copy example code"
        @click="copyExample"
      />
    </div>
    <div
      ref="example"
      class="min-w-0 px-4 py-4 [&_pre]:m-0 [&_pre]:overflow-x-auto [&_pre]:text-xs [&_pre]:leading-6 sm:[&_pre]:text-sm"
    >
      <slot />
    </div>
  </div>
</template>
