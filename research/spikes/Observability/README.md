# Aspire collector integration spike

The AppHost runs a typed CommunityToolkit collector (13.6.1; OpenTelemetry Collector contrib image 0.138.0) and a .NET 11 producer. The producer uses the existing executor and real MEAI 10.11.0 `UseOpenTelemetry` middleware with an in-memory fake model. The collector receives OTLP gRPC and writes OTLP JSON lines to `/tmp/opencode/llmcmd-otel/`. No vendor is contacted. The collector's insecure transport and bind mount are for this local spike only.

The telemetry TUnit scenario runs three calls: content disabled, explicitly enabled, and missing usage. A separate scenario makes three HTTP requests for structured extraction, correction and grading of the original version. It checks immutable versions, JSON Pointer, execution identity, and ActivityLinks from new follow-up traces back to extraction, including the emitted grade event. The local ledger and actor are synthetic; authentication and persistence are production work. It compares the returned and collected execution records, checks parent trace linkage, model IDs, usage attributes, absence of missing usage, and exact input/output content on the opted-in span. It verifies the token histograms against 7 input and 3 output tokens through exemplars correlated to the test request trace. It checks bounded metadata in the default span. The exporter may repeat cumulative metric snapshots; do not add repeated snapshots as separate usage.

The profile-routing scenario executes the generated `ExtractInvoice` command. Its synthetic OpenAI registration fails twice (initial call plus one retry), then its Anthropic fallback succeeds. Receiver assertions check one command fallback event, three provider-attempt spans with inner MEAI spans, one fallback counter transition with bounded labels, run/execution correlation, and attempt-duration exemplars. A healthy control executes once without a fallback event. The same prompt and output contract are used across both destinations.

The evidence files contain fake prompts and responses, including opted-in content: `traces.jsonl`, `metrics.jsonl`, `envelope-safe.json`, `envelope-sensitive.json`, `envelope-missing-usage.json`, `grading-evidence.json`, `fallback-evidence.json`, and `tests.xml`. This is a local integration test, not a production privacy policy. It does not exercise a remote model, streaming, tool calls, real vendor billing, or a production deployment.

From the repository root, with Docker running and the isolated .NET SDK installed:

```sh
mkdir -p /tmp/opencode/llmcmd-otel /tmp/opencode/llmcmd-docker-config
export PATH=/tmp/opencode/llmcmd-dotnet11:$PATH
export DOTNET_ROOT=/tmp/opencode/llmcmd-dotnet11
export DOCKER_CONFIG=/tmp/opencode/llmcmd-docker-config
export ASPIRE_ENABLE_CONTAINER_TUNNEL=false
aspire start --apphost "$PWD/spikes/Observability/apphost.cs" --non-interactive --format Json
aspire wait collector --status healthy --apphost "$PWD/spikes/Observability/apphost.cs" --non-interactive
aspire wait producer --status up --apphost "$PWD/spikes/Observability/apphost.cs" --non-interactive
aspire describe --apphost "$PWD/spikes/Observability/apphost.cs" --format Json > /tmp/opencode/llmcmd-otel/describe.json
export LLMCMD_PRODUCER_URL=$(python3 -c 'import json; x=json.load(open("/tmp/opencode/llmcmd-otel/describe.json")); print(next(u["url"] for r in x["resources"] if r["displayName"]=="producer" for u in r["urls"] if u["name"]=="http"))')
dotnet build tests/LlmCmd.Observability.Tests/LlmCmd.Observability.Tests.csproj
TUNIT_ENABLE_JUNIT_REPORTER=1 dotnet run --no-build --project tests/LlmCmd.Observability.Tests/LlmCmd.Observability.Tests.csproj -- --junit-output-path /tmp/opencode/llmcmd-otel/tests.xml
aspire stop --apphost "$PWD/spikes/Observability/apphost.cs" --non-interactive
```

The empty Docker config avoids a credential-helper stall observed on this machine. It does not contain the user's Docker CLI plugins, so the example disables Aspire's optional container tunnel; the producer connects through the collector's local host endpoint. Review this choice for other environments. The test waits for its own matching telemetry to arrive; the Aspire wait commands establish resource readiness.
