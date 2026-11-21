<script setup lang="ts">
import { listReviews } from '../../api/endpoints'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import PagePanel from '../../components/PagePanel.vue'
import ResourceTable from '../../components/ResourceTable.vue'
const reviews = useResource((signal) => listReviews(signal))
</script>
<template>
  <PagePanel
    id="review"
    title="Review"
    icon="i-lucide-clipboard-check"
  >
    <template #actions>
      <UButton
        to="/review/calibrations"
        label="Calibrations"
        size="sm"
        color="neutral"
        variant="outline"
      />
    </template>
    <ApiState
      :result="reviews.result.value"
      :loading="reviews.loading.value"
      what="reviews"
      @retry="reviews.reload"
    >
      <template #default="{ value }">
        <ResourceTable
          :rows="value"
          to="/review"
          empty="No reviews in this scope."
        />
      </template>
    </ApiState>
  </PagePanel>
</template>
