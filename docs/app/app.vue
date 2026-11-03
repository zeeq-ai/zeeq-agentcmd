<script setup lang="ts">
const { seo } = useAppConfig()
const base = useRuntimeConfig().app.baseURL
const route = useRoute()
const site = useRuntimeConfig().public.siteUrl
const canonical = computed(() => `${site}${base.replace(/\/$/, '')}${route.path}`)

const { data: navigation } = await useAsyncData('navigation', () => queryCollectionNavigation('docs'))
const { data: files } = useLazyAsyncData('search', () => queryCollectionSearchSections('docs', {
  ignoredTags: ['style']
}), {
  server: false
})

// Search the full section, but keep its visible and spoken preview short.
function searchPreview(value: unknown) {
  if (typeof value !== 'string') {
    return ''
  }
  const text = value.replaceAll('&lt;', '<').replaceAll('&gt;', '>')
  return text.length > 160 ? `${text.slice(0, 160)}…` : text
}

useHead({
  meta: [
    { name: 'viewport', content: 'width=device-width, initial-scale=1' }
  ],
  link: [
    { rel: 'icon', href: `${base}favicon.svg`, type: 'image/svg+xml' },
    { rel: 'canonical', href: canonical }
  ],
  htmlAttrs: {
    lang: 'en'
  }
})

useSeoMeta({
  titleTemplate: `%s - ${seo?.siteName}`,
  ogSiteName: seo?.siteName,
  ogUrl: canonical,
  twitterCard: 'summary_large_image'
})

provide('navigation', navigation)
</script>

<template>
  <UApp>
    <NuxtLoadingIndicator />

    <UBanner
      title="0.1.0-alpha.1 · .NET 11 / C# 15 RC · In-memory storage defaults"
      icon="i-lucide-flask-conical"
      color="neutral"
      :ui="{
        root: 'bg-muted border-b border-default',
        container: 'h-auto min-h-10 py-2',
        icon: 'text-muted',
        title: 'text-muted text-center whitespace-normal text-xs sm:text-sm'
      }"
    />

    <AppHeader />

    <UMain>
      <NuxtLayout>
        <NuxtPage />
      </NuxtLayout>
    </UMain>

    <AppFooter />

    <ClientOnly>
      <LazyUContentSearch
        :files="files"
        :navigation="navigation"
      >
        <template #item-label="{ item }">
          <span
            v-if="item.prefix"
            class="text-muted"
          >{{ item.prefix }}</span>
          <span>{{ item.label }}</span>
          <span
            v-if="item.suffix"
            class="truncate text-muted"
          >{{ searchPreview(item.suffix) }}</span>
        </template>
      </LazyUContentSearch>
    </ClientOnly>
  </UApp>
</template>
