/** Formatting helpers. Missing values become "unknown", never a zero or an empty string. */

export function formatTime(value: string | undefined | null): string {
  if (!value) {
    return 'unknown'
  }
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function arrayOf<TItem = unknown>(value: unknown): TItem[] {
  return Array.isArray(value) ? (value as TItem[]) : []
}

export function textOf(value: unknown): string | undefined {
  if (value === undefined || value === null) {
    return undefined
  }
  return typeof value === 'string' ? value : JSON.stringify(value)
}
