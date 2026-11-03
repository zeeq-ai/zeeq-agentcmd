# Human grading and immutable output versions

The in-memory `HumanLedger` implements `IHumanFeedbackLedger`. RunId is independent of trace sampling. OutputRef identifies a run, producer, artifact, unique immutable version ID and version number. `ProducerRef` distinguishes a command execution, a composition draft, and a human correction with its source ancestry. An aggregate has no model execution ID. Every correction names the exact version the reviewer saw, an RFC 6901 JSON Pointer, and the expected old value. It creates a new version. Independent corrections branch; no call silently edits a moving latest version.

Grades target that exact reference, even after correction. Event IDs are idempotency keys scoped to the run. Repeating a request returns its receipt; a changed payload or event kind using the same key returns a conflict. Supersession requires the same actor, version and field. Undefined values, duplicate JSON keys and invalid pointers are rejected with closed outcomes. JSON null is a valid value.

The application supplies actor identity. This spike enforces exact tenant/subject ownership and returns the same not-found result for absent and unauthorized data. Invalid actors cannot create a run; `CreateRun` returns Guid.Empty. A production application should expose a richer creation result and choose its reviewer authorization policy. Never read actor or tenant from trace baggage.

Host ledger adapters must implement `RecordProducedOutput` explicitly. This is a compile-time migration: an adapter cannot silently reject every aggregate only after its parts have been paid for. Producer references use the `$producer` JSON discriminator. A store that reorders JSON properties must read them with `JsonSerializerOptions.AllowOutOfOrderMetadataProperties = true` (or the corresponding source-generation option). The shared `LlmEvent` contract uses the same rule for `$event`.

Human events create Activities with run/execution/version/event IDs, a pointer and an ActivityLink to the stored extraction context. Values and grader identity stay in the ledger. Grades add a `gen_ai.evaluation.result` event. This does not guarantee every OTLP vendor turns it into feedback. [Aspire tests](../Observability/README.md) verify separate HTTP requests and traces at the receiver.

Production gates: durable storage and outbox; JSON Schema validation of corrections; multi-field patching; distributed concurrency; revision-head policy; retention; audit authorization; and provider response-ID joins. Replacement currently accepts unambiguous JSON and does not guarantee the compiled result schema. Idempotency fingerprints use serialized request bytes, not a full canonical JSON specification.

```sh
/tmp/opencode/llmcmd-dotnet11/dotnet build tests/LlmCmd.HumanGrading.Tests/LlmCmd.HumanGrading.Tests.csproj
TUNIT_ENABLE_JUNIT_REPORTER=1 /tmp/opencode/llmcmd-dotnet11/dotnet run --no-build --project tests/LlmCmd.HumanGrading.Tests/LlmCmd.HumanGrading.Tests.csproj -- --junit-output-path /tmp/opencode/llmcmd-human/tests.xml
```
