<script setup lang="ts">
/**
 * Console shell: compact sidebar (five primary areas, Settings secondary), the scope selector,
 * the workspace identity/auth status and the job tray. The shell shows authentication and
 * availability failures from /workspace directly, because every page depends on them.
 */
import { computed, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { useIntervalFn } from '@vueuse/core'
import type { NavigationMenuItem } from '@nuxt/ui'
import { describeFailure } from './api/client'
import { getWorkspace, listJobs } from './api/endpoints'
import { useResource } from './composables/useResource'
import { applyScope, useScope } from './composables/useScope'
import StateBadge from './components/StateBadge.vue'

const router = useRouter()
const scope = useScope()

const navigation: NavigationMenuItem[] = [
  { label: 'Commands', icon: 'i-lucide-square-terminal', to: '/commands' },
  { label: 'Experiments', icon: 'i-lucide-flask-conical', to: '/experiments' },
  { label: 'Traces', icon: 'i-lucide-waypoints', to: '/traces' },
  { label: 'Datasets', icon: 'i-lucide-database', to: '/datasets' },
  { label: 'Review', icon: 'i-lucide-clipboard-check', to: '/review' }
]
const secondary: NavigationMenuItem[] = [{ label: 'Settings', icon: 'i-lucide-settings', to: '/settings' }]

const workspace = useResource((signal) => getWorkspace(signal))
const jobs = useResource((signal) => listJobs(signal))

// The tray refreshes while the tab is open; a failed refresh stays visible as a failure.
useIntervalFn(() => void jobs.reload(), 15000)

const activeJobs = computed(() => {
  const result = jobs.result.value
  if (result?.kind !== 'ok' || !Array.isArray(result.value)) {
    return []
  }
  const finished = ['completed', 'succeeded', 'failed', 'cancelled', 'canceled']
  return result.value.filter((job) => !finished.includes(String(job.body?.status ?? job.state).toLowerCase()))
})

const scopeDraft = reactive({ ...scope })
const scopeOpen = ref(false)
watch(scopeOpen, (open) => {
  if (open) {
    Object.assign(scopeDraft, scope)
  }
})

async function submitScope(): Promise<void> {
  await applyScope(router, {
    application: scopeDraft.application.trim(),
    environment: scopeDraft.environment.trim(),
    partition: scopeDraft.partition.trim()
  })
  scopeOpen.value = false
}

const served = computed(() => (workspace.result.value?.kind === 'ok' ? workspace.result.value.value : undefined))
const scopeMismatch = computed(() => {
  const actual = served.value?.scope
  if (!actual) {
    return false
  }
  return actual.application !== scope.application || actual.environment !== scope.environment || actual.partition !== scope.partition
})
</script>

<template>
  <Suspense>
    <UApp :tooltip="{ delayDuration: 250 }">
      <UDashboardGroup unit="rem">
        <UDashboardSidebar
          id="primary"
          collapsible
          :default-size="13"
          :min-size="11"
          :max-size="18"
          :ui="{ header: 'h-11 min-h-11', footer: 'flex-col items-stretch gap-1 border-t border-default py-2' }"
        >
          <template #header="{ collapsed }">
            <RouterLink
              to="/commands"
              class="flex items-center gap-1.5 font-semibold"
            >
              <UIcon
                name="i-lucide-terminal"
                class="size-4 text-primary"
              />
              <span v-if="!collapsed">AgentCmd</span>
            </RouterLink>
          </template>

          <template #default="{ collapsed }">
            <UNavigationMenu
              :collapsed="collapsed"
              :items="navigation"
              orientation="vertical"
              tooltip
            />
            <UNavigationMenu
              :collapsed="collapsed"
              :items="secondary"
              orientation="vertical"
              tooltip
              class="mt-auto"
            />
          </template>

          <template #footer="{ collapsed }">
            <UPopover
              v-model:open="scopeOpen"
              :content="{ side: 'right', align: 'end' }"
            >
              <UTooltip
                :text="`Scope: ${scope.application} / ${scope.environment} / ${scope.partition}`"
                :disabled="!collapsed"
              >
                <UButton
                  color="neutral"
                  variant="ghost"
                  size="xs"
                  icon="i-lucide-layers"
                  block
                  class="justify-start"
                  :label="collapsed ? undefined : `${scope.application} / ${scope.environment} / ${scope.partition}`"
                  :ui="{ label: 'font-mono truncate' }"
                  aria-label="Change scope"
                />
              </UTooltip>
              <template #content>
                <form
                  class="w-72 space-y-2 p-3"
                  @submit.prevent="submitScope"
                >
                  <p class="text-xs text-muted">
                    Sent as X-AgentCmd-* headers. The server decides access.
                  </p>
                  <UFormField
                    label="Application"
                    size="sm"
                  >
                    <UInput
                      v-model="scopeDraft.application"
                      size="sm"
                      class="w-full font-mono"
                    />
                  </UFormField>
                  <UFormField
                    label="Environment"
                    size="sm"
                  >
                    <UInput
                      v-model="scopeDraft.environment"
                      size="sm"
                      class="w-full font-mono"
                    />
                  </UFormField>
                  <UFormField
                    label="Partition"
                    size="sm"
                  >
                    <UInput
                      v-model="scopeDraft.partition"
                      size="sm"
                      class="w-full font-mono"
                    />
                  </UFormField>
                  <div class="flex justify-end">
                    <UButton
                      type="submit"
                      size="xs"
                      label="Apply scope"
                    />
                  </div>
                </form>
              </template>
            </UPopover>

            <UPopover :content="{ side: 'right', align: 'end' }">
              <UButton
                color="neutral"
                variant="ghost"
                size="xs"
                block
                class="justify-start"
                :icon="jobs.result.value?.kind === 'ok' ? 'i-lucide-list-checks' : 'i-lucide-list-x'"
                :label="collapsed ? undefined : jobs.result.value?.kind === 'ok' ? `Jobs: ${activeJobs.length} shown active${jobs.result.value.listLimit && jobs.result.value.value.length >= jobs.result.value.listLimit ? ` (max ${jobs.result.value.listLimit})` : ''}` : 'Jobs: unavailable'"
                aria-label="Job tray"
              />
              <template #content>
                <div class="w-80 p-2 text-xs">
                  <p
                    v-if="jobs.result.value && jobs.result.value.kind !== 'ok'"
                    class="text-error"
                  >
                    {{ describeFailure(jobs.result.value) }}
                  </p>
                  <p
                    v-else-if="activeJobs.length === 0"
                    class="text-muted"
                  >
                    No queued or running jobs in the returned list.
                  </p>
                  <ul
                    v-else
                    class="divide-y divide-default"
                  >
                    <li
                      v-for="job in activeJobs.slice(0, 8)"
                      :key="job.id"
                      class="flex items-center justify-between gap-2 py-1"
                    >
                      <RouterLink
                        :to="`/experiments/jobs/${encodeURIComponent(job.id)}`"
                        class="truncate hover:underline"
                      >
                        {{ job.name }}
                      </RouterLink>
                      <StateBadge :state="String(job.body?.status ?? job.state)" />
                    </li>
                  </ul>
                  <UButton
                    to="/experiments"
                    variant="link"
                    size="xs"
                    label="All jobs"
                    class="mt-1 px-0"
                  />
                </div>
              </template>
            </UPopover>

            <UTooltip
              :text="
                workspace.result.value?.kind === 'ok'
                  ? `${served?.identity.subject} · ${served?.authentication.mode} auth`
                  : workspace.result.value
                    ? describeFailure(workspace.result.value)
                    : 'Checking workspace…'
              "
            >
              <RouterLink
                to="/settings"
                class="flex items-center gap-1.5 rounded px-1.5 py-1 text-xs hover:bg-elevated"
                aria-label="Workspace status"
              >
                <span
                  class="size-2 shrink-0 rounded-full"
                  :class="workspace.result.value?.kind === 'ok' ? (scopeMismatch ? 'bg-warning' : 'bg-success') : workspace.result.value ? 'bg-error' : 'bg-muted'"
                />
                <span
                  v-if="!collapsed"
                  class="truncate"
                >
                  <template v-if="workspace.result.value?.kind === 'ok'">
                    {{ served?.identity.subject }}
                    <span class="text-muted">· {{ served?.authentication.mode }}</span>
                  </template>
                  <template v-else-if="workspace.result.value">{{ describeFailure(workspace.result.value) }}</template>
                  <template v-else>Checking…</template>
                </span>
              </RouterLink>
            </UTooltip>
          </template>
        </UDashboardSidebar>

        <RouterView />
      </UDashboardGroup>
    </UApp>
  </Suspense>
</template>
