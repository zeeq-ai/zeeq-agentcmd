import { createServer } from 'node:http'
import { existsSync, readFileSync, statSync } from 'node:fs'
import { extname, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const output = resolve(fileURLToPath(new URL('../.output/public/', import.meta.url)))
const base = process.env.NUXT_APP_BASE_URL || '/zeeq-agentcmd/'
const port = Number(process.env.PORT || 4173)
const mime = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript',
  '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml',
  '.png': 'image/png', '.woff2': 'font/woff2', '.wasm': 'application/wasm',
  '.txt': 'text/plain', '.md': 'text/plain'
}

// Serve the generated artifact under the real Pages prefix. A dev server at /
// cannot detect broken absolute links or a missing prerendered search index.
createServer((request, response) => {
  const pathname = decodeURI(new URL(request.url, 'http://localhost').pathname)
  if (pathname === '/' || pathname === base.slice(0, -1)) {
    response.writeHead(302, { Location: base }).end()
    return
  }
  const file = resolve(output, pathname.slice(base.length) || 'index.html')
  if (!pathname.startsWith(base) || !file.startsWith(`${output}${sep}`)) {
    response.writeHead(404).end('Not found')
    return
  }
  const target = existsSync(file) && statSync(file).isDirectory() ? resolve(file, 'index.html') : file
  if (!existsSync(target)) {
    response.writeHead(404, { 'Content-Type': 'text/html' }).end(readFileSync(resolve(output, '404.html')))
    return
  }
  response.writeHead(200, { 'Content-Type': mime[extname(target)] || 'application/octet-stream' })
  response.end(readFileSync(target))
}).listen(port, '127.0.0.1', () => {
  console.log(`Static Pages preview: http://127.0.0.1:${port}${base}`)
})
