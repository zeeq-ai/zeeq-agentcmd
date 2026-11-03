# OpenTelemetry integration

This optional package contains collector exporter profiles, `AddLlmCmdInstrumentation()` for tracing/metrics, and explicit transport-independent context propagation. A host chooses its own exporter and supplies secret references through `ISecretSource`.

The core runtime depends on System.Diagnostics rather than the OpenTelemetry SDK. Operation correlation is captured once at execution entry. Observer failure cannot replace a model outcome or stand in for a durable journal.

For cross-process work, transport a versioned work envelope, resolve and authorize it under the receiving actor, then establish a local scope. Private LlmCmd references do not enter ambient W3C baggage by default. Broker settlement and durable recovery remain host responsibilities.

Collector profile tests exercise actual exporter HTTP payloads against a local handler. They do not establish acceptance or read-back from hosted Langfuse, LangSmith, Braintrust or Logfire.

`LangfuseGeneration.EmitSafely` emits a completed provider snapshot as a generation using an explicitly captured parent context and timestamps, while isolating observer faults. Register `Zeeq.LlmCmd.Langfuse` with the tracer provider. Only agreed input, output and total token counts are emitted; raw content and overlapping subsets are omitted. A complete USD pricing estimate may be supplied; partial estimates are omitted. Prompt versions are integers. `Profiles.LangfusePromptAttributes` returns an integer version suitable for OTLP; the older string helper remains for compatibility. Langfuse may infer costs from matching model definitions when no estimate is supplied.

Mapping reference: https://langfuse.com/integrations/native/opentelemetry
