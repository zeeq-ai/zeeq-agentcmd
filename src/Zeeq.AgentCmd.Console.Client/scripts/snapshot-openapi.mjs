import { writeFile, rename, unlink } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'

// Generate offline from a reviewed snapshot. Refresh deliberately when the host contract changes.
const url = process.env.AGENTCMD_OPENAPI_URL ?? 'http://127.0.0.1:5180/openapi/v1.json'
const destination = fileURLToPath(new URL('../openapi.json', import.meta.url))
const temporary = `${destination}.tmp`
const response = await fetch(url)
if (!response.ok) {
  throw new Error(`OpenAPI download failed: HTTP ${response.status}`)
}
const schema = await response.json()
if (typeof schema.openapi !== 'string' || !schema.paths || typeof schema.paths !== 'object') {
  throw new Error('Response is not an OpenAPI document')
}
try {
  await writeFile(temporary, `${JSON.stringify(schema, null, 2)}\n`)
  await rename(temporary, destination)
} finally {
  await unlink(temporary).catch(() => {})
}
