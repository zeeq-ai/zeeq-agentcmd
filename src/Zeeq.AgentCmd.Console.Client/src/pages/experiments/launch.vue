<script setup lang="ts">
/**
 * Backtest / Reassess launch with an explicit review step.
 *
 * Nothing runs on this page. Saving creates a job with `executionApproved: true` only after the
 * operator checks the approval box on the review step; a worker then claims it. Candidates (what
 * generates outputs) and the judge (what scores them) are chosen in separate steps and shown in
 * separate panels, because the benchmark version fixes the judge and a candidate cannot change it.
 */
import { computed, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { ApiResult } from '../../api/client'
import { createJob, getWorkspace, listBenchmarks, listDatasets, listJobs, listPublications } from '../../api/endpoints'
import type { Job, JobCandidate, JobKind, PublicationBody } from '../../api/types'
import { useResource } from '../../composables/useResource'
import ApiState from '../../components/ApiState.vue'
import IdentityChip from '../../components/IdentityChip.vue'
import PagePanel from '../../components/PagePanel.vue'
import StateBadge from '../../components/StateBadge.vue'

const route = useRoute()
const router = useRouter()

const benchmarks = useResource((signal) => listBenchmarks(signal))
const publications = useResource((signal) => listPublications(signal))
const datasets = useResource((signal) => listDatasets(signal))
const jobs = useResource((signal) => listJobs(signal))
const workspace = useResource((signal) => getWorkspace(signal))

const okValue = <TValue,>(result: ApiResult<TValue> | undefined): TValue | undefined =>
  result?.kind === 'ok' ? result.value : undefined

const form = reactive({
  kind: (route.query.kind === 'Reassess' ? 'Reassess' : 'Backtest') as JobKind,
  benchmarkId: '',
  sourceJobId: typeof route.query.sourceJobId === 'string' ? route.query.sourceJobId : '',
  name: '',
  approved: false
})
const candidates = ref<JobCandidate[]>([])
const step = ref(0)

const isFrozen = (state: string) => state.toLowerCase() === 'frozen'

const benchmark = computed(() => okValue(benchmarks.result.value)?.find((item) => item.id === form.benchmarkId))
const dataset = computed(() => okValue(datasets.result.value)?.find((item) => item.id === benchmark.value?.body?.datasetId))
const sourceJob = computed(() => okValue(jobs.result.value)?.find((item) => item.id === form.sourceJobId))

const benchmarkItems = computed(() =>
  (okValue(benchmarks.result.value) ?? []).map((item) => ({
    label: `${item.name} · r${item.revision} · ${item.state}`,
    value: item.id,
    disabled: !isFrozen(item.state)
  }))
)
const sourceJobItems = computed(() =>
  (okValue(jobs.result.value) ?? [])
    .filter((item) => item.body?.kind === 'Backtest')
    .map((item) => ({ label: `${item.name} · ${item.body?.status ?? item.state}`, value: item.id }))
)

// Reassess re-scores the outputs of a source backtest, so its candidates come from that job.
watch(sourceJob, (job) => {
  if (form.kind === 'Reassess' && job) {
    candidates.value = (job.body?.candidates ?? []).map((candidate) => ({ ...candidate }))
    if (job.body?.benchmarkId && !form.benchmarkId) {
      form.benchmarkId = job.body.benchmarkId
    }
  }
})
watch(
  () => form.kind,
  () => {
    candidates.value = []
    form.approved = false
  }
)

// Candidate picker.
const pick = reactive({ publicationId: '', commandId: '', variantId: '' })
const publication = computed(() => okValue(publications.result.value)?.find((item) => item.id === pick.publicationId))
const publicationBody = computed(() => publication.value?.body as PublicationBody | undefined)
const command = computed(() => publicationBody.value?.commands?.find((item) => item.commandId === pick.commandId))

const publicationItems = computed(() =>
  (okValue(publications.result.value) ?? []).map((item) => ({
    label: `${item.name} · r${item.revision} · ${isFrozen(item.state) ? 'frozen' : `draft ${item.contentHash.slice(0, 10)}`}`,
    value: item.id
  }))
)
const commandItems = computed(() => (publicationBody.value?.commands ?? []).map((item) => ({ label: `${item.commandId} r${item.commandRevision}`, value: item.commandId })))
const variantItems = computed(() => (command.value?.variants ?? []).map((item) => ({ label: item.id, value: item.id })))

watch(() => pick.publicationId, () => Object.assign(pick, { commandId: '', variantId: '' }))
watch(() => pick.commandId, () => (pick.variantId = ''))

function addCandidate(): void {
  if (!publication.value || !command.value || !pick.variantId) {
    return
  }
  const index = candidates.value.length + 1
  candidates.value.push({
    id: `c${index}`,
    name: `${command.value.commandId}:${pick.variantId}`,
    buildId: publicationBody.value?.buildId ?? '',
    publicationId: publication.value.id,
    commandId: command.value.commandId,
    variantId: pick.variantId
  })
  pick.variantId = ''
}

const duplicateCandidate = computed(() => {
  const keys = candidates.value.map((item) => `${item.publicationId}/${item.commandId}/${item.variantId}`)
  return new Set(keys).size !== keys.length
})

const judgeCriteria = computed(() => (benchmark.value?.body?.criteria ?? []).filter((item) => item.scorer?.kind === 'Llm'))
const automaticCriteria = computed(() =>
  (benchmark.value?.body?.criteria ?? []).filter((item) => item.scorer?.kind === 'Exact' || item.scorer?.kind === 'Numeric')
)
const humanCriteria = computed(() => (benchmark.value?.body?.criteria ?? []).filter((item) => item.scorer?.kind === 'Human'))

const caseCount = computed(() => dataset.value?.body?.cases?.length)
const repeats = computed(() => benchmark.value?.body?.repeats ?? 1)
const plannedGenerations = computed(() =>
  form.kind === 'Backtest' && caseCount.value !== undefined ? caseCount.value * candidates.value.length * repeats.value : undefined
)

const noWorker = computed(() => {
  const info = okValue(workspace.result.value)
  return !!info && (!info.capabilities.typedWorker || info.activeWorkers.length === 0)
})

const stepValid = computed(() => [
  !!benchmark.value && isFrozen(benchmark.value.state) && (form.kind === 'Backtest' || !!sourceJob.value),
  candidates.value.length > 0 && !duplicateCandidate.value,
  true,
  form.name.trim() !== '' && form.approved
])

const steps = computed(() => [
  { title: 'Benchmark', description: form.kind, icon: 'i-lucide-ruler' },
  { title: 'Candidates', description: `${candidates.value.length} selected`, icon: 'i-lucide-git-branch' },
  { title: 'Judge', description: judgeCriteria.value.length ? `${judgeCriteria.value.length} LLM criteria` : 'no LLM judge', icon: 'i-lucide-scale' },
  { title: 'Review', description: 'Approve and save', icon: 'i-lucide-shield-check' }
])

const busy = ref(false)
const failure = ref<ApiResult<Job>>()

async function submit(): Promise<void> {
  if (!stepValid.value.every(Boolean) || !benchmark.value) {
    return
  }
  busy.value = true
  const result = await createJob({
    name: form.name.trim(),
    body: {
      kind: form.kind,
      datasetId: benchmark.value.body?.datasetId ?? '',
      benchmarkId: benchmark.value.id,
      candidates: candidates.value,
      executionApproved: true,
      ...(form.kind === 'Reassess' ? { sourceJobId: form.sourceJobId } : {})
    }
  })
  busy.value = false
  if (result.kind === 'ok') {
    void router.push(`/experiments/jobs/${encodeURIComponent(result.value.id)}`)
  } else {
    failure.value = result
  }
}
</script>

<template>
  <PagePanel
    id="launch"
    :title="`New ${form.kind.toLowerCase()}`"
    icon="i-lucide-rocket"
  >
    <template #leading>
      <UButton
        to="/experiments"
        icon="i-lucide-arrow-left"
        color="neutral"
        variant="ghost"
        size="xs"
        aria-label="Back to experiments"
      />
    </template>

    <UStepper
      v-model="step"
      :items="steps"
      size="xs"
      :linear="false"
      class="w-full"
    />

    <ApiState
      :result="benchmarks.result.value"
      :loading="benchmarks.loading.value"
      what="benchmarks"
      @retry="benchmarks.reload"
    >
      <template #default>
        <!-- Step 1: kind and benchmark -->
        <section
          v-if="step === 0"
          class="max-w-2xl space-y-3"
        >
          <URadioGroup
            v-model="form.kind"
            orientation="horizontal"
            variant="card"
            size="sm"
            :items="[
              { label: 'Backtest', value: 'Backtest', description: 'Generates new outputs for every case and candidate. Paid model calls.' },
              { label: 'Reassess', value: 'Reassess', description: 'Re-scores the stored outputs of an earlier backtest. No new generation; judge calls may cost.' }
            ]"
          />
          <UFormField
            v-if="form.kind === 'Reassess'"
            label="Source backtest"
            required
            size="sm"
            help="Its stored outputs are scored again; its candidates are reused."
          >
            <USelect
              v-model="form.sourceJobId"
              :items="sourceJobItems"
              placeholder="Choose a backtest job"
              size="sm"
              class="w-full"
            />
          </UFormField>
          <UFormField
            label="Benchmark"
            required
            size="sm"
            help="Only frozen benchmarks qualify. A draft must be frozen first."
          >
            <USelect
              v-model="form.benchmarkId"
              :items="benchmarkItems"
              placeholder="Choose a frozen benchmark"
              size="sm"
              class="w-full"
            />
          </UFormField>
          <p
            v-if="benchmarkItems.length === 0"
            class="text-xs text-muted"
          >
            No benchmarks yet. <ULink to="/benchmarks/new">
              Create a draft benchmark
            </ULink> and freeze it.
          </p>
          <div
            v-if="benchmark"
            class="rounded border border-default p-2 text-xs"
          >
            <span class="text-muted">Dataset </span>
            <IdentityChip
              :value="benchmark.body?.datasetId"
              label="Dataset"
            />
            <span v-if="dataset"> · {{ dataset.name }} · {{ caseCount }} cases · <StateBadge :state="dataset.state" /></span>
            <span
              v-else
              class="value-unknown"
            > · not readable in this scope</span>
          </div>
        </section>

        <!-- Step 2: candidates -->
        <section
          v-else-if="step === 1"
          class="space-y-3"
        >
          <div
            v-if="form.kind === 'Backtest'"
            class="flex flex-wrap items-end gap-2"
          >
            <UFormField
              label="Publication"
              size="sm"
              class="w-72"
            >
              <USelect
                v-model="pick.publicationId"
                :items="publicationItems"
                placeholder="Choose a publication"
                size="sm"
                class="w-full"
              />
            </UFormField>
            <UFormField
              label="Command"
              size="sm"
              class="w-56"
            >
              <USelect
                v-model="pick.commandId"
                :items="commandItems"
                :disabled="!publication"
                size="sm"
                class="w-full"
              />
            </UFormField>
            <UFormField
              label="Variant"
              size="sm"
              class="w-44"
            >
              <USelect
                v-model="pick.variantId"
                :items="variantItems"
                :disabled="!command"
                size="sm"
                class="w-full"
              />
            </UFormField>
            <UButton
              size="sm"
              icon="i-lucide-plus"
              label="Add candidate"
              :disabled="!pick.variantId"
              @click="addCandidate"
            />
          </div>
          <p
            v-if="publication && publication.state.toLowerCase() !== 'frozen'"
            class="text-xs text-warning"
          >
            Draft publication: the job pins its current content hash
            <IdentityChip
              :value="publication.contentHash"
              label="Content hash"
            />. Later edits are not used.
          </p>
          <p
            v-if="form.kind === 'Reassess'"
            class="text-xs text-muted"
          >
            Candidates come from the source backtest and cannot change.
          </p>
          <UTable
            :data="candidates"
            :columns="[
              { accessorKey: 'id', header: 'ID' },
              { accessorKey: 'name', header: 'Name' },
              { accessorKey: 'publicationId', header: 'Publication' },
              { accessorKey: 'commandId', header: 'Command' },
              { accessorKey: 'variantId', header: 'Variant' },
              { accessorKey: 'buildId', header: 'Build' },
              { id: 'remove', header: '' }
            ]"
            empty="No candidates yet. Add at least one publication variant."
          >
            <template #name-cell="{ row }">
              <UInput
                v-model="row.original.name"
                size="xs"
                :disabled="form.kind === 'Reassess'"
              />
            </template>
            <template #publicationId-cell="{ row }">
              <IdentityChip
                :value="row.original.publicationId"
                label="Publication"
              />
            </template>
            <template #buildId-cell="{ row }">
              <IdentityChip
                :value="row.original.buildId"
                label="Build"
              />
            </template>
            <template #remove-cell="{ row }">
              <UTooltip
                v-if="form.kind === 'Backtest'"
                text="Remove candidate"
              >
                <UButton
                  icon="i-lucide-x"
                  color="neutral"
                  variant="ghost"
                  size="xs"
                  aria-label="Remove candidate"
                  @click="candidates.splice(row.index, 1)"
                />
              </UTooltip>
            </template>
          </UTable>
          <p
            v-if="duplicateCandidate"
            class="text-xs text-error"
          >
            The same publication variant is listed twice.
          </p>
        </section>

        <!-- Step 3: judge (read-only; fixed by the benchmark) -->
        <section
          v-else-if="step === 2"
          class="max-w-3xl space-y-3"
        >
          <UAlert
            color="neutral"
            variant="subtle"
            icon="i-lucide-scale"
            title="The judge is fixed by the benchmark version"
            description="Scorers and judge configurations come from the frozen benchmark. Candidates generate outputs; the judge only scores them. To change the judge, freeze a new benchmark version."
          />
          <p
            v-if="!benchmark"
            class="text-muted"
          >
            Choose a benchmark first.
          </p>
          <template v-else>
            <div class="rounded border border-default p-2">
              <p class="mb-1 text-xs font-medium">
                LLM judge criteria ({{ judgeCriteria.length }})
              </p>
              <p
                v-if="!judgeCriteria.length"
                class="text-xs text-muted"
              >
                None. No judge model calls.
              </p>
              <ul class="space-y-1 text-xs">
                <li
                  v-for="criterion in judgeCriteria"
                  :key="criterion.id"
                >
                  <span class="font-medium">{{ criterion.name }}</span>
                  <span class="text-muted"> · {{ criterion.scope }} · output {{ criterion.outputBinding || '""' }} · judge </span>
                  <IdentityChip
                    :value="criterion.scorer.judgeConfigId"
                    label="Judge config"
                  />
                  <UBadge
                    v-if="criterion.id === benchmark.body?.primaryCriterionId"
                    label="Primary"
                    size="sm"
                    class="ml-1"
                  />
                </li>
              </ul>
            </div>
            <p class="text-xs text-muted">
              Also scored: {{ automaticCriteria.length }} deterministic criteria (Exact/Numeric, no model call) and
              {{ humanCriteria.length }} human criteria (queued for review).
            </p>
          </template>
        </section>

        <!-- Step 4: review and approve -->
        <section
          v-else
          class="space-y-3"
        >
          <div class="grid gap-3 md:grid-cols-2">
            <UCard :ui="{ header: 'p-2 sm:px-3', body: 'p-3 sm:p-3 text-xs space-y-1' }">
              <template #header>
                <p class="text-sm font-semibold">
                  Candidates · generate outputs
                </p>
              </template>
              <p
                v-if="form.kind === 'Reassess'"
                class="text-muted"
              >
                Reassess: no generation. Outputs of the source job are reused.
              </p>
              <div
                v-for="candidate in candidates"
                :key="candidate.id"
                class="flex items-center gap-2"
              >
                <span class="font-mono">{{ candidate.id }}</span>
                <span>{{ candidate.name }}</span>
                <IdentityChip
                  :value="candidate.publicationId"
                  label="Publication"
                />
              </div>
            </UCard>
            <UCard :ui="{ header: 'p-2 sm:px-3', body: 'p-3 sm:p-3 text-xs space-y-1' }">
              <template #header>
                <p class="text-sm font-semibold">
                  Judge · scores outputs
                </p>
              </template>
              <p>Benchmark <span class="font-medium">{{ benchmark?.name }}</span> r{{ benchmark?.revision }}</p>
              <p>{{ judgeCriteria.length }} LLM, {{ automaticCriteria.length }} deterministic, {{ humanCriteria.length }} human criteria</p>
              <p>Primary: <span class="font-mono">{{ benchmark?.body?.primaryCriterionId }}</span></p>
            </UCard>
          </div>

          <dl class="grid max-w-xl grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs">
            <dt class="text-muted">
              Cases
            </dt>
            <dd>{{ caseCount ?? 'unknown' }}</dd>
            <dt class="text-muted">
              Repeats
            </dt>
            <dd>{{ repeats }}</dd>
            <dt class="text-muted">
              Planned generations
            </dt>
            <dd>
              <template v-if="form.kind === 'Reassess'">
                0 (reassess)
              </template>
              <span
                v-else-if="plannedGenerations === undefined"
                class="value-unknown"
              >unknown</span>
              <template v-else>
                {{ plannedGenerations }}
              </template>
            </dd>
            <dt class="text-muted">
              Estimated cost
            </dt>
            <dd class="value-unknown">
              unknown — no price source is available; this console does not bound spend
            </dd>
          </dl>

          <UAlert
            v-if="noWorker"
            color="warning"
            variant="subtle"
            icon="i-lucide-hourglass"
            title="No active typed worker"
            description="The job will be saved but stay queued or blocked until a worker that hosts these commands is running."
          />

          <div class="max-w-xl space-y-2">
            <UFormField
              label="Job name"
              required
              size="sm"
            >
              <UInput
                v-model="form.name"
                size="sm"
                class="w-full"
                :placeholder="`${form.kind} ${benchmark?.name ?? ''}`"
              />
            </UFormField>
            <UCheckbox
              v-model="form.approved"
              :label="form.kind === 'Backtest' ? `I approve paid model execution: ${plannedGenerations ?? 'an unknown number of'} generations plus judge calls.` : 'I approve paid judge calls on the stored outputs of the source job.'"
              description="Saving records this approval with the job. A worker runs it; the console does not call a model."
            />
          </div>

          <ApiState
            v-if="failure && failure.kind !== 'ok'"
            :result="failure"
            what="the job"
            @retry="submit"
          />
        </section>
      </template>
    </ApiState>

    <div class="flex items-center gap-2 border-t border-default pt-2">
      <UButton
        v-if="step > 0"
        size="sm"
        color="neutral"
        variant="outline"
        icon="i-lucide-arrow-left"
        label="Back"
        @click="step--"
      />
      <span class="flex-1" />
      <UButton
        v-if="step < 3"
        size="sm"
        trailing-icon="i-lucide-arrow-right"
        label="Next"
        :disabled="!stepValid[step]"
        @click="step++"
      />
      <UButton
        v-else
        size="sm"
        icon="i-lucide-shield-check"
        :label="`Save approved ${form.kind.toLowerCase()}`"
        :disabled="!stepValid.every(Boolean)"
        :loading="busy"
        @click="submit"
      />
    </div>
  </PagePanel>
</template>
