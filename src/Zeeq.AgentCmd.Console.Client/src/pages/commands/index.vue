<script setup lang="ts">
/**
 * Commands: registered builds (immutable manifests reported by hosts) and the publications that
 * configure their experiments. A publication stays Draft until a separate review/target action.
 */
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { TableColumn } from '@nuxt/ui'
import { listBuilds, listPublications } from '../../api/endpoints'
import type { Build, Publication } from '../../api/types'
import { useResource } from '../../composables/useResource'
import { manifestCommands } from '../../api/manifest'
import { arrayOf } from '../../composables/format'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import RegisterBuild from '../../components/RegisterBuild.vue'
import ResourceTable from '../../components/ResourceTable.vue'

const route = useRoute()
const router = useRouter()

const tab = computed({
  get: () => (route.query.tab === 'publications' ? 'publications' : 'builds'),
  set: (value: string) => void router.replace({ query: { ...route.query, tab: value } })
})

const builds = useResource((signal) => listBuilds(signal))
const publications = useResource((signal) => listPublications(signal))
const registerOpen = ref(false)

const buildColumns: TableColumn<Build>[] = [
  { id: 'commands', header: 'Commands', cell: ({ row }) => manifestCommands(row.original.body).length }
]
const publicationColumns: TableColumn<Publication>[] = [
  { id: 'build', header: 'Build', cell: ({ row }) => row.original.body?.buildId ?? 'unknown' },
  { id: 'commands', header: 'Commands', cell: ({ row }) => arrayOf(row.original.body?.commands).length }
]

function onRegistered(build: Build): void {
  registerOpen.value = false
  void builds.reload()
  void router.push(`/commands/builds/${encodeURIComponent(build.id)}`)
}
</script>

<template>
  <PagePanel
    id="commands"
    title="Commands"
    icon="i-lucide-square-terminal"
  >
    <template #actions>
      <UButton
        v-if="tab === 'builds'"
        size="sm"
        icon="i-lucide-package-plus"
        label="Register build"
        @click="registerOpen = true"
      />
      <UButton
        v-else
        size="sm"
        icon="i-lucide-file-plus"
        label="New draft publication"
        to="/commands/publications/new"
      />
    </template>
    <template #toolbar>
      <UTabs
        v-model="tab"
        :items="[
          { label: 'Builds', value: 'builds', icon: 'i-lucide-package' },
          { label: 'Publications', value: 'publications', icon: 'i-lucide-git-branch' }
        ]"
        :content="false"
        variant="link"
        size="sm"
      />
    </template>

    <template v-if="tab === 'builds'">
      <ApiState
        :result="builds.result.value"
        :loading="builds.loading.value"
        what="builds"
        @retry="builds.reload"
      >
        <template #default="{ value }">
          <UEmpty
            v-if="value.length === 0"
            icon="i-lucide-package"
            title="No builds registered in this scope"
            description="A build is the immutable manifest of compiled commands: IDs, revisions and contract signatures. A host registers it at startup, or you can paste a manifest here. Publications and backtests need a registered build."
            :actions="[{ label: 'Register build', icon: 'i-lucide-package-plus', onClick: () => (registerOpen = true) }]"
          />
          <ResourceTable
            v-else
            :rows="value"
            to="/commands/builds"
            :extra="buildColumns"
          />
        </template>
      </ApiState>
    </template>

    <template v-else>
      <ApiState
        :result="publications.result.value"
        :loading="publications.loading.value"
        what="publications"
        @retry="publications.reload"
      >
        <template #default="{ value }">
          <UEmpty
            v-if="value.length === 0"
            icon="i-lucide-git-branch"
            title="No publications in this scope"
            description="A publication names a build and, per command, the experiment, its control variant and the variants' templates and weights. Start a draft from a registered build; freeze it before a backtest uses it."
            :actions="[{ label: 'New draft publication', icon: 'i-lucide-file-plus', to: '/commands/publications/new' }]"
          />
          <ResourceTable
            v-else
            :rows="value"
            to="/commands/publications"
            :extra="publicationColumns"
          />
        </template>
      </ApiState>
    </template>

    <USlideover
      v-model:open="registerOpen"
      title="Register build"
      description="Build registration is immutable. A later change needs a new build."
    >
      <template #body>
        <RegisterBuild @registered="onRegistered" />
      </template>
    </USlideover>
  </PagePanel>
</template>
