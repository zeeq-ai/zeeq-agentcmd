<script setup lang="ts">
/** One registered build: its compiled commands and contracts. Immutable, so there is no edit. */
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { getBuild } from '../../../api/endpoints'
import { manifestCommands } from '../../../api/manifest'
import { useResource } from '../../../composables/useResource'
import ApiState from '../../../components/ApiState.vue'
import IdentityChip from '../../../components/IdentityChip.vue'
import PagePanel from '../../../components/PagePanel.vue'
import RawJson from '../../../components/RawJson.vue'
import ResourceMeta from '../../../components/ResourceMeta.vue'
import ValueOrUnknown from '../../../components/ValueOrUnknown.vue'

const route = useRoute('/commands/builds/[id]')
const id = computed(() => route.params.id)
const build = useResource((signal) => getBuild(id.value, signal), [id])
</script>

<template>
  <PagePanel
    id="build"
    :title="`Build ${id}`"
    icon="i-lucide-package"
  >
    <template #leading>
      <UButton
        to="/commands"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Back to commands"
      />
    </template>
    <template #actions>
      <UButton
        size="sm"
        icon="i-lucide-file-plus"
        label="Start draft publication"
        :to="{ path: '/commands/publications/new', query: { ...route.query, fromBuild: id } }"
      />
    </template>

    <ApiState
      :result="build.result.value"
      :loading="build.loading.value"
      what="the build"
      @retry="build.reload"
    >
      <template #default="{ value }">
        <div class="grid gap-3 lg:grid-cols-[1fr_20rem]">
          <div class="space-y-2">
            <h2 class="text-sm font-semibold">
              Commands
            </h2>
            <p
              v-if="manifestCommands(value.body).length === 0"
              class="text-muted"
            >
              The manifest lists no commands in a recognized shape. See the raw manifest.
            </p>
            <UCard
              v-for="command in manifestCommands(value.body)"
              :key="`${command.commandId}@${command.commandRevision}`"
              :ui="{ body: 'p-3 sm:p-3 space-y-2' }"
            >
              <div class="flex flex-wrap items-center gap-2">
                <span class="font-mono font-semibold">{{ command.commandId ?? 'unknown command' }}</span>
                <UBadge
                  color="neutral"
                  variant="outline"
                  size="sm"
                  :label="`compiled r${command.commandRevision ?? '?'}`"
                />
                <span class="text-xs text-muted">contract</span>
                <IdentityChip
                  :value="command.contractSignature"
                  label="Contract signature"
                />
                <span
                  v-if="command.definitionHash"
                  class="text-xs text-muted"
                >definition</span>
                <IdentityChip
                  v-if="command.definitionHash"
                  :value="command.definitionHash"
                  label="Definition hash"
                />
              </div>
              <div class="text-xs">
                <span class="text-muted">Overridable parameters: </span>
                <template v-if="command.overridableParameters?.length">
                  <UBadge
                    v-for="parameter in command.overridableParameters"
                    :key="parameter"
                    :label="parameter"
                    size="sm"
                    variant="subtle"
                    class="mr-1 font-mono"
                  />
                </template>
                <ValueOrUnknown
                  v-else-if="command.overridableParameters === undefined"
                  :value="undefined"
                />
                <span v-else>none</span>
              </div>
              <UAccordion
                type="multiple"
                :items="[
                  { label: 'Configured system template', slot: 'system' as const },
                  { label: 'Configured user template', slot: 'user' as const },
                  { label: 'Input and output schemas', slot: 'schemas' as const }
                ]"
                :ui="{ trigger: 'py-1.5 text-xs' }"
              >
                <template #system>
                  <pre class="whitespace-pre-wrap rounded bg-muted p-2 font-mono text-xs">{{ command.systemTemplate ?? 'none or not reported' }}</pre>
                </template>
                <template #user>
                  <pre class="whitespace-pre-wrap rounded bg-muted p-2 font-mono text-xs">{{ command.userTemplate ?? 'not reported' }}</pre>
                </template>
                <template #schemas>
                  <div class="grid gap-2 md:grid-cols-2">
                    <RawJson
                      :value="command.inputSchema"
                      label="Input schema"
                    />
                    <RawJson
                      :value="command.outputSchema"
                      label="Output schema (owned by the compiled command)"
                    />
                  </div>
                </template>
              </UAccordion>
            </UCard>
            <RawJson
              :value="value.body"
              label="Raw manifest"
            />
          </div>
          <UCard :ui="{ body: 'p-3 sm:p-3' }">
            <ResourceMeta :resource="value" />
            <p class="mt-2 text-xs text-muted">
              Build ID in body: <IdentityChip
                :value="value.body?.buildId"
                label="Build ID"
              />
            </p>
          </UCard>
        </div>
      </template>
    </ApiState>
  </PagePanel>
</template>
