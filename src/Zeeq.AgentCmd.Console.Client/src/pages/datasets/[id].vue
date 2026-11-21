<script setup lang="ts">
/** One dataset: its cases with tags, reference completeness and exposure, plus the draft editor. */
import { computed, ref, shallowRef, watch } from 'vue'
import type { ApiResult } from '../../api/client'
import { revealDatasetCase } from '../../api/endpoints'
import ApiState from '../../components/ApiState.vue'
import DatasetExposure from '../../components/DatasetExposure.vue'
import { useRoute } from 'vue-router'
import type { DatasetBody, DatasetCaseBody } from '../../api/types'
import EditableResourcePage from '../../components/EditableResourcePage.vue'
import RawJson from '../../components/RawJson.vue'

const route = useRoute('/datasets/[id]')
const id = computed(() => route.params.id)
const filter = ref('')
const selected = ref<DatasetCaseBody>()
const reason = ref('')
watch(id, () => { selected.value = undefined; reason.value = '' })
const revealBusy = ref(false)
const revealFailure = shallowRef<ApiResult<DatasetCaseBody>>()
const exposurePanel = ref<InstanceType<typeof DatasetExposure>>()
const datasetPage = ref<{ reload: () => Promise<void> }>()
async function reveal(): Promise<void> {
  if (!selected.value?.holdoutWithheld || !reason.value.trim() || revealBusy.value) { return }
  revealBusy.value = true
  const result = await revealDatasetCase(id.value, selected.value.id, reason.value.trim())
  revealBusy.value = false
  if (result.kind === 'ok') { selected.value = result.value; revealFailure.value = undefined; reason.value = ''; await Promise.all([exposurePanel.value?.reload(), datasetPage.value?.reload()]) }
  else { revealFailure.value = result }
}

function cases(body: unknown): DatasetCaseBody[] {
  const all = (body as DatasetBody | undefined)?.cases ?? []
  const term = filter.value.trim().toLowerCase()
  if (!term) {
    return all
  }
  return all.filter((item) => item.id?.toLowerCase().includes(term) || item.tags?.some((tag) => tag.toLowerCase().includes(term)))
}

const completenessColor = (value: string | undefined) =>
  value === 'Exhaustive' ? 'success' : value === 'Partial' ? 'warning' : 'neutral'
</script>

<template>
  <EditableResourcePage
    :id="id"
    ref="datasetPage"
    collection="datasets"
    back="/datasets"
    noun="dataset"
    icon="i-lucide-database"
  >
    <template #summary="{ resource }">
      <div class="flex items-center gap-2">
        <UBadge
          :label="(resource.body as DatasetBody | undefined)?.purpose ?? 'purpose unknown'"
          variant="subtle"
        />
        <span class="text-xs text-muted">{{ (resource.body as DatasetBody | undefined)?.cases?.length ?? 0 }} cases</span>
        <UInput
          v-model="filter"
          size="xs"
          icon="i-lucide-search"
          placeholder="Filter by case ID or tag"
          class="ml-auto w-56"
        />
      </div>
      <DatasetExposure
        v-if="(resource.body as DatasetBody | undefined)?.purpose === 'Acceptance'"
        :id="id"
        ref="exposurePanel"
        :revision="resource.revision"
      />
      <UTable
        :data="cases(resource.body)"
        :columns="[
          { accessorKey: 'id', header: 'Case' },
          { accessorKey: 'sourceFamilyId', header: 'Source family' },
          { id: 'tags', header: 'Tags' },
          { id: 'reference', header: 'Reference' },
          { id: 'exposed', header: 'Exposed' }
        ]"
        empty="No cases. Edit the draft to add cases."
        class="[&_tbody_tr]:cursor-pointer"
        @select="(_event: Event, row: { original: DatasetCaseBody }) => (selected = row.original)"
      >
        <template #id-cell="{ row }">
          <span class="font-mono">{{ row.original.id }}</span>
        </template>
        <template #sourceFamilyId-cell="{ row }">
          <span class="font-mono text-xs">{{ row.original.sourceFamilyId }}</span>
        </template>
        <template #tags-cell="{ row }">
          <UBadge
            v-for="tag in row.original.tags ?? []"
            :key="tag"
            :label="tag"
            size="sm"
            variant="outline"
            color="neutral"
            class="mr-1"
          />
        </template>
        <template #reference-cell="{ row }">
          <UBadge
            :label="row.original.holdoutWithheld ? 'Withheld' : row.original.reference?.completeness ?? 'None'"
            :color="completenessColor(row.original.reference?.completeness)"
            variant="subtle"
            size="sm"
          />
        </template>
        <template #exposed-cell="{ row }">
          <UTooltip :text="row.original.exposed === true ? 'Exposed: this case may have been seen by authors or models' : row.original.exposed === false ? 'Not reported as exposed' : 'Exposure status unknown'">
            <UIcon
              :name="row.original.exposed ? 'i-lucide-eye' : 'i-lucide-eye-off'"
              class="text-muted"
            />
          </UTooltip>
        </template>
      </UTable>

      <USlideover
        :open="!!selected"
        :title="`Case ${selected?.id}`"
        @update:open="(open: boolean) => !open && (selected = undefined)"
      >
        <template #body>
          <div
            v-if="selected"
            class="space-y-2"
          >
            <UAlert
              v-if="selected.holdoutWithheld"
              title="Held-out case withheld"
              description="Input, reference and assets are not included in this projection."
              color="warning"
            />
            <div
              v-if="selected.holdoutWithheld"
              class="space-y-2"
            >
              <p class="text-xs text-warning">
                Revealing records durable exposure for this case and its source family. It makes independent acceptance ineligible. This does not change the dataset revision or hash.
              </p>
              <UFormField
                label="Reason for reveal"
                required
              >
                <UTextarea
                  v-model="reason"
                  class="w-full"
                />
              </UFormField>
              <UButton
                label="Reveal held-out case"
                size="sm"
                color="warning"
                :disabled="!reason.trim()"
                :loading="revealBusy"
                @click="reveal"
              />
              <ApiState
                v-if="revealFailure && revealFailure.kind !== 'ok'"
                :result="revealFailure"
                what="case reveal"
                retry-label="Dismiss"
                @retry="revealFailure = undefined"
              />
            </div>
            <RawJson
              v-if="!selected.holdoutWithheld"
              :value="selected.input"
              label="Input"
              open
            />
            <RawJson
              v-if="!selected.holdoutWithheld"
              :value="selected.reference"
              :label="`Reference (${selected.reference?.completeness ?? 'None'})`"
              open
            />
            <RawJson
              v-if="!selected.holdoutWithheld"
              :value="selected.assets"
              label="Assets"
            />
          </div>
        </template>
      </USlideover>
    </template>
  </EditableResourcePage>
</template>
