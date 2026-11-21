<script setup lang="ts">
/**
 * Registers a build manifest. Registration is immutable (no edit), so the form shows the exact
 * payload and requires valid JSON before it sends anything. The ID is optional; without it the
 * server derives `body.buildId`.
 */
import { computed, ref, shallowRef } from 'vue'
import type { ApiResult } from '../api/client'
import { registerBuild } from '../api/endpoints'
import type { Build, JsonObject } from '../api/types'
import { isRecord } from '../composables/format'
import ApiState from './ApiState.vue'

const emit = defineEmits<{ registered: [build: Build] }>()

const name = ref('')
const id = ref('')
const text = ref('{\n  "formatVersion": 1,\n  "catalogId": "",\n  "commands": []\n}')
const busy = ref(false)
const failure = shallowRef<ApiResult<Build>>()

const parsed = computed<{ value?: JsonObject; error?: string }>(() => {
  try {
    const value = JSON.parse(text.value) as unknown
    if (!isRecord(value)) { return { error: 'The manifest must be a JSON object.' } }
    const manifest = isRecord(value.manifest) ? value.manifest : value
    if (manifest.formatVersion !== 1 || typeof manifest.catalogId !== 'string' || !manifest.catalogId.trim() || !Array.isArray(manifest.commands)) {
      return { error: 'Manifest requires formatVersion 1, a nonblank catalogId, and a commands array.' }
    }
    if (manifest.commands.some(c => !isRecord(c) || typeof c.id !== 'string' || !c.id.trim())) {
      return { error: 'Each manifest command requires an id.' }
    }
    return { value: value as JsonObject }
  } catch (error) {
    return { error: `Invalid JSON: ${error instanceof Error ? error.message : String(error)}` }
  }
})

const canSubmit = computed(() => !busy.value && name.value.trim() !== '' && !!parsed.value.value)

async function submit(): Promise<void> {
  if (!parsed.value.value) {
    return
  }
  busy.value = true
  const result = await registerBuild({
    ...(id.value.trim() ? { id: id.value.trim() } : {}),
    name: name.value.trim(),
    body: parsed.value.value
  })
  busy.value = false
  if (result.kind === 'ok') {
    emit('registered', result.value)
  } else {
    failure.value = result
  }
}
</script>

<template>
  <form
    class="space-y-3"
    @submit.prevent="submit"
  >
    <UFormField
      label="Name"
      required
      size="sm"
    >
      <UInput
        v-model="name"
        size="sm"
        class="w-full"
        placeholder="invoice-service 2026.11.05"
      />
    </UFormField>
    <UFormField
      label="Build ID"
      hint="Optional"
      help="Optional server identity. For a direct manifest, supply the build ID here when needed."
      size="sm"
    >
      <UInput
        v-model="id"
        size="sm"
        class="w-full font-mono"
      />
    </UFormField>
    <UFormField
      label="Manifest (JSON body)"
      required
      :error="parsed.error"
      size="sm"
    >
      <UTextarea
        v-model="text"
        :rows="16"
        autoresize
        :maxrows="30"
        spellcheck="false"
        class="w-full"
        :ui="{ base: 'font-mono text-xs' }"
      />
    </UFormField>
    <ApiState
      v-if="failure && failure.kind !== 'ok'"
      :result="failure"
      what="the build"
      @retry="submit"
    />
    <div class="flex justify-end">
      <UButton
        type="submit"
        size="sm"
        icon="i-lucide-package-plus"
        label="Register immutable build"
        :disabled="!canSubmit"
        :loading="busy"
      />
    </div>
  </form>
</template>
