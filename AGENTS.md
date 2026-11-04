# Zeeq AgentCmd

## Overview

`AgentCmd` is modeled after `SqlCommand`: typed commands encapsulate string-based prompts, parameters, execution, and results.  Key objective is to make it easier to surface parts of the command like the raw prompt, the parameters, etc. to allow emitters to store parts for eval calculation and surfacing of metrics in production.

- Keep call sites terse and fluent: `WithParameters(...).ExecuteAsync()`.
- Generate boilerplate. Use `WithHeaderFrom(...)` and typed dependencies to compose a whole.
- Use modern C# 15 / .NET 11 RC; `global.json` pins the SDK.
- Explain **why** in adjacent C# or XML comments: authority, replay, serialization, accounting, and scope lifetime.
- Document every public production API with XML comments. `CS1591` and malformed parameter docs fail the build; generated-file exemptions must not hide authored API gaps. See [quality floor](docs/architecture/quality-floor.md).
- Use CSharpier through mise. Re-read after formatting; code and line numbers move. The exact C# 15 exceptions use the SDK formatter via `scripts/format.sh`.
- Use **TUnit**, not xUnit. Paid, secret-dependent, and container tests must be `[Explicit]`. Live API calls also require the opt-in gate before loading secrets.
- Grade the exact stored aggregate when evaluating the whole. Never substitute a component's execution ID.
- Never inspect or print API keys. Keep sanitized evidence in ignored `artifacts/`.

## Best Practices

- Source formatting hygiene with CSharpier
- Idiomatic C# XML comments focus on "why", not "how"; long term memory bank that is infrastructureless and helps future travelers orient
- Classes should have comments `<summary></summary>` that describe the purpose, intended usage, reponsibilities; other agents read start of file so this helps guide
- Inline comments sparingly for specific callouts
- *ALWAYS* brace `{ }`, even single line statements
- Use newlines liberally to separate code and related statements for legibility
- Break large classes into `partial` classes when necessary to help organize related logic
- Generic types use descriptive names `TSomething` instead of `TS`.

## Tooling

- [mise llms.txt](https://mise.jdx.dev/llms.txt): pinned tools and tasks.
- [Aspire llms.txt](https://aspire.dev/llms.txt): explicit development resources.
- [TUnit llms.txt](https://tunit.dev/llms.txt): tests and explicit selection.

## MEAI and Design Objectives

- Extend **Microsoft.Extensions.AI (MEAI)** without replacing it. Accept standard `IChatClient` clients and preserve `ChatOptions`, responses, cancellation, and client ownership.
- Compose behaviors through `ChatClientBuilder` and `DelegatingChatClient`. Preserve host middleware for retry, caching, tools, and telemetry. Place per-attempt capture next to the provider, inside retry/fallback/tool loops.
- Keep the command layer provider-neutral. Add future Bedrock, DeepSeek, Fireworks, or other providers through adapters and optional SDK bridges, not core forks or vendor-specific fluent APIs.
- Keep seams independent: `IProviderResponseAdapter` parses protocol accounting; `INativeResponseProjector` bridges an SDK response; `IExtractionSink` consumes normalized snapshots. Pricing and observability consume those snapshots, not SDK graphs.
- Keep platform emitters optional. A provider must not know about Langfuse or another sink. Avoid a separate implementation for every provider/platform pair.
- Use typed hooks for defaults and validation; MEAI middleware for execution behaviors; `IHumanFeedbackLedger` and `ICompositionStore` for storage. Preserve extension boundaries when adding capabilities.
- Keep setup small, DI-friendly, and explicit. Generate contracts, schemas, rendering, and registration; keep stable prompt IDs and authored intent visible.
- Keep leaf invocations immutable. Root registration is deferred; only `ExecuteAsync` runs work. Support ordinary branches and typed pinned dependencies.
- Capture context once. Explicit context replaces ambient context as a whole. Correlation is not authority, membership, or a persistence guarantee.
- Preserve closed success/failure outcomes and provenance. Observers cannot change results; journals and claims gate execution. Missing usage or prices are unknown, never zero.
- Reconstitute and evaluate the exact stored whole. Keep component lineage, but do not require graders to reconstruct it from traces or grade every prompt separately.
- Require provider contract tests before claiming support. Current limitations remain explicit: in-memory defaults, no exactly-once billing, and incomplete native streaming/Responses support.

MEAI references:

- [Overview and packages](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai).
- [Middleware pipelines](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient#functionality-pipelines) and [custom middleware](https://learn.microsoft.com/en-us/dotnet/ai/ichatclient#custom-ichatclient-middleware).
- [DelegatingChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.delegatingchatclient): custom middleware base.

## Commands

```sh
mise install                         # Install pinned SDK and tools.
mise run build                       # Build the source library and applications.
mise run format                      # Normalize source; then re-read changed files.
mise run format:check                # Non-mutating formatting check.
mise run verify                      # Offline tests, package consumer, examples.
mise run pack                        # Local NuGet packages; no publish.

mise run test:live:runtime:azure      # Explicit paid generated-command test.
mise run test:live:runtime:anthropic  # Explicit paid generated-command test.
mise run test:live:composition       # Three prompts; read and grade only the whole.

agentcmd_host=dev/Zeeq.AgentCmd.AppHost/Zeeq.AgentCmd.AppHost.csproj
aspire start --non-interactive --apphost "$agentcmd_host"  # Dashboard; Langfuse stays stopped.
aspire resource langfuse start --non-interactive --apphost "$agentcmd_host"
aspire wait langfuse --non-interactive --apphost "$agentcmd_host"
mise run test:langfuse:readback       # Verify local OTLP ingestion and stored generation.
```
