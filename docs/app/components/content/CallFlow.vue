<script setup lang="ts">
const steps = [
  { icon: 'i-lucide-file-code', title: 'Declare intent', text: 'A stable prompt ID, a template, and plain input and output types.' },
  { icon: 'i-lucide-git-branch', title: 'Build an invocation', text: 'Choose parameters and a profile. Branch without running work.' },
  { icon: 'i-lucide-workflow', title: 'Execute through MEAI', text: 'Keep your client pipeline. Capture attempts beside the provider.' },
  { icon: 'i-lucide-clipboard-check', title: 'Inspect and evaluate', text: 'Handle the outcome and delivery receipt. Grade the exact stored output.' }
]
</script>

<template>
  <ol class="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
    <li
      v-for="(step, index) in steps"
      :key="step.title"
      class="rounded-xl border border-default bg-default p-6"
    >
      <div class="mb-5 flex items-center justify-between">
        <UIcon
          :name="step.icon"
          class="size-6 text-primary"
        />
        <span class="font-mono text-sm text-muted">0{{ index + 1 }}</span>
      </div>
      <h3 class="font-semibold text-highlighted">
        {{ step.title }}
      </h3>
      <p class="mt-2 text-sm leading-6 text-muted">
        {{ step.text }}
      </p>
    </li>
  </ol>
</template>
