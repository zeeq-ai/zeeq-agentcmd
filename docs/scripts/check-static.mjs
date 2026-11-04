import assert from 'node:assert/strict'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = new URL('../', import.meta.url)
const output = new URL('.output/public/', root)
const base = process.env.NUXT_APP_BASE_URL || '/zeeq-agentcmd/'
const site = process.env.NUXT_PUBLIC_SITE_URL || 'https://zeeq-ai.github.io'

function routes(dir, prefix = '') {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const name = entry.name.replace(/^\d+\./, '')
    if (entry.isDirectory()) {
      return routes(join(dir, entry.name), `${prefix}/${name}`)
    }
    if (!name.endsWith('.md') || (!prefix && name === 'index.md')) {
      return []
    }
    return [name === 'index.md' ? prefix : `${prefix}/${name.slice(0, -3)}`]
  })
}

const paths = ['/', ...routes(fileURLToPath(new URL('content/', root)))]
for (const path of paths) {
  const htmlPath = new URL(`${path.slice(1)}${path === '/' ? '' : '/'}index.html`, output)
  assert.ok(existsSync(htmlPath), `Missing HTML: ${path}`)
  const html = readFileSync(htmlPath, 'utf8')
  assert.match(html, /<h1\b/, `Missing page heading: ${path}`)
  assert.ok(!html.includes('Page not found'), `Error page rendered: ${path}`)
  assert.ok(html.includes(`${site}${base.replace(/\/$/, '')}${path}`), `Missing canonical URL: ${path}`)

  // A root-relative URL without the repository prefix works locally but fails
  // on Pages. Check the emitted document, not only the authored Markdown.
  for (const [, href] of html.matchAll(/(?:href|src)="(\/[^"#?]*)/g)) {
    if (href.startsWith('//')) {
      continue
    }
    assert.ok(href.startsWith(base), `URL escapes deployment prefix on ${path}: ${href}`)
    const target = decodeURI(href.slice(base.length)).replaceAll('&amp;', '&')
    assert.ok(
      existsSync(new URL(target || 'index.html', output)) || existsSync(new URL(`${target.replace(/\/$/, '')}/index.html`, output)),
      `Missing static target on ${path}: ${href}`
    )
  }

  if (path !== '/') {
    const raw = new URL(`raw${path}.md`, output)
    assert.ok(existsSync(raw), `Missing Markdown: ${path}`)
    assert.match(readFileSync(raw, 'utf8'), /^# /, `Markdown has no title: ${path}`)
  }
}

for (const file of ['.nojekyll', '404.html', 'favicon.svg', 'llms.txt', 'llms-full.txt']) {
  assert.ok(existsSync(new URL(file, output)), `Missing deployment asset: ${file}`)
}
const llms = readFileSync(new URL('llms.txt', output), 'utf8')
assert.ok(llms.includes(`${site}${base}raw/getting-started/quick-start.md`), 'LLM index has no deployed quick-start URL')
console.log(`Static site verified: ${paths.length} HTML pages, ${paths.length - 1} Markdown twins, deployment prefix ${base}`)
