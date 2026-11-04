/// <reference types="node" />
import { readdirSync } from 'node:fs'
import { join } from 'node:path'

const siteUrl = process.env.NUXT_PUBLIC_SITE_URL || 'https://zeeq-ai.github.io'
const baseURL = process.env.NUXT_APP_BASE_URL || '/zeeq-llmcmd/'

// Pages has no application server. Emit both HTML and Markdown for every route
// so direct links, search, and the copy controls work after static deployment.
function contentRoutes(dir = join(import.meta.dirname, 'content'), prefix = ''): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const name = entry.name.replace(/^\d+\./, '')
    if (entry.isDirectory()) {
      return contentRoutes(join(dir, entry.name), `${prefix}/${name}`)
    }
    if (!name.endsWith('.md') || (prefix === '' && name === 'index.md')) {
      return []
    }
    const slug = name.replace(/\.md$/, '')
    const path = slug === 'index' ? prefix : `${prefix}/${slug}`
    return [path, `/raw${path}.md`]
  })
}

export default defineNuxtConfig({
  modules: ['@nuxt/eslint', '@nuxt/ui', '@nuxt/content', 'nuxt-llms'],

  $development: {
    app: { baseURL: process.env.NUXT_APP_BASE_URL || '/' }
  },

  devtools: { enabled: false },
  app: { baseURL },
  css: ['~/assets/css/main.css'],

  content: {
    build: {
      markdown: {
        toc: { searchDepth: 1 },
        highlight: {
          theme: { default: 'github-light', light: 'github-light', dark: 'github-dark' },
          langs: ['csharp', 'bash', 'json', 'yaml', 'xml', 'ts', 'vue', 'md']
        }
      }
    },
    experimental: { sqliteConnector: 'native' }
  },
  runtimeConfig: { public: { siteUrl } },

  compatibilityDate: '2026-06-30',

  nitro: {
    prerender: { routes: ['/', ...contentRoutes()], crawlLinks: true, failOnError: true }
  },

  eslint: {
    config: {
      stylistic: { commaDangle: 'never', braceStyle: '1tbs' }
    }
  },

  llms: {
    domain: `${siteUrl}${baseURL.replace(/\/$/, '')}`,
    title: 'Zeeq AgentCmd',
    description: 'Typed prompt commands over Microsoft.Extensions.AI. Stable identity, captured parameters, closed outcomes, and evaluation of the exact stored whole.',
    full: {
      title: 'Zeeq AgentCmd — Full documentation',
      description: 'The local alpha documentation for AgentCmd on .NET 11 / C# 15 RC.'
    },
    sections: ([
      ['Getting started', '/getting-started%'],
      ['Guides', '/guides%'],
      ['Examples', '/examples%'],
      ['Reference', '/reference%']
    ] as const).map(([title, path]) => ({
      title,
      contentCollection: 'docs',
      contentFilters: [{ field: 'path', operator: 'LIKE', value: path }]
    }))
  }
})
