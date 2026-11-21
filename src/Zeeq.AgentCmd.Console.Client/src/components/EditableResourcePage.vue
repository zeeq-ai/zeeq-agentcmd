<script setup lang="ts" generic="TBody">
/**
 * Detail page for a draft-then-freeze resource (publication, dataset, benchmark). The `new` ID
 * opens a create form. After a save the page shows the server's stored envelope, not the local
 * text, so the revision and state on screen are always the server's.
 */
import { computed, ref, shallowRef, watch } from 'vue'
import { useRouter } from 'vue-router'
import type { ApiResult } from '../api/client'
import { editable, type EditableCollection } from '../api/endpoints'
import { useScope } from '../composables/useScope'
import { projectDataset } from '../api/datasetProjection'
import type { ApiResource } from '../api/types'
import ApiState from './ApiState.vue'
import PagePanel from './PagePanel.vue'
import RawJson from './RawJson.vue'
import ResourceEditor from './ResourceEditor.vue'
import ResourceMeta from './ResourceMeta.vue'
import StateBadge from './StateBadge.vue'

const props = defineProps<{
  collection: EditableCollection
  id: string
  back: string
  noun: string
  icon: string
  initialBody?: unknown
}>()

defineSlots<{ summary(props: { resource: ApiResource<TBody> }): unknown; aside(props: { resource: ApiResource<TBody> }): unknown }>()

const router = useRouter()
const creating = computed(() => props.id === 'new')
const result = shallowRef<ApiResult<ApiResource<TBody>>>()
const loading = ref(false)
const tab = ref('summary')
const scope = useScope()
let loadVersion = 0

async function load(): Promise<void> {
  const version = ++loadVersion
  result.value = undefined
  if (creating.value) {
    result.value = undefined
    return
  }
  loading.value = true
  const next = (await editable[props.collection].get(props.id)) as ApiResult<ApiResource<TBody>>
  if (version !== loadVersion) { return }
  result.value = next.kind === 'ok' && props.collection === 'datasets' ? { ...next, value: projectDataset(next.value) } : next
  loading.value = false
}

watch([() => props.id, () => ({ ...scope })], load, { immediate: true, deep: true })
defineExpose({ reload: load })

function onSaved(resource: ApiResource<unknown>): void {
  const shown = props.collection === 'datasets' ? projectDataset(resource) : resource
  result.value = { kind: 'ok', value: shown as ApiResource<TBody>, revision: resource.revision }
  if (creating.value || resource.id !== props.id) {
    void router.replace(`${props.collection === 'publications' ? '/commands/publications' : `/${props.collection}`}/${encodeURIComponent(resource.id)}`)
  }
}
</script>

<template>
  <PagePanel
    :id="collection"
    :title="creating ? `New draft ${noun}` : (result?.kind === 'ok' ? result.value.name : `${noun} ${id}`)"
    :icon="icon"
  >
    <template #leading>
      <UButton
        :to="back"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        :aria-label="`Back`"
      />
    </template>
    <template
      v-if="result?.kind === 'ok'"
      #actions
    >
      <StateBadge :state="result.value.state" />
      <UBadge
        color="neutral"
        variant="outline"
        size="sm"
        :label="`r${result.value.revision}`"
      />
    </template>

    <ResourceEditor
      v-if="creating"
      :collection="collection"
      :initial-body="initialBody"
      @saved="onSaved"
    />

    <ApiState
      v-else
      :result="result"
      :loading="loading"
      :what="`the ${noun}`"
      @retry="load"
    >
      <template #default="{ value }">
        <UTabs
          v-model="tab"
          :items="[
            { label: 'Summary', value: 'summary', slot: 'summary' as const },
            { label: value.state.toLowerCase() === 'draft' ? 'Edit draft' : 'Body', value: 'edit', slot: 'edit' as const },
            { label: 'Raw', value: 'raw', slot: 'raw' as const }
          ]"
          variant="link"
          size="sm"
          :unmount-on-hide="false"
        >
          <template #summary>
            <div class="grid gap-3 pt-2 lg:grid-cols-[1fr_20rem]">
              <div class="min-w-0 space-y-3">
                <slot
                  name="summary"
                  :resource="value"
                />
              </div>
              <div class="space-y-3">
                <UCard :ui="{ body: 'p-3 sm:p-3' }">
                  <ResourceMeta :resource="value" />
                </UCard>
                <slot
                  name="aside"
                  :resource="value"
                />
              </div>
            </div>
          </template>
          <template #edit>
            <div class="pt-2">
              <ResourceEditor
                :collection="collection"
                :resource="value"
                @saved="onSaved"
              />
            </div>
          </template>
          <template #raw>
            <div class="pt-2">
              <RawJson
                :value="value"
                label="Stored envelope"
                open
              />
            </div>
          </template>
        </UTabs>
      </template>
    </ApiState>
  </PagePanel>
</template>
