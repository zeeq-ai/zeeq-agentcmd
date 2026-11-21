<script setup lang="ts">
import { getWorkspace } from '../api/endpoints'
import { useResource } from '../composables/useResource'
import ApiState from '../components/ApiState.vue'
import PagePanel from '../components/PagePanel.vue'
import RawJson from '../components/RawJson.vue'
const workspace = useResource((signal) => getWorkspace(signal))
</script>
<template>
  <PagePanel
    id="settings"
    title="Settings"
    icon="i-lucide-settings"
  >
    <ApiState
      :result="workspace.result.value"
      :loading="workspace.loading.value"
      what="workspace"
      @retry="workspace.reload"
    >
      <template #default="{ value }">
        <div class="space-y-3 text-sm">
          <p>Actor: <span class="font-mono">{{ value.identity.subject }}</span> · {{ value.authentication.mode }} authentication</p>
          <p>Scope: <span class="font-mono">{{ value.scope.application }} / {{ value.scope.environment }} / {{ value.scope.partition }}</span></p>
          <p>Storage: {{ value.storage.provider }} · {{ value.storage.topology }}</p>
          <RawJson
            :value="value.identity.permissions"
            label="Permissions"
          />
          <RawJson
            :value="value.capabilities"
            label="Capabilities"
          />
          <RawJson
            :value="value.activeWorkers"
            label="Active workers"
          />
        </div>
      </template>
    </ApiState>
  </PagePanel>
</template>
