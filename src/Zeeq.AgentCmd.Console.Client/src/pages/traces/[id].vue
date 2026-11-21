<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { getExecution } from '../../api/endpoints'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import AssessmentActions from '../../components/AssessmentActions.vue'
import CaptureIncident from '../../components/CaptureIncident.vue'
import PagePanel from '../../components/PagePanel.vue'
import RawJson from '../../components/RawJson.vue'
import ResourceMeta from '../../components/ResourceMeta.vue'
const route = useRoute()
const id = computed(() => String((route.params as { id: string }).id))
const execution = useResource((signal) => getExecution(id.value, signal), [id])
</script>
<template>
  <PagePanel
    id="execution"
    title="Execution"
    icon="i-lucide-waypoints"
  >
    <template #leading>
      <UButton
        to="/traces"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Back to traces"
      />
    </template>
    <ApiState
      :result="execution.result.value"
      :loading="execution.loading.value"
      what="execution"
      @retry="execution.reload"
    >
      <template #default="{ value }">
        <div class="space-y-3">
          <ResourceMeta :resource="value" /><UAlert
            v-if="value.body.holdoutWithheld === true"
            color="warning"
            title="Holdout evidence withheld"
            description="Reveal the relevant acceptance case before inspecting or assessing its evidence."
          /><template v-else>
            <CaptureIncident :execution-id="value.id" /><AssessmentActions :execution-id="value.id" />
          </template><p class="text-xs text-muted">
            Cost: unknown unless recorded in the stored evidence.
          </p><RawJson
            v-if="value.body.holdoutWithheld !== true"
            :value="value.body"
            label="Stored execution evidence"
          />
        </div>
      </template>
    </ApiState>
  </PagePanel>
</template>
