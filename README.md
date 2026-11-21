# Zeeq.AgentCmd

Typed prompt commands and aggregate composition over `Microsoft.Extensions.AI`. This is the first source library, version `0.1.0-alpha.1`, extracted from the tested designs in this repository. It targets .NET 11 and C# 15. `global.json` pins the SDK used by mise and CI.

[Documentation](https://zeeq-ai.github.io/zeeq-agentcmd/) · [Offline quick start](docs/content/1.getting-started/3.quick-start.md) · [Build the documentation site](docs/README.md)

AgentCmd is available under the [MIT License](LICENSE).

The core NuGet package includes the runtime, source generators and registration analyzers. Its generator emits AOT-safe serializer metadata directly. A consumer needs one `PackageReference`; it does not need a handwritten command constructor, serializer context, JSON schema, registration table or MSBuild import. Provider extraction and OpenTelemetry are separate optional packages.

## Author a command

```csharp
using Zeeq.AgentCmd;

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
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.Generated;

services
    .AddAgentCmd(options => options
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

The complete [command example](examples/Zeeq.AgentCmd.CommandExample/Program.cs) includes host construction and an offline client. A generated public catalog remains available for registering commands declared in a different assembly.

### Multiple clients and models

Register host-owned pipelines with MEAI, then map profiles to their keys and model
or deployment targets. `createAzureClient` and `createClaudeClient` below are host
factories returning configured `IChatClient` pipelines, including optional model
validation, capture, caching or telemetry middleware.

```csharp
services.AddKeyedChatClient("azure.luna", createAzureClient);
services.AddKeyedChatClient("claude", createClaudeClient);

services
    .AddAgentCmd(options => options
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

The bundled analyzer checks string references in straight-line `AddAgentCmd`
lambdas. Unknown clients (`AGENTCLIENT001`), duplicate client registrations
(`AGENTCLIENT002`) and constant fixed-model conflicts (`AGENTCLIENT003`) fail the
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

The host owns authorization, client pipelines, assignment and the side-effect policy for shadows. The default cache, scheduler and ledger are process-local. Composition shadows are offered explicitly after the primary is pinned. See the [runtime experiments guide](docs/content/2.guides/7.runtime-experiments.md) and run the [offline example](examples/Zeeq.AgentCmd.ExperimentsExample/README.md) with `mise run demo:experiments`.

## Management console

The repository now includes a .NET management API and a compact Vue/Vite console. The API uses discovered `IEndpoint` modules, transient `IEndpointHandler` classes, named operations, handler activities, and an OpenAPI snapshot consumed by Kubb. The console retains the five work areas: Commands, Experiments, Traces, Datasets, and Review. Settings reports actual storage and registered capabilities.

Start the local operator API and client in separate terminals:

```sh
mise run console:install
mise run console:api:local
# In another terminal:
mise run console:dev
```

The API listens on `http://127.0.0.1:5180`; the client proxies `/api/v1` there. Local mode is explicit and accepts loopback connections, Host authorities, and browser origins only. It grants `local-operator` the `local/dev/default` scope. There is no seeded product data, worker, provider client, or publication target on startup. The [offline workflow fixture](tests/fixtures/AgentCmd.ControlPlane.OfflineSeed/README.md) uses generated commands and fake chat clients when retained development evidence is wanted.

Remote hosts must install authenticated-principal middleware through `ControlPlaneApplication.Build(args, configure)`. The API requires a subject, action permissions in `agentcmd.permission`, and allowed application/environment/partition claims. Scope headers select an authorized namespace; they never establish identity. Default external mode denies unauthenticated reads and writes. Resource edits and transitions require the returned ETag in `If-Match`. A conflict preserves the earlier version; frozen resources are immutable.

Storage is a host-wide bootstrap choice. `ControlPlane:Provider=Sqlite` uses persistent local disk and one host process. Its sidecar lease also excludes offline maintenance. Never use a network file share or a database-file alias. PostgreSQL uses `ControlPlane:Provider=Postgres` plus `ControlPlane:ConnectionStringReference`, which names a server environment variable rather than containing credentials. Database selection is independent of the runtime publication source. The SQLite and PostgreSQL stores share revisions, transactional audit/outbox, claim fencing and late-receipt semantics. Explicit qualification passed against PostgreSQL 17.6 with durable settings; this does not test connectivity or permissions for another deployment.

The first schema initializes additively and refuses unsupported schema versions. Retention is `RetainAll`: no automatic garbage collection or deletion API removes pinned evidence. Assets have an independent scoped content-addressed filesystem store. Future schema/provider changes require an offline migration and restart; no live store swap is offered.

### Register structure, then define judgment

Generated catalogs expose `ExportManifest()` with input/output schemas, structural signatures, placeholders and runtime override metadata. Unsupported/custom serializer shapes remain opaque and require typed preflight. Templates and profiles are withheld unless `includeBaselineTemplates: true` is requested. A digest checks integrity; authenticated namespace authority still controls registration.

The core package supplies an optional exporter under `tools/`. Add `GeneratePathProperty="true"` to its `PackageReference`, explicitly import `$(PkgZeeq_AgentCmd)/tools/AgentCmd.Manifest.targets`, then run:

```sh
dotnet msbuild MyApp.csproj -restore -t:AgentCmdExportManifest
```

The target needs Python 3. It rebuilds and extracts generated data without loading or starting the application. Ordinary compilation does not import this target or run Python. In a source checkout, import `scripts/AgentCmd.Manifest.targets`. Register the exported JSON through `POST /api/v1/builds`; a wrapper can retain `manifestJson`, `manifestDigest`, source revision and CI identity without changing the catalog bytes.

A manifest defines structure. A frozen benchmark explicitly selects business criteria and one primary criterion; other fields remain unscored. Criteria name independent output, reference and source bindings. `outputBindings` maps declared output signatures to reviewed pointers for cross-contract comparisons; a missing mapping is unmapped. Scorers are selected explicitly. Human, exact, numeric and scoped LLM assessments retain their exact original output references, evaluator/protocol versions and non-score states. Human corrections do not improve model scores.

LLM judging can target a field, subtree, collection or whole output. Collection order, duplicates and matching unit are explicit. Source-grounded additions and known-reference agreement stay distinct. A supported addition against an exhaustive reference creates an unresolved completeness dispute. Citations prove retained text exists, not that it entails a claim. Judge configuration and calibration thresholds freeze before preview. Primary judge admission requires matching human calibration on independent source families; a judge is not ground truth. Candidate and evaluator calls require explicit launch approval and host-owned pipelines. Unknown usage, pricing, timing and guardrails remain unknown.

### Run, compare and curate

Backtests pin frozen inputs, build, publication/variant and benchmark. Reassessment uses original outputs and makes no new candidate calls; judge calls can still incur cost. Claims and a claim-bound journal gate provider starts. Late finishes remain recordable. An indeterminate send is held and never retried automatically. Strict `RequireExperimentCandidate(...)` plus `PreflightAsync()` prevents paid fallback to a different configuration; production fallback behavior remains unchanged.

Applications explicitly register `TypedWorkerAdapter<TInput,TOutput>` or `JudgeWorkerAdapter` for their own compiled commands and provider-neutral `IChatClient` pipelines. `InProcessEvaluationWorker.ForCommand/ForJudge` dispatches one compatible item per `DispatchOnceAsync` call; the application owns scheduling, shutdown and side-effect policy. The console never loads assemblies supplied by a browser. With no registered capability, work stays queued. The default ledger is still in memory; durable applications must provide their own ledger/composition store.

Evaluation pipelines must be tool-free or explicitly approved as side-effect-free. Put physical-attempt capture/journaling next to the provider inside any host retry/tool loops, or disable those loops for evaluation. One admitted `IChatClient` invocation does not prove one physical HTTP request, and no exactly-once billing or exact spend ceiling is promised.

Comparisons retain exact assessment IDs and baseline pins. Reports reduce repeats within a case, give cases equal weight, and expose common scored cases, coverage, non-scores and unresolved guardrails. They do not infer causal lift or a statistical winner. Purpose is separate from Primary/Shadow role; missing origin is Unknown. Trusted application services can report production/shadow outcomes and captured attempt/trace evidence through `/executions/observe`; best-effort observations never substitute for a send journal. Captured invocation elapsed is distinct from attempt timing and from a benchmark's case-latency rule.

Acceptance cases are withheld in browser dataset, job, execution and assessment responses until an explicit audited reveal. Exposure follows the source family across versions. Drafts are protected too. Related sources cannot silently cross regression, calibration and acceptance splits. Hosts must record equivalent reveals or prompt-example use outside this console. A trace-linked incident needs retained authorized input/assets before capture. Capture creates a regression draft case and an owned review; the case cannot freeze before acceptance, and no reference answer is invented.

### Publish and operate

Applications configure `PublicationTargetRegistry` explicitly. `PublicationPostgresTarget` supplies immutable history and ABA-safe pointer CAS independently of the management database; `PublicationSnapshotSource` reads it through `IExperimentSnapshotSource`. Initialization is explicit and constructors resolve no credentials. File remains a development reader with no CAS/history writer. S3, Redis and Floci integration remain future adapter work; Floci is only needed when an S3 adapter is tested.

Publication review records the target's actual pointer and exact immutable decision/content. Delivery uses that precondition once. Lost acknowledgements reconcile by reading; they never trigger a blind overwrite or resend. Manual decisions say `automatedAcceptance=NotAsserted`. Host reports show only reporting hosts and expire to Unknown; they do not prove fleet-wide adoption. Stopping restores an explicitly reviewed empty complete snapshot, not all-zero weights. Host defaults and compiled command changes remain deployment actions.

SQLite backup/restore is an offline host-wide command. Stop the host and use explicit absolute source paths. A backup verifies database and asset checksums. Restore verifies staged bytes and requires fresh destination paths; it never overwrites the original:

```sh
mise exec -- dotnet run --project src/Zeeq.AgentCmd.ControlPlane.Host -- \
  --maintenance=backup --archive=/absolute/backups/run-001 \
  --ControlPlane:DatabasePath=/absolute/data/management.db \
  --ControlPlane:AssetPath=/absolute/data/assets
# Use --maintenance=verify to check an archive, or --maintenance=restore with new destination paths.
```

PostgreSQL backups use the deployment's database backup tooling plus the retained asset store; the console does not expose host-wide database export over scoped HTTP. Full telemetry/span queries, automatic cost pricing, schedules, CI gates, alerts and automatic promotion remain extension work. The console shows their absence explicitly.

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

The [composition example](examples/Zeeq.AgentCmd.CompositionExample/Program.cs) declares all types, builds DI, exercises both branches, publishes one aggregate version, replays the handle and grades that exact version. It runs with reflection serialization disabled.

## Context, profiles and observability

Use `AgentCmdContext.BeginScope(context)` for local defaults. `.InContext(context)` replaces that default as a whole; `.WithoutContext()` suppresses it. Ambient context is correlation, not authorization or slot membership. Explicit composition registration decides membership. Sinks receive captured envelopes and do not re-read AsyncLocal.

`ProfileRouter` supports typed profile keys, retry budgets and fallback with per-attempt records. `Zeeq.AgentCmd.Providers` supplies normalized accounting, protocol adapters, data-driven pricing and opt-in model request validation without taking a vendor SDK dependency. See [model policies and extension seams](docs/architecture/provider-model-policies.md). `Zeeq.AgentCmd.OpenTelemetry` supplies collector configuration, `AddAgentCmdInstrumentation()` and explicit wire propagation. Wire references must be resolved and authorized at the receiver. Private operation references do not enter W3C baggage by default.

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

`build` compiles `Zeeq.AgentCmd.slnx`. `test` runs the maintained TUnit suites directly with dotnet, including offline control-plane checks. `test:package` packs the core and builds/runs an isolated consumer outside the checkout using only its NuGet package, with reflection disabled; it also checks wrong-slot compilation and optional manifest export (Python 3). `pack` writes three local alpha packages to `artifacts/packages`. No package is published. `mise run verify` also checks the installed console client. Run `console:install` first. No ordinary verification task starts a container or makes paid model calls.

Formatting uses a pinned CSharpier. Its parser predates C# 15 `closed`/`union`, so an exact file list is formatted and checked by the pinned SDK instead. Production public XML documentation and a small SDK analyzer [quality floor](docs/architecture/quality-floor.md) fail builds on violations; no third-party analyzer bundle is added.

Paid API tests remain separate:

```sh
mise run test:live:openai
mise run test:live:azure
mise run test:live:anthropic
mise run test:live:runtime:azure
mise run test:live:runtime:anthropic
```

They retain `UserSecretsId=zeeq-llmcmd-live-tests` on `tests/AgentCmd.LiveProviders.Tests`. Their [setup guide](tests/AgentCmd.LiveProviders.Tests/README.md) shows key configuration. Ordinary test runs do not make paid calls.

The runtime cases exercise a generated command, typed JSON, stored-output readback, and per-attempt events through the real Azure OpenAI and Anthropic SDKs. Sanitized accounting receipts can be replayed into an explicitly started local Langfuse instance without another model call:

```sh
mise run test:langfuse:start
mise run test:langfuse:readback artifacts/live-providers/azureopenai.runtime.capture.json
mise run test:langfuse:readback artifacts/live-providers/anthropic.runtime.capture.json
mise run test:langfuse:stop
```

This checks the configured Langfuse target, OTLP export, and persisted generation readback. Docker is needed only for these explicit [local Langfuse tests](tests/AgentCmd.LangfuseReadback/README.md). Token estimates use [first-party rate cards](docs/research/provider-pricing.md) and explicit billing scope; they are not invoice reconciliation.

## Source layout and design record

- `src/Zeeq.AgentCmd`: commands, closed outcomes, composition, context, profiles, DI and evaluation contracts.
- `src/Zeeq.AgentCmd.Generators`: command, composition and serialization generators plus registration analyzers, bundled in the core package.
- `src/Zeeq.AgentCmd.Providers` and `src/Zeeq.AgentCmd.OpenTelemetry`: optional integration packages.
- `tests`: maintained tests against these assemblies, generated fixtures and an isolated package consumer.
- `examples`: runnable applications that use the source library.
- `research`: the original executable spikes, tests and orchestration scripts, preserved independently.

Read the [foundation decisions](docs/architecture/library-foundation.md) for the tested properties, their rationale and remaining work. The [research index](research/README.md) explains how to revisit historical proofs.

## Current limits

This is an alpha foundation. The default feedback ledger and composition store are explicitly in memory. They do not provide process-restart recovery, distributed leases/fencing, a database transaction or exactly-once provider billing. Pending work has unknown spend. Cancellation after sending may still be billable. Store interfaces allow host implementations, but no durable implementation ships yet.

A dynamic `Then` invocation's contract can be checked only after its prerequisite is stored and before that dependent call spends. Optional slots may be absent but cannot attach a null part. Native streaming accounting, OpenAI Responses API extraction, invoice reconciliation and hosted collector read-back remain separate work. Azure GPT-6 Luna and Claude Haiku 5.5 have passed explicit API and generated-runtime tests; local self-hosted Langfuse has accepted and returned their generation records.

The C# 15/.NET 11 toolchain is pinned to the .NET 11 GA SDK. Serialization metadata is emitted during ordinary compiler generation; no precompiler executable, serializer MSBuild target or packaged Roslyn runtime is required. Unsupported contracts receive a build diagnostic and can use an explicit STJ context. See [serialization and NativeAOT](docs/architecture/serialization-and-aot.md) for the supported boundary and package-only proof.
