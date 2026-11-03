# LlmCmd documentation

Nuxt UI and Nuxt Content, with the same navigation and reading layout as
`zeeq-biome`. Content lives in `content/`. Existing architecture and research
notes remain source material outside the published content collection.

```sh
# From the repository root.
mise install
mise run docs:install    # Restore the pinned lockfile.
mise run docs:dev        # Development server; serves from /.
mise run docs:check      # ESLint and TypeScript.
mise run docs:build      # Static Pages artifact and route/link checks.
mise exec -- pnpm --dir docs preview # Preview the artifact at /zeeq-llmcmd/.

# Validate copied C# examples against freshly packed local packages.
mise run pack
mise run docs:examples
```

The release build uses `/zeeq-llmcmd/`. Set `NUXT_APP_BASE_URL=/` to generate for
a domain root. `NUXT_PUBLIC_SITE_URL` controls canonical and Markdown URLs.
Both variables must be set consistently when running `docs:build`.

## Content approach

Start with a concrete operation. Explain the reason for a design choice, show
the code, then show the result a reader can inspect. Credit MEAI's existing
typed-response and middleware APIs. Use LlmCmd's identity, capture, delivery,
composition, and whole-output evaluation to explain why to add a command layer.

The getting-started example runs with a fake client. Provider setup excerpts
state their prerequisites. Illustrative scenarios are labeled. NativeAOT and
alpha limits remain explicit. Do not imply that local packages are on NuGet.org.

`scripts/verify-docs-examples.cs` extracts the exact named C# and project fences
from the Markdown. It runs the quick start and offline telemetry/evaluation
example against local packages. It also compiles the selected context,
inspection, support, composition setup and telemetry exporter examples. The
exporter is compiled but does not contact a collector. Other partial excerpts
are not a substitute for the runnable examples under `examples/`.

## GitHub Pages

`.github/workflows/docs.yml` checks and generates the site on pull requests. It
also packs the library and validates the documented C# fences without paid calls.
Pushes to `main` and manual runs deploy the validated artifact to GitHub Pages.
The repository's Pages source must be **GitHub Actions**.

The artifact includes HTML routes, raw Markdown twins, local search data,
`llms.txt`, `llms-full.txt`, and `.nojekyll`. It needs no runtime server or
secret. `scripts/check-static.mjs` checks the generated routes and repository
prefix before upload.

## Dependency audit

Run `mise exec -- pnpm --dir docs audit --prod` to review the locked graph.
As of 2026-11-03, it reports five advisories: one high, two moderate, and two
low. The high advisory, [GHSA-mrhx-c7xc-m3w5](https://github.com/advisories/GHSA-mrhx-c7xc-m3w5),
affects Zod versions before 4.6.6. Nuxt Content 3.16.2 requires Zod 3.25.76,
so forcing the fixed major would cross its declared dependency range. Update
Nuxt Content when it supports the fix, then remove this note after verification.

The deployed site is static. An audit of the package graph alone does not
establish which dependencies run in the browser or whether an advisory is
reachable in this site. The `unctx` package extension in `pnpm-workspace.yaml`
supplies compatible peer dependencies for the currently locked version.
