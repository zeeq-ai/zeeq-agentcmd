<script setup lang="ts">
/** Edits explicit criterion bindings while retaining any advanced server-owned properties. */
import { computed } from 'vue'
import type { BenchmarkBody, CriterionBody } from '../api/types'
const props = defineProps<{ modelValue: unknown; disabled: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: unknown] }>()
const body = computed(() => props.modelValue as BenchmarkBody)
const valid = computed(() => body.value && typeof body.value === 'object' && Array.isArray(body.value.criteria) && Array.isArray(body.value.guardrails) && body.value.guardrails.every(g => g && typeof g === 'object') && body.value.criteria.every(c => c && typeof c === 'object' && c.scorer && typeof c.scorer === 'object'))
function update(mutator: (draft: BenchmarkBody) => void): void {
  if (!valid.value || props.disabled) { return }
  const draft = JSON.parse(JSON.stringify(body.value)) as BenchmarkBody
  mutator(draft)
  emit('update:modelValue', draft)
}
function field(index: number, key: keyof CriterionBody, value: string): void {
  update((draft) => { (draft.criteria[index] as unknown as Record<string, unknown>)[key] = value })
}
function scorer(index: number, key: string, value: string): void {
  update((draft) => { (draft.criteria[index].scorer as unknown as Record<string, unknown>)[key] = key === 'tolerance' ? (value === '' ? undefined : Number(value)) : value })
}
function matching(index: number, key: string, value: string | boolean): void {
  update(d => { const c = d.criteria[index]; c.matching ??= {} as NonNullable<CriterionBody['matching']>; (c.matching as unknown as Record<string, unknown>)[key] = value })
}
function guard(index: number, key: string, value: string | boolean): void {
  update(d => { (d.guardrails[index] as unknown as Record<string, unknown>)[key] = ['threshold', 'minimumCoverage'].includes(key) ? Number(value) : value })
}
function add(): void {
  update((draft) => { draft.criteria.push({ id: '', version: 1, name: '', scope: 'Field', outputBinding: '', scorer: { kind: 'Human', version: '1' } }) })
}
</script>
<template>
  <div
    v-if="valid"
    class="space-y-3"
  >
    <div class="grid gap-2 md:grid-cols-3">
      <UFormField
        label="Dataset ID"
        required
      >
        <UInput
          :model-value="body.datasetId"
          :disabled="disabled"
          class="w-full"
          @update:model-value="update(d => { d.datasetId = String($event) })"
        />
      </UFormField>
      <UFormField
        label="Repeats"
        required
      >
        <UInput
          type="number"
          min="1"
          :model-value="body.repeats"
          :disabled="disabled"
          @update:model-value="update(d => { d.repeats = Number($event) })"
        />
      </UFormField>
      <UFormField
        label="Primary criterion"
        required
      >
        <USelect
          :model-value="body.primaryCriterionId"
          :items="body.criteria.map(c => c.id)"
          :disabled="disabled"
          @update:model-value="update(d => { d.primaryCriterionId = String($event) })"
        />
      </UFormField>
    </div>
    <div
      v-for="(criterion, index) in body.criteria"
      :key="index"
      class="space-y-2 rounded border border-default p-3"
    >
      <div class="flex items-center gap-2">
        <strong>Criterion {{ index + 1 }}</strong><UBadge
          v-if="criterion.id && criterion.id === body.primaryCriterionId"
          label="Primary"
        /><span class="flex-1" /><UButton
          label="Remove"
          color="neutral"
          variant="ghost"
          size="xs"
          :disabled="disabled"
          @click="update(d => { d.criteria.splice(index, 1) })"
        />
      </div>
      <div class="grid gap-2 md:grid-cols-3">
        <UFormField
          label="ID"
          required
        >
          <UInput
            :model-value="criterion.id"
            :disabled="disabled"
            @update:model-value="field(index, 'id', String($event))"
          />
        </UFormField>
        <UFormField label="Criterion version">
          <UInput
            type="number"
            min="1"
            :model-value="criterion.version"
            :disabled="disabled"
            @update:model-value="update(d => { d.criteria[index].version = Number($event) })"
          />
        </UFormField>
        <UFormField
          label="Name"
          required
        >
          <UInput
            :model-value="criterion.name"
            :disabled="disabled"
            @update:model-value="field(index, 'name', String($event))"
          />
        </UFormField>
        <UFormField label="Scope">
          <USelect
            :model-value="criterion.scope"
            :items="['Field', 'Subtree', 'Collection', 'Whole']"
            :disabled="disabled"
            @update:model-value="field(index, 'scope', String($event))"
          />
        </UFormField>
        <UFormField label="Output JSON Pointer">
          <UInput
            :model-value="criterion.outputBinding"
            :disabled="disabled"
            placeholder="Empty = whole"
            @update:model-value="field(index, 'outputBinding', String($event))"
          />
        </UFormField>
        <UFormField label="Reference JSON Pointer">
          <UInput
            :model-value="criterion.referenceBinding ?? ''"
            :disabled="disabled"
            @update:model-value="field(index, 'referenceBinding', String($event))"
          />
        </UFormField>
        <UFormField label="Source JSON Pointer">
          <UInput
            :model-value="criterion.sourceBinding ?? ''"
            :disabled="disabled"
            @update:model-value="field(index, 'sourceBinding', String($event))"
          />
        </UFormField>
        <UFormField label="Scorer">
          <USelect
            :model-value="criterion.scorer?.kind"
            :items="['Human', 'Exact', 'Numeric', 'Llm']"
            :disabled="disabled"
            @update:model-value="scorer(index, 'kind', String($event))"
          />
        </UFormField>
        <UFormField label="Scorer version">
          <UInput
            :model-value="criterion.scorer?.version"
            :disabled="disabled"
            @update:model-value="scorer(index, 'version', String($event))"
          />
        </UFormField>
        <UFormField
          v-if="criterion.scorer?.kind === 'Numeric'"
          label="Tolerance"
        >
          <UInput
            type="number"
            :model-value="criterion.scorer.tolerance"
            :disabled="disabled"
            @update:model-value="scorer(index, 'tolerance', String($event))"
          />
        </UFormField>
        <UFormField
          v-if="criterion.scorer?.kind === 'Llm'"
          label="Judge config ID"
        >
          <UInput
            :model-value="criterion.scorer.judgeConfigId"
            :disabled="disabled"
            @update:model-value="scorer(index, 'judgeConfigId', String($event))"
          />
        </UFormField>
      </div>
      <div
        v-if="criterion.scope === 'Collection'"
        class="grid gap-2 md:grid-cols-3"
      >
        <UFormField label="Order">
          <USelect
            :model-value="criterion.matching?.order"
            :items="['Preserve', 'Ignore']"
            :disabled="disabled"
            placeholder="Select order"
            @update:model-value="matching(index, 'order', String($event))"
          />
        </UFormField>
        <UFormField label="Duplicates">
          <USelect
            :model-value="criterion.matching?.duplicates"
            :items="['Count', 'Collapse']"
            :disabled="disabled"
            placeholder="Select duplicates"
            @update:model-value="matching(index, 'duplicates', String($event))"
          />
        </UFormField>
        <UFormField label="Match unit">
          <USelect
            :model-value="criterion.matching?.unit"
            :items="['Item', 'AtomicFact']"
            :disabled="disabled"
            placeholder="Select unit"
            @update:model-value="matching(index, 'unit', String($event))"
          />
        </UFormField>
        <UFormField label="Paraphrases">
          <USelect
            :model-value="criterion.matching?.paraphrases === undefined ? undefined : String(criterion.matching.paraphrases)"
            :items="[{label: 'Allow', value: 'true'}, {label: 'Do not allow', value: 'false'}]"
            placeholder="Select policy"
            :disabled="disabled"
            @update:model-value="matching(index, 'paraphrases', $event === 'true')"
          />
        </UFormField>
        <UFormField
          v-if="criterion.matching?.unit === 'AtomicFact'"
          label="Decomposition config ID"
        >
          <UInput
            :model-value="criterion.matching?.decompositionConfigId ?? ''"
            :disabled="disabled"
            @update:model-value="matching(index, 'decompositionConfigId', String($event))"
          />
        </UFormField>
      </div>
    </div>
    <UButton
      label="Add criterion"
      icon="i-lucide-plus"
      size="xs"
      :disabled="disabled"
      @click="add"
    />
    <h3 class="font-semibold">
      Independent guardrails
    </h3>
    <p class="text-xs text-muted">
      Unknown evidence does not pass. Only supported metrics can be evaluated.
    </p>
    <div
      v-for="(rule, index) in body.guardrails"
      :key="index"
      class="grid gap-2 rounded border border-default p-2 md:grid-cols-4"
    >
      <UFormField label="ID">
        <UInput
          :model-value="rule.id"
          :disabled="disabled"
          @update:model-value="guard(index, 'id', String($event))"
        />
      </UFormField>
      <UFormField label="Metric">
        <UInput
          :model-value="rule.metric"
          :disabled="disabled"
          placeholder="criterion:id"
          @update:model-value="guard(index, 'metric', String($event))"
        />
      </UFormField>
      <UFormField label="Operator">
        <USelect
          :model-value="rule.operator"
          :items="['Gte', 'Lte', 'Eq']"
          :disabled="disabled"
          @update:model-value="guard(index, 'operator', String($event))"
        />
      </UFormField>
      <UFormField label="Threshold">
        <UInput
          type="number"
          :model-value="rule.threshold"
          :disabled="disabled"
          @update:model-value="guard(index, 'threshold', String($event))"
        />
      </UFormField>
      <UFormField label="Minimum coverage (0–1)">
        <UInput
          type="number"
          min="0"
          max="1"
          step="0.01"
          :model-value="rule.minimumCoverage"
          :disabled="disabled"
          @update:model-value="guard(index, 'minimumCoverage', String($event))"
        />
      </UFormField>
      <UCheckbox
        :model-value="rule.required"
        label="Required"
        :disabled="disabled"
        @update:model-value="guard(index, 'required', Boolean($event))"
      />
      <UButton
        label="Remove guardrail"
        color="neutral"
        variant="ghost"
        size="xs"
        :disabled="disabled"
        @click="update(d => { d.guardrails.splice(index, 1) })"
      />
    </div>
    <UButton
      label="Add guardrail"
      size="xs"
      :disabled="disabled"
      @click="update(d => { d.guardrails.push({ id: '', metric: '', operator: 'Gte', threshold: 0, minimumCoverage: 1, required: true }) })"
    />
  </div>
  <p
    v-else
    class="text-warning"
  >
    Correct the benchmark JSON shape before using the structured editor.
  </p>
</template>
