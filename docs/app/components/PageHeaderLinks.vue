<script setup lang="ts">
import { useClipboard } from '@vueuse/core'
import { withBase, withoutTrailingSlash } from 'ufo'

const route = useRoute()
const toast = useToast()
const { copy, copied } = useClipboard()
const config = useRuntimeConfig()

// Keep fetch and link paths under the repository prefix on GitHub Pages.
const rawPath = computed(() => withBase(`/raw${withoutTrailingSlash(route.path)}.md`, config.app.baseURL))
const markdownUrl = computed(() => `${config.public.siteUrl}${rawPath.value}`)
const items = computed(() => [{
  label: 'Copy Markdown link',
  icon: 'i-lucide-link',
  async onSelect() {
    await copy(markdownUrl.value)
    toast.add({ title: 'Markdown link copied', icon: 'i-lucide-check' })
  }
}, {
  label: 'View as Markdown',
  icon: 'i-lucide-file-text',
  to: rawPath.value,
  external: true,
  target: '_blank'
}])

async function copyPage() {
  try {
    await copy(await $fetch<string>(rawPath.value))
    toast.add({ title: 'Page copied as Markdown', icon: 'i-lucide-check' })
  } catch {
    toast.add({ title: 'Could not copy this page', description: 'Use View as Markdown from the menu.', color: 'error' })
  }
}
</script>

<template>
  <UFieldGroup>
    <UButton
      label="Copy page"
      :icon="copied ? 'i-lucide-copy-check' : 'i-lucide-copy'"
      color="neutral"
      variant="outline"
      @click="copyPage"
    />
    <UDropdownMenu
      :items="items"
      :content="{ align: 'end', sideOffset: 8 }"
    >
      <UButton
        icon="i-lucide-chevron-down"
        color="neutral"
        variant="outline"
        aria-label="More page actions"
      />
    </UDropdownMenu>
  </UFieldGroup>
</template>
