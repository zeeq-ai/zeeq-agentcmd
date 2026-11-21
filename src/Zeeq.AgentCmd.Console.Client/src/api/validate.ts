/**
 * Client-side checks for authored bodies, run before a save.
 *
 * They catch shape mistakes early with a readable message. They do not replace server validation:
 * the server stays the authority, and its problem details are shown as they come back.
 */
import { isRecord } from '../composables/format'
import type { EditableCollection } from './endpoints'

export interface BodyCheck {
  errors: string[]
  warnings: string[]
}

function requireString(errors: string[], value: unknown, where: string): void {
  if (typeof value !== 'string' || !value.trim()) {
    errors.push(`${where} must be a nonblank string.`)
  }
}

function checkPublication(body: Record<string, unknown>, result: BodyCheck): void {
  requireString(result.errors, body.buildId, 'buildId')
  if (!Array.isArray(body.commands) || body.commands.length === 0) {
    result.errors.push('commands must list at least one command.')
    return
  }

  body.commands.forEach((command: unknown, index) => {
    const at = `commands[${index}]`
    if (!isRecord(command)) {
      result.errors.push(`${at} must be an object.`)
      return
    }
    requireString(result.errors, command.commandId, `${at}.commandId`)
    if (typeof command.commandRevision !== 'number') {
      result.errors.push(`${at}.commandRevision must be a number.`)
    }
    requireString(result.errors, command.contractSignature, `${at}.contractSignature`)
    requireString(result.errors, command.experimentId, `${at}.experimentId`)
    requireString(result.errors, command.controlVariantId, `${at}.controlVariantId`)

    const variants = Array.isArray(command.variants) ? command.variants : []
    if (variants.length === 0) {
      result.errors.push(`${at}.variants must list at least one variant.`)
    }

    const ids = new Set<string>()
    let weight = 0
    variants.forEach((variant: unknown, variantIndex) => {
      const vat = `${at}.variants[${variantIndex}]`
      if (!isRecord(variant)) {
        result.errors.push(`${vat} must be an object.`)
        return
      }
      requireString(result.errors, variant.id, `${vat}.id`)
      if (typeof variant.id === 'string') {
        if (ids.has(variant.id)) {
          result.errors.push(`${vat}.id "${variant.id}" is duplicated.`)
        }
        ids.add(variant.id)
      }
      if (!Number.isInteger(variant.weight) || Number(variant.weight) < 0 || Number(variant.weight) > 2147483647) {
        result.errors.push(`${vat}.weight must be a nonnegative 32-bit integer.`)
      } else {
        weight += Number(variant.weight)
      }
      if (variant.systemMode !== undefined && !['inherit', 'remove', 'replace'].includes(String(variant.systemMode))) {
        result.errors.push(`${vat}.systemMode must be inherit, remove or replace.`)
      }
      if (variant.systemTemplate !== undefined && variant.systemTemplate !== null && (typeof variant.systemTemplate !== 'string' || !variant.systemTemplate.length)) {
        result.errors.push(`${vat}.systemTemplate must be a nonempty string, null, or omitted.`)
      }
      if (variant.systemMode === 'replace' && (typeof variant.systemTemplate !== 'string' || !variant.systemTemplate.length)) {
        result.errors.push(`${vat}.systemMode replace requires a nonempty systemTemplate.`)
      }
      if (variant.systemMode === 'inherit' && variant.systemTemplate !== undefined) {
        result.errors.push(`${vat}.systemMode inherit requires systemTemplate to be omitted.`)
      }
      if (variant.systemMode === 'remove' && variant.systemTemplate !== undefined && variant.systemTemplate !== null) {
        result.errors.push(`${vat}.systemMode remove cannot include a replacement template.`)
      }
      if (variant.profile != null) {
        result.errors.push(`${vat}.profile is not supported by this editor; remove it and specify the full route when needed.`)
      }
      if (variant.route != null) {
        const route = isRecord(variant.route) ? variant.route : undefined
        if (!route) { result.errors.push(`${vat}.route must be an object.`) }
        else {
          requireString(result.errors, isRecord(route.key) ? route.key.id : undefined, `${vat}.route.key.id`)
          requireString(result.errors, route.provider, `${vat}.route.provider`)
          requireString(result.errors, route.model, `${vat}.route.model`)
          if (route.temperature != null && (typeof route.temperature !== 'number' || !Number.isFinite(route.temperature))) { result.errors.push(`${vat}.route.temperature must be a finite number.`) }
          if (route.defaults != null) {
            if (!isRecord(route.defaults)) { result.errors.push(`${vat}.route.defaults must be an object.`) }
            else {
              const d = route.defaults
              for (const key of ['temperature', 'topP']) {
                if (d[key] != null && (typeof d[key] !== 'number' || !Number.isFinite(d[key]))) { result.errors.push(`${vat}.route.defaults.${key} must be a finite number.`) }
              }
              for (const key of ['maxOutputTokens', 'topK']) {
                if (d[key] != null && (!Number.isInteger(d[key]) || Number(d[key]) <= 0 || Number(d[key]) > 2147483647)) { result.errors.push(`${vat}.route.defaults.${key} must be a positive 32-bit integer.`) }
              }
              for (const key of ['reasoningEffort', 'reasoningOutput']) {
                if (d[key] != null && typeof d[key] !== 'string') { result.errors.push(`${vat}.route.defaults.${key} must be a string.`) }
              }
            }
          }
        }
      }
      // The runtime rejects a blank user template; catch it before publication review.
      requireString(result.errors, variant.userTemplate, `${vat}.userTemplate`)
      if (!isRecord(variant.parameters)) {
        result.errors.push(`${vat}.parameters must be an object (use {} for none).`)
      }
    })

    if (typeof command.controlVariantId === 'string' && variants.length > 0 && !ids.has(command.controlVariantId)) {
      result.errors.push(`${at}.controlVariantId "${command.controlVariantId}" is not one of the variants.`)
    }
    if (variants.length > 0 && weight <= 0) {
      result.errors.push(`${at}: total variant weight must be greater than zero.`)
    }
  })
}

function checkDataset(body: Record<string, unknown>, result: BodyCheck): void {
  if (!['Regression', 'Calibration', 'Acceptance'].includes(String(body.purpose))) {
    result.errors.push('purpose must be Regression, Calibration or Acceptance.')
  }
  if (!Array.isArray(body.cases)) {
    result.errors.push('cases must be an array.')
    return
  }
  if (body.cases.length === 0) {
    result.warnings.push('The dataset has no cases. It cannot support a useful benchmark.')
  }

  const ids = new Set<string>()
  body.cases.forEach((item: unknown, index) => {
    const at = `cases[${index}]`
    if (!isRecord(item)) {
      result.errors.push(`${at} must be an object.`)
      return
    }
    requireString(result.errors, item.id, `${at}.id`)
    if (typeof item.id === 'string') {
      if (ids.has(item.id)) {
        result.errors.push(`${at}.id "${item.id}" is duplicated.`)
      }
      ids.add(item.id)
    }
    requireString(result.errors, item.sourceFamilyId, `${at}.sourceFamilyId`)
    if (!isRecord(item.input)) {
      result.errors.push(`${at}.input must be an object.`)
    }
    if (!Array.isArray(item.assets)) {
      result.errors.push(`${at}.assets must be an array.`)
    }
    if (!Array.isArray(item.tags)) {
      result.errors.push(`${at}.tags must be an array.`)
    }
    if (typeof item.exposed !== 'boolean') {
      result.errors.push(`${at}.exposed must be true or false.`)
    }
    if (item.reference !== undefined) {
      if (!isRecord(item.reference) || !['Partial', 'Exhaustive', 'None'].includes(String(item.reference.completeness))) {
        result.errors.push(`${at}.reference.completeness must be Partial, Exhaustive or None.`)
      }
    }
  })
}

function checkBenchmark(body: Record<string, unknown>, result: BodyCheck): void {
  requireString(result.errors, body.datasetId, 'datasetId')
  if (!Array.isArray(body.criteria) || body.criteria.length === 0) {
    result.errors.push('criteria must list at least one criterion.')
    return
  }

  const ids = new Set<string>()
  body.criteria.forEach((criterion: unknown, index) => {
    const at = `criteria[${index}]`
    if (!isRecord(criterion)) {
      result.errors.push(`${at} must be an object.`)
      return
    }
    requireString(result.errors, criterion.id, `${at}.id`)
    if (typeof criterion.id === 'string') {
      if (ids.has(criterion.id)) {
        result.errors.push(`${at}.id "${criterion.id}" is duplicated.`)
      }
      ids.add(criterion.id)
    }
    requireString(result.errors, criterion.name, `${at}.name`)
    if (!Number.isInteger(criterion.version) || Number(criterion.version) < 1) {
      result.errors.push(`${at}.version must be a positive integer.`)
    }
    for (const binding of ['outputBinding', 'referenceBinding', 'sourceBinding']) {
      const value = criterion[binding]
      if (value !== undefined && (typeof value !== 'string' || (value !== '' && (!value.startsWith('/') || /~(?![01])/.test(value))))) {
        result.errors.push(`${at}.${binding} must be a JSON Pointer (empty or starting with /, with ~0 and ~1 escapes).`)
      }
    }
    if (!['Field', 'Subtree', 'Collection', 'Whole'].includes(String(criterion.scope))) {
      result.errors.push(`${at}.scope must be Field, Subtree, Collection or Whole.`)
    }
    // A binding is a JSON pointer; "" legitimately addresses the whole output.
    if (typeof criterion.outputBinding !== 'string' || criterion.outputBinding.includes('*')) {
      result.errors.push(`${at}.outputBinding must be a concrete JSON pointer string without wildcards ("" is the whole output).`)
    }

    if (criterion.scope === 'Collection') {
      const m = isRecord(criterion.matching) ? criterion.matching : {}
      if (!['Preserve', 'Ignore'].includes(String(m.order)) || !['Count', 'Collapse'].includes(String(m.duplicates)) || !['Item', 'AtomicFact'].includes(String(m.unit)) || typeof m.paraphrases !== 'boolean') {
        result.errors.push(`${at}.matching requires explicit order, duplicates, unit and paraphrases.`)
      }
      if (m.unit === 'AtomicFact') { requireString(result.errors, m.decompositionConfigId, `${at}.matching.decompositionConfigId`) }
    }
    const scorer = isRecord(criterion.scorer) ? criterion.scorer : undefined
    if (!scorer || !['Human', 'Exact', 'Numeric', 'Llm'].includes(String(scorer.kind))) {
      result.errors.push(`${at}.scorer.kind must be Human, Exact, Numeric or Llm.`)
    } else {
      requireString(result.errors, scorer.version, `${at}.scorer.version`)
      if (scorer.kind === 'Numeric' && (typeof scorer.tolerance !== 'number' || !Number.isFinite(scorer.tolerance) || scorer.tolerance < 0)) {
        result.errors.push(`${at}.scorer.tolerance is required for a Numeric scorer.`)
      }
      if (scorer.kind === 'Llm' && (typeof scorer.judgeConfigId !== 'string' || !scorer.judgeConfigId)) {
        result.errors.push(`${at}.scorer.judgeConfigId is required for an Llm scorer.`)
      }
      if ((scorer.kind === 'Exact' || scorer.kind === 'Numeric') && typeof criterion.referenceBinding !== 'string') {
        result.warnings.push(`${at}: ${String(scorer.kind)} scoring usually needs a referenceBinding.`)
      }
    }
  })

  if (typeof body.primaryCriterionId !== 'string' || !ids.has(body.primaryCriterionId)) {
    result.errors.push('primaryCriterionId must name exactly one of the criteria.')
  }
  if (!Number.isInteger(body.repeats) || Number(body.repeats) < 1 || Number(body.repeats) > 100) {
    result.errors.push('repeats must be an integer from 1 to 100.')
  }
  if (!Array.isArray(body.guardrails)) {
    result.errors.push('guardrails must be an array (use [] for none).')
  } else {
    const guardIds = new Set<string>()
    body.guardrails.forEach((rule: unknown, index: number) => {
      const at = `guardrails[${index}]`
      if (!isRecord(rule)) { result.errors.push(`${at} must be an object.`); return }
      requireString(result.errors, rule.id, `${at}.id`)
      requireString(result.errors, rule.metric, `${at}.metric`)
      if (typeof rule.id === 'string') { if (guardIds.has(rule.id)) { result.errors.push(`${at}.id is duplicated.`) }; guardIds.add(rule.id) }
      if (!['Gte', 'Lte', 'Eq'].includes(String(rule.operator))) { result.errors.push(`${at}.operator must be Gte, Lte or Eq.`) }
      if (typeof rule.threshold !== 'number' || !Number.isFinite(rule.threshold)) { result.errors.push(`${at}.threshold must be finite.`) }
      if (typeof rule.minimumCoverage !== 'number' || rule.minimumCoverage < 0 || rule.minimumCoverage > 1 || !Number.isFinite(rule.minimumCoverage)) { result.errors.push(`${at}.minimumCoverage must be between 0 and 1.`) }
      if (typeof rule.required !== 'boolean') { result.errors.push(`${at}.required must be true or false.`) }
      if (typeof rule.metric === 'string' && !['ValidOutputRate', 'UnsupportedAdditionRate', 'KnownCostPerPricedCase', 'CaseElapsedP95'].includes(rule.metric) && !(rule.metric.startsWith('criterion:') && ids.has(rule.metric.slice(10)))) { result.warnings.push(`${at}.metric is not a known metric or criterion; its evaluation is unknown.`) }
    })
  }
}

/** Checks an authored body for one editable collection. */
export function checkBody(collection: EditableCollection, body: unknown): BodyCheck {
  const result: BodyCheck = { errors: [], warnings: [] }
  if (!isRecord(body)) {
    result.errors.push('The body must be a JSON object.')
    return result
  }

  switch (collection) {
    case 'publications':
      checkPublication(body, result)
      break
    case 'datasets':
      checkDataset(body, result)
      break
    case 'benchmarks':
      checkBenchmark(body, result)
      break
  }
  return result
}

/** Starting bodies for a new draft. They are templates to edit, not defaults the server applies. */
export const bodyTemplates: Record<EditableCollection, unknown> = {
  publications: {
    buildId: '',
    commands: [
      {
        commandId: '',
        commandRevision: 1,
        contractSignature: '',
        experimentId: '',
        controlVariantId: 'control',
        variants: [{ id: 'control', weight: 1, userTemplate: '', parameters: {} }]
      }
    ]
  },
  datasets: {
    purpose: 'Regression',
    cases: [
      {
        id: 'case-1',
        sourceFamilyId: 'family-1',
        input: {},
        assets: [],
        tags: [],
        reference: { value: {}, completeness: 'Partial' },
        exposed: false
      }
    ]
  },
  benchmarks: {
    datasetId: '',
    criteria: [
      {
        id: 'whole',
        version: 1,
        name: 'Whole output',
        scope: 'Whole',
        outputBinding: '',
        scorer: { kind: 'Human', version: '1' }
      }
    ],
    primaryCriterionId: 'whole',
    repeats: 1,
    guardrails: []
  }
}
