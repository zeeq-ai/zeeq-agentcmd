import { defineConfig } from 'kubb'
import { pluginTs } from '@kubb/plugin-ts'
import { pluginFetch } from '@kubb/plugin-fetch'
import ts from 'typescript'

export default defineConfig({
  input: './openapi.json',
  output: { path: './src/generated', clean: true },
  plugins: [pluginTs({ output: { path: './types', mode: 'directory' }, printer: { nodes: { bigint: () => ts.factory.createKeywordTypeNode(ts.SyntaxKind.NumberKeyword) } } }), pluginFetch({ output: { path: './clients', mode: 'directory' } })]
})
