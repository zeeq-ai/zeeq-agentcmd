<script setup lang="ts">
/**
 * A publication: per command, the experiment, control variant and variants. Drafts are edited as
 * JSON with local checks; freezing makes a snapshot that backtests can use. Promotion to a
 * runtime target is a separate server action that is not available yet.
 */
import { computed, shallowRef, watch } from 'vue'
import { useRoute } from 'vue-router'
import { manifestCommands } from '../../../api/manifest'
import { getBuild } from '../../../api/endpoints'
import type { Build, PublicationBody, PublicationCommandBody } from '../../../api/types'
import EditableResourcePage from '../../../components/EditableResourcePage.vue'
import IdentityChip from '../../../components/IdentityChip.vue'
import ValueOrUnknown from '../../../components/ValueOrUnknown.vue'
import PublicationDelivery from '../../../components/PublicationDelivery.vue'

const route = useRoute('/commands/publications/[id]')
const id = computed(() => route.params.id)
const fromBuild = computed(() => (typeof route.query.fromBuild === 'string' ? route.query.fromBuild : undefined))
const sourceBuild = shallowRef<Build>()

watch(
  fromBuild,
  async (buildResourceId) => {
    sourceBuild.value = undefined
    if (buildResourceId) {
      const result = await getBuild(buildResourceId)
      sourceBuild.value = result.kind === 'ok' ? result.value : undefined
    }
  },
  { immediate: true }
)

/**
 * A draft derived from a build copies its commands and configured templates as the control
 * variant. The experiment ID stays blank on purpose: the author must name it.
 */
const initialBody = computed<PublicationBody | undefined>(() => {
  const build = sourceBuild.value
  if (!build) {
    return undefined
  }
  return {
    buildId: build.body?.buildId ?? build.id,
    commands: manifestCommands(build.body).map(
      (command): PublicationCommandBody => ({
        commandId: command.commandId ?? '',
        commandRevision: command.commandRevision ?? 0,
        contractSignature: command.contractSignature ?? '',
        experimentId: '',
        controlVariantId: 'control',
        variants: [
          {
            id: 'control',
            weight: 1,
            userTemplate: command.userTemplate ?? '',
            parameters: {}
          }
        ]
      })
    )
  }
})

function totalWeight(command: PublicationCommandBody): number {
  return (command.variants ?? []).reduce((sum, variant) => sum + (typeof variant.weight === 'number' ? variant.weight : 0), 0)
}
</script>

<template>
  <EditableResourcePage
    :id="id"
    collection="publications"
    back="/commands?tab=publications"
    noun="publication"
    icon="i-lucide-git-branch"
    :initial-body="initialBody"
  >
    <template #summary="{ resource }">
      <UAlert
        v-if="resource.state.toLowerCase() === 'draft'"
        color="neutral"
        variant="subtle"
        icon="i-lucide-info"
        title="Draft: not served to any runtime"
        description="Freeze the draft to use it in a backtest. Publishing to a runtime target is a separate reviewed action."
      />
      <PublicationDelivery
        v-if="resource.state.toLowerCase() === 'frozen'"
        :publication="resource as import('../../../api/types').ApiResource<PublicationBody>"
      />
      <p class="text-xs">
        <span class="text-muted">Build </span>
        <IdentityChip
          :value="(resource.body as PublicationBody | undefined)?.buildId"
          label="Build ID"
          :head="32"
        />
      </p>
      <UCard
        v-for="command in (resource.body as PublicationBody | undefined)?.commands ?? []"
        :key="`${command.commandId}/${command.experimentId}`"
        :ui="{ header: 'p-2 sm:px-3', body: 'p-0 sm:p-0' }"
      >
        <template #header>
          <div class="flex flex-wrap items-center gap-2 text-xs">
            <span class="font-mono text-sm font-semibold">{{ command.commandId }}</span>
            <UBadge
              color="neutral"
              variant="outline"
              size="sm"
              :label="`r${command.commandRevision}`"
            />
            <IdentityChip
              :value="command.contractSignature"
              label="Contract signature"
            />
            <span class="text-muted">experiment</span>
            <span class="font-mono">{{ command.experimentId || '(unnamed)' }}</span>
            <span class="text-muted">control</span>
            <span class="font-mono">{{ command.controlVariantId }}</span>
          </div>
        </template>
        <table class="w-full text-xs">
          <thead class="text-muted">
            <tr class="border-b border-default text-left">
              <th class="px-3 py-1 font-medium">
                Variant
              </th>
              <th class="px-3 py-1 font-medium">
                Weight
              </th>
              <th class="px-3 py-1 font-medium">
                Route
              </th>
              <th class="px-3 py-1 font-medium">
                System template
              </th>
              <th class="px-3 py-1 font-medium">
                Parameters
              </th>
              <th class="px-3 py-1 font-medium">
                User template
              </th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="variant in command.variants ?? []"
              :key="variant.id"
              class="border-b border-default last:border-0 align-top"
            >
              <td class="px-3 py-1 font-mono">
                {{ variant.id }}
                <UBadge
                  v-if="variant.id === command.controlVariantId"
                  label="control"
                  size="sm"
                  variant="subtle"
                  class="ml-1"
                />
              </td>
              <td class="px-3 py-1">
                {{ variant.weight }}
                <span
                  v-if="totalWeight(command) > 0"
                  class="text-muted"
                >({{ Math.round((variant.weight / totalWeight(command)) * 100) }}%)</span>
              </td>
              <td class="px-3 py-1 font-mono">
                <ValueOrUnknown :value="variant.route ? `${variant.route.key?.id ?? 'unknown'} · ${variant.route.provider} / ${variant.route.model}` : 'inherited route'" />
              </td>
              <td class="px-3 py-1">
                {{ variant.systemMode ? `mode: ${variant.systemMode}` : variant.systemTemplate === null ? 'legacy: remove' : variant.systemTemplate === undefined ? 'legacy: inherit' : 'legacy: replace' }}
              </td>
              <td class="px-3 py-1 font-mono">
                {{ Object.keys(variant.parameters ?? {}).length ? JSON.stringify(variant.parameters) : 'none' }}
              </td>
              <td class="max-w-md px-3 py-1">
                <span class="line-clamp-2 font-mono">{{ variant.userTemplate || '(blank — invalid)' }}</span>
              </td>
            </tr>
          </tbody>
        </table>
      </UCard>
    </template>
  </EditableResourcePage>
</template>
