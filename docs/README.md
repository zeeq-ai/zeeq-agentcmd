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

# Validate copied C# examples against a freshly packed local package.
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
from the Markdown. It runs the quick start and compiles the selected context,
inspection, support, and composition setup examples. Other partial excerpts
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
