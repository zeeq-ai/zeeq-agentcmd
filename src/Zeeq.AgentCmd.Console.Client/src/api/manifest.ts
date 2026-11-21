/** Normalizes direct and registry-wrapped manifest shapes without fabricating missing fields. */
import type { BuildBody, ManifestCommand } from './types'
export function manifestCommands(body?: BuildBody): ManifestCommand[] {
  const manifest = body?.manifest ?? body
  return Array.isArray(manifest?.commands) ? manifest.commands.map(command => ({
    ...command,
    commandId: command.id ?? command.commandId,
    commandRevision: command.revision ?? command.commandRevision,
    inputSchema: command.input?.schema ?? command.inputSchema,
    outputSchema: command.output?.schema ?? command.outputSchema,
    overridableParameters: command.runtimeOverrides?.map(p => p.jsonName ?? p.name ?? 'unknown') ?? command.overridableParameters,
    systemTemplate: command.baselineTemplates?.system ?? command.systemTemplate,
    userTemplate: command.baselineTemplates?.user ?? command.userTemplate
  })) : []
}
