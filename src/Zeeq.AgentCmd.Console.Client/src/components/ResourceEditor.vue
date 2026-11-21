<script setup lang="ts">
/**
 * JSON body editor for publications, datasets and benchmarks.
 *
 * It keeps the draft/freeze boundary explicit: "Save draft" creates or updates (with If-Match),
 * and "Freeze" is a separate, confirmed action on the saved revision only. Unsaved edits stay
 * visible and survive a revision conflict, so a 409 never discards the author's text.
 */
import { computed, ref, shallowRef, watch } from 'vue'
import type { ApiResult } from '../api/client'
import { editable, type EditableCollection } from '../api/endpoints'
import type { ApiResource } from '../api/types'
import { bodyTemplates, checkBody } from '../api/validate'
import { isRecord } from '../composables/format'
import ApiState from './ApiState.vue'
import BenchmarkEditor from './BenchmarkEditor.vue'

const props = defineProps<{
  collection: EditableCollection
  /** The saved resource; undefined when creating. */
  resource?: ApiResource<unknown>
  /** Starting body for a new draft, such as one derived from a build. */
  initialBody?: unknown
}>()

const emit = defineEmits<{ saved: [resource: ApiResource<unknown>] }>()

const labels: Record<EditableCollection, string> = {
  publications: 'publication',
  datasets: 'dataset',
  benchmarks: 'benchmark'
}

const name = ref('')
const text = ref('')
const baseline = ref({ name: '', text: '' })
const busy = ref(false)
const lastFailure = shallowRef<ApiResult<unknown>>()
const freezeOpen = ref(false)
const cloning = ref(false)

function load(resource: ApiResource<unknown> | undefined): void {
  name.value = resource?.name ?? ''
  text.value = JSON.stringify(resource ? resource.body : (props.initialBody ?? bodyTemplates[props.collection]), null, 2)
  baseline.value = { name: name.value, text: resource ? text.value : '' }
  lastFailure.value = undefined
}

watch([() => props.resource, () => props.initialBody], () => load(props.resource), { immediate: true })

const redacted = computed(() => { const body = props.resource?.body; return props.collection === 'datasets' && isRecord(body) && Array.isArray(body.cases) && body.cases.some(item => isRecord(item) && item.holdoutWithheld === true) })
const isDraft = computed(() => cloning.value || !props.resource || props.resource.state.toLowerCase() === 'draft')
const dirty = computed(() => name.value !== baseline.value.name || text.value !== baseline.value.text)

const parsed = computed<{ ok: true; value: unknown } | { ok: false; message: string }>(() => {
  try {
    return { ok: true, value: JSON.parse(text.value) as unknown }
  } catch (error) {
    return { ok: false, message: error instanceof Error ? error.message : String(error) }
  }
})

const check = computed(() =>
  parsed.value.ok ? checkBody(props.collection, parsed.value.value) : { errors: [`Invalid JSON: ${parsed.value.message}`], warnings: [] }
)

const canSave = computed(
  () => isDraft.value && !redacted.value && dirty.value && !busy.value && name.value.trim() !== '' && check.value.errors.length === 0
)
const canFreeze = computed(() => !cloning.value && !!props.resource && isDraft.value && !dirty.value && !busy.value)

const conflict = computed(() => (lastFailure.value?.kind === 'conflict' ? lastFailure.value : undefined))

async function save(): Promise<void> {
  if (!canSave.value || !parsed.value.ok) {
    return
  }

  busy.value = true
  const api = editable[props.collection]
  const write = { name: name.value.trim(), body: parsed.value.value as never }
  const result = props.resource && !cloning.value
    ? await api.update(props.resource.id, props.resource.revision, write)
    : await api.create(write)
  busy.value = false

  if (result.kind === 'ok') {
    lastFailure.value = undefined
    cloning.value = false
    emit('saved', result.value as ApiResource<unknown>)
  } else {
    lastFailure.value = result
  }
}

async function freeze(): Promise<void> {
  if (!props.resource) {
    return
  }

  busy.value = true
  const result = await editable[props.collection].freeze(props.resource.id, props.resource.revision)
  busy.value = false
  freezeOpen.value = false

  if (result.kind === 'ok') {
    lastFailure.value = undefined
    emit('saved', result.value as ApiResource<unknown>)
  } else {
    lastFailure.value = result
  }
}

async function reloadServerVersion(): Promise<void> {
  if (!props.resource) {
    return
  }

  const result = await editable[props.collection].get(props.resource.id)
  if (result.kind === 'ok') {
    emit('saved', result.value as ApiResource<unknown>)
  } else {
    lastFailure.value = result
  }
}
</script>

<template>
  <div class="space-y-2">
    <div class="flex flex-wrap items-end gap-2">
      <UFormField
        label="Name"
        required
        class="min-w-64 flex-1"
      >
        <UInput
          v-model="name"
          size="sm"
          :disabled="!isDraft || redacted"
          class="w-full"
          :placeholder="`Name of the ${labels[collection]}`"
        />
      </UFormField>
      <UBadge
        v-if="dirty && isDraft"
        color="warning"
        variant="subtle"
        icon="i-lucide-pencil"
        label="Unsaved edits"
      />
      <UBadge
        v-if="!isDraft"
        color="neutral"
        variant="subtle"
        icon="i-lucide-lock"
        :label="`${resource?.state} — read-only`"
      />
      <UButton
        size="sm"
        icon="i-lucide-save"
        :label="resource && !cloning ? 'Save draft' : `Create draft ${labels[collection]}`"
        :disabled="!canSave"
        :loading="busy"
        @click="save"
      />
      <UButton
        v-if="resource && !isDraft"
        label="Clone to draft"
        :disabled="redacted"
        color="neutral"
        variant="outline"
        size="sm"
        @click="cloning = true; name = `${name} copy`"
      />
      <span
        v-if="redacted"
        class="text-xs text-warning"
      >Editing and cloning are unavailable while cases are withheld. Reveal the cases explicitly before editing their content.</span>
      <UTooltip
        v-if="resource && !cloning"
        :text="dirty ? 'Save or discard edits before freezing' : 'Freeze makes this revision immutable'"
      >
        <UButton
          size="sm"
          color="neutral"
          variant="outline"
          icon="i-lucide-snowflake"
          label="Freeze…"
          :disabled="!canFreeze"
          @click="freezeOpen = true"
        />
      </UTooltip>
    </div>

    <UAlert
      v-if="conflict"
      color="warning"
      variant="subtle"
      icon="i-lucide-git-compare"
      :title="`Revision conflict: you edited r${resource?.revision}, the server now has ${conflict.currentRevision !== undefined ? `r${conflict.currentRevision}` : 'a newer revision'}.`"
      description="Your edits are kept below. Copy them if you need them, then reload the server version."
      :actions="[{ label: 'Reload server version (discards edits)', color: 'warning', variant: 'outline', size: 'xs', onClick: reloadServerVersion }]"
    />
    <ApiState
      v-else-if="lastFailure && lastFailure.kind !== 'ok'"
      :result="lastFailure"
      what="the change"
      @retry="save"
    />

    <BenchmarkEditor
      v-if="collection === 'benchmarks' && parsed.ok"
      :model-value="parsed.value"
      :disabled="!isDraft || redacted"
      @update:model-value="text = JSON.stringify($event, null, 2)"
    />
    <p class="text-xs font-medium">
      Advanced body JSON
    </p>
    <div class="grid gap-2 lg:grid-cols-[1fr_18rem]">
      <UTextarea
        v-model="text"
        :disabled="!isDraft || redacted"
        :rows="22"
        autoresize
        :maxrows="40"
        spellcheck="false"
        class="w-full"
        :ui="{ base: 'font-mono text-xs leading-snug' }"
        aria-label="Body JSON"
      />
      <div class="space-y-2 text-xs">
        <div
          v-if="check.errors.length"
          class="rounded border border-error/40 bg-error/5 p-2"
        >
          <p class="mb-1 font-medium text-error">
            {{ check.errors.length }} error(s) block saving
          </p>
          <ul class="list-disc space-y-0.5 pl-4">
            <li
              v-for="message in check.errors"
              :key="message"
            >
              {{ message }}
            </li>
          </ul>
        </div>
        <div
          v-if="check.warnings.length"
          class="rounded border border-warning/40 bg-warning/5 p-2"
        >
          <p class="mb-1 font-medium text-warning">
            Warnings
          </p>
          <ul class="list-disc space-y-0.5 pl-4">
            <li
              v-for="message in check.warnings"
              :key="message"
            >
              {{ message }}
            </li>
          </ul>
        </div>
        <p
          v-if="!check.errors.length && !check.warnings.length"
          class="text-success"
        >
          <UIcon
            name="i-lucide-check"
            class="align-middle"
          /> Local checks pass. The server validates again on save.
        </p>
        <p class="text-muted">
          Drafts can change. Freezing creates an immutable revision that jobs and benchmarks can reference; it needs
          frozen dependencies.
        </p>
      </div>
    </div>

    <UModal
      v-model:open="freezeOpen"
      :title="`Freeze ${labels[collection]} r${resource?.revision}?`"
    >
      <template #body>
        <div class="space-y-2 text-sm">
          <p>
            Freezing makes revision <span class="font-mono">r{{ resource?.revision }}</span> of
            <span class="font-medium">{{ resource?.name }}</span> immutable. Later changes need a new draft.
          </p>
          <p class="text-muted">
            The server rejects the freeze if a dependency is not frozen. No model call runs.
          </p>
        </div>
      </template>
      <template #footer>
        <div class="flex w-full justify-end gap-2">
          <UButton
            size="sm"
            color="neutral"
            variant="ghost"
            label="Cancel"
            @click="freezeOpen = false"
          />
          <UButton
            size="sm"
            icon="i-lucide-snowflake"
            label="Freeze revision"
            :loading="busy"
            @click="freeze"
          />
        </div>
      </template>
    </UModal>
  </div>
</template>
