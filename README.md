# Zeeq.LlmCmd

Typed prompt commands and aggregate composition over `Microsoft.Extensions.AI`. This is the first source library, version `0.1.0-alpha.1`, extracted from the tested designs in this repository. It targets .NET 11 and C# 15.

[Documentation](https://zeeq-ai.github.io/zeeq-llmcmd/) · [Offline quick start](docs/content/1.getting-started/3.quick-start.md) · [Build the documentation site](docs/README.md)

LlmCmd is available under the [MIT License](LICENSE).

The core NuGet package includes the runtime, source generators and registration analyzers. Its generator emits AOT-safe serializer metadata directly. A consumer needs one `PackageReference`; it does not need a handwritten command constructor, serializer context, JSON schema, registration table or MSBuild import. Provider extraction and OpenTelemetry are separate optional packages.

## Author a command

```csharp
using Zeeq.LlmCmd;

namespace MyApp;

public sealed record InvoiceInput(string Document, string Locale = "en");
public sealed record Invoice(decimal Total, string Currency);

public sealed partial class ExtractInvoice : PromptCommand<InvoiceInput, Invoice>
{
    const string Id = "invoice.extract";
    const string UserTemplate = "Extract the invoice from {Document} ({Locale}).";
}
```

The ID stays explicit so a class rename does not change stored prompt identity. The generator checks the templates and produces JSON contracts, requiredness, recursive null validation, schema, rendering and DI registrations from the same type model.

## Register and call

The host supplies an `IChatClient` and a trusted actor accessor. The following code is in the assembly that declares its commands:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Zeeq.LlmCmd;
using Zeeq.LlmCmd.Generated;

services
    .AddLlmCmd(options => options
        .UseChatClient(_ => chatClient)
        .AddProfile("extract", "model-id")
        .UseActor(_ => actor))
    .AddGeneratedCommands();

var extract = provider.GetRequiredService<ExtractInvoice>();
var result = await extract
    .WithParameters(new("Invoice for USD 12.50"))
    .Configure(options => options.Temperature = 0.1f)
    .ExecuteAsync(ct);

switch (result)
{
    case Outcome<Invoice>.Success success:
        Console.WriteLine(success.Value.Total);
        break;
    case Outcome<Invoice>.Failed failed:
        Console.WriteLine(failed.Failure.Kind);
        break;
}
```

`WithParameters`, profile selection, context selection and settings return immutable invocations. `ExecuteAsync` captures the effective context once. `Outcome<T>` has two top-level cases; the non-generic closed `ExecutionFailure` preserves its original identity and context through `Map`, `Bind` and `BindAsync`. Success includes a separate output-delivery receipt.

A single configured profile becomes the default. With multiple profiles, select one with `UseDefaultProfile("fast")` or author a command-specific profile. Catalog registration validates these choices before the client factory runs.

The complete [command example](examples/Zeeq.LlmCmd.CommandExample/Program.cs) includes host construction and an offline client. A generated public catalog remains available for registering commands declared in a different assembly.

### Multiple clients and models

Register host-owned pipelines with MEAI, then map profiles to their keys and model
or deployment targets. `createAzureClient` and `createClaudeClient` below are host
factories returning configured `IChatClient` pipelines, including optional model
validation, capture, caching or telemetry middleware.

```csharp
services.AddKeyedChatClient("azure.luna", createAzureClient);
services.AddKeyedChatClient("claude", createClaudeClient);

services
    .AddLlmCmd(options => options
        .AddChatClient("azure.luna", fixedModel: "gpt-6-luna")
        .AddChatClient("claude")
        .AddProfile("fast", "azure.luna", "gpt-6-luna",
            new ProfileDefaults(MaxOutputTokens: 64, ReasoningEffort: ReasoningEffort.None))
        .AddProfile("deep", "claude", "claude-sonnet-5-5",
            new ProfileDefaults(MaxOutputTokens: 512, ReasoningEffort: ReasoningEffort.Low))
        .UseDefaultProfile("fast")
        .UseActor(_ => actor))
    .AddGeneratedCommands();

var result = await extract
    .WithParameters(new("Invoice for USD 12.50"))
    .WithProfile("deep")
    .ExecuteAsync(ct);
```

Client-registration names are case-sensitive routing keys, independent of the
provider/API identity used by capture. Model-bound SDK clients need separate
registrations per model or deployment, with `fixedModel` set explicitly. A client
that honors `ChatOptions.ModelId` can serve several profiles. A profile that
conflicts with its fixed binding fails setup before any factory runs. The pinned
OpenAI Chat Completions bridge is model-bound; the Claude bridge supports model
selection per request. Azure targets are deployment names, not proof of an
underlying model; keep the separate model-policy/pricing binding explicit.

The keyed overload borrows a singleton-compatible DI client. The factory overload
`AddChatClient("name", sp => CreateClient(sp))` transfers client ownership to the
runtime. Do not return a DI-owned client from that overload. `UseChatClient` and
the existing two-argument `AddProfile` remain available for single-client hosts;
mixing the two configuration modes is rejected.

The bundled analyzer checks string references in straight-line `AddLlmCmd`
lambdas. Unknown clients (`LLMCLIENT001`), duplicate client registrations
(`LLMCLIENT002`) and constant fixed-model conflicts (`LLMCLIENT003`) fail the
build. Configuration values, helpers and control flow remain supported by the
runtime; the analyzer reports only what its local scope can establish. See
[registration analysis](docs/architecture/client-registration-analysis.md).

`ProfileDefaults` contains immutable portable inference settings. Explicit request
options win, then the selected profile, then command/provider defaults. Fallback
uses its own profile settings; caller overrides remain explicit. Retry, fallback
and per-attempt timeout are optional `AddProfile` arguments; `UseBudget` bounds
the whole route. Model-policy middleware still validates model-specific limits
and unsupported temperature/reasoning combinations. Omitted settings stay omitted.

Defaults and fixed bindings enter composition replay identity. Legacy routes keep
their original fingerprint. Host middleware configuration is outside that identity;
hosts must version request/trial keys when changing such external behavior.

## Try runtime variants

An experiment source can supply a new prompt, opted-in parameter values, and an allowed client, model and sampling configuration while the application is running. A/B selection returns the selected variant. Optional shadows run independently and retain their own output references for comparison. A composition pins its publication before executing parts, so a later update cannot switch the selected publication during execution. If a source fails before selection, the complete configured default runs.

The host owns authorization, client pipelines, assignment and the side-effect policy for shadows. The default cache, scheduler and ledger are process-local. Composition shadows are offered explicitly after the primary is pinned. See the [runtime experiments guide](docs/content/2.guides/7.runtime-experiments.md) and run the [offline example](examples/Zeeq.LlmCmd.ExperimentsExample/README.md) with `mise run demo:experiments`.

## Compose an aggregate

```csharp
var root = await roots.BeginAsync<DocumentExtraction>(
    new(runId, InputRevision: "invoice-v1", Key: "extract:invoice-42"), ct);

root.WithHeaderFrom(header.WithParameters(input))
    .WithPartiesFrom(parties.WithParameters(input));

if (detailed)
    root.WithLineItemsFrom(detailedLines.WithParameters(input));
else
    root.WithLineItemsFrom(quickLines.WithParameters(input));

root.WithTotalsFrom(root.Steps.LineItems.Then(items =>
    totals.WithParameters(new(input.Document, items.Value.Count))));

var result = await root.ExecuteAsync(ct);
```

The generated names come from the aggregate's properties. Registration changes the same construction handle and does no model work. Execute freezes and validates it. A dependency receives the exact stored `Pinned<T>` result. Duplicate registration is a configuration failure. A failed dependency blocks its dependent. Required-slot coverage, authorization, claims, attachment and finalization are runtime checks.

The [composition example](examples/Zeeq.LlmCmd.CompositionExample/Program.cs) declares all types, builds DI, exercises both branches, publishes one aggregate version, replays the handle and grades that exact version. It runs with reflection serialization disabled.

## Context, profiles and observability

Use `LlmCmdContext.BeginScope(context)` for local defaults. `.InContext(context)` replaces that default as a whole; `.WithoutContext()` suppresses it. Ambient context is correlation, not authorization or slot membership. Explicit composition registration decides membership. Sinks receive captured envelopes and do not re-read AsyncLocal.

`ProfileRouter` supports typed profile keys, retry budgets and fallback with per-attempt records. `Zeeq.LlmCmd.Providers` supplies normalized accounting, protocol adapters, data-driven pricing and opt-in model request validation without taking a vendor SDK dependency. See [model policies and extension seams](docs/architecture/provider-model-policies.md). `Zeeq.LlmCmd.OpenTelemetry` supplies collector configuration, `AddLlmCmdInstrumentation()` and explicit wire propagation. Wire references must be resolved and authorized at the receiver. Private operation references do not enter W3C baggage by default.

## Build and verify

`global.json` is the SDK version source. Install it with mise:

```sh
mise install
mise run build
mise run format
mise run format:check
mise run test
mise run test:package
mise run demo:command
mise run demo:composition
mise run demo:composition --quick
mise run demo:experiments
mise run pack
```

`build` compiles `Zeeq.LlmCmd.slnx`. `test` runs the maintained TUnit suites directly with dotnet. `test:package` packs the core and builds/runs an isolated consumer outside the checkout using only its NuGet package, with reflection disabled; it also checks wrong-slot compilation. `pack` writes three local alpha packages to `artifacts/packages`. No package is published. `mise run verify` runs the library checks and examples in order. These tasks do not need Python or Aspire.

Formatting uses the same CSharpier pin as `zeeq-tmpl`. Its parser predates C# 15 `closed`/`union`, so an exact file list is formatted and checked by the pinned SDK instead. Production public XML documentation and a small SDK analyzer [quality floor](docs/architecture/quality-floor.md) fail builds on violations; no third-party analyzer bundle is added.

Paid API tests remain separate:

```sh
mise run test:live:openai
mise run test:live:azure
mise run test:live:anthropic
mise run test:live:runtime:azure
mise run test:live:runtime:anthropic
```

They retain `UserSecretsId=zeeq-llmcmd-live-tests` on `tests/LlmCmd.LiveProviders.Tests`. Their [setup guide](tests/LlmCmd.LiveProviders.Tests/README.md) shows key configuration. Ordinary test runs do not make paid calls.

The runtime cases exercise a generated command, typed JSON, stored-output readback, and per-attempt events through the real Azure OpenAI and Anthropic SDKs. Sanitized accounting receipts can be replayed into an explicitly started local Langfuse instance without another model call:

```sh
mise run test:langfuse:start
mise run test:langfuse:readback artifacts/live-providers/azureopenai.runtime.capture.json
mise run test:langfuse:readback artifacts/live-providers/anthropic.runtime.capture.json
mise run test:langfuse:stop
```

This checks the configured Langfuse target, OTLP export, and persisted generation readback. Docker is needed only for these explicit [local Langfuse tests](tests/LlmCmd.LangfuseReadback/README.md). Token estimates use [first-party rate cards](docs/research/provider-pricing.md) and explicit billing scope; they are not invoice reconciliation.

## Source layout and design record

- `src/Zeeq.LlmCmd`: commands, closed outcomes, composition, context, profiles, DI and evaluation contracts.
- `src/Zeeq.LlmCmd.Generators`: command, composition and serialization generators plus registration analyzers, bundled in the core package.
- `src/Zeeq.LlmCmd.Providers` and `src/Zeeq.LlmCmd.OpenTelemetry`: optional integration packages.
- `tests`: maintained tests against these assemblies, generated fixtures and an isolated package consumer.
- `examples`: runnable applications that use the source library.
- `research`: the original executable spikes, tests and orchestration scripts, preserved independently.

Read the [foundation decisions](docs/architecture/library-foundation.md) for the tested properties, their rationale and remaining work. The [research index](research/README.md) explains how to revisit historical proofs.

## Current limits

This is an alpha foundation. The default feedback ledger and composition store are explicitly in memory. They do not provide process-restart recovery, distributed leases/fencing, a database transaction or exactly-once provider billing. Pending work has unknown spend. Cancellation after sending may still be billable. Store interfaces allow host implementations, but no durable implementation ships yet.

A dynamic `Then` invocation's contract can be checked only after its prerequisite is stored and before that dependent call spends. Optional slots may be absent but cannot attach a null part. Native streaming accounting, OpenAI Responses API extraction, invoice reconciliation and hosted collector read-back remain separate work. Azure GPT-6 Luna and Claude Haiku 5.5 have passed explicit API and generated-runtime tests; local self-hosted Langfuse has accepted and returned their generation records.

The C# 15/.NET 11 toolchain remains pinned to the verified release candidate. Serialization metadata is emitted during ordinary compiler generation; no precompiler executable, serializer MSBuild target or packaged Roslyn runtime is required. Unsupported contracts receive a build diagnostic and can use an explicit STJ context. See [serialization and NativeAOT](docs/architecture/serialization-and-aot.md) for the supported boundary and package-only proof.
