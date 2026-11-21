<script setup lang="ts">
import { listExecutions } from '../../api/endpoints'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import ResourceTable from '../../components/ResourceTable.vue'
const executions = useResource((signal) => listExecutions(signal))
</script>
<template>
  <PagePanel
    id="traces"
    title="Traces"
    icon="i-lucide-waypoints"
  >
    <p class="mb-3 text-xs text-muted">
      Stored executions in this scope. Trace query is not available.
    </p>
    <ApiState
      :result="executions.result.value"
      :loading="executions.loading.value"
      what="executions"
      @retry="executions.reload"
    >
      <template #default="{ value }">
        <ResourceTable
          :rows="value"
          to="/traces"
          empty="No executions in this scope."
        />
      </template>
    </ApiState>
  </PagePanel>
</template>
