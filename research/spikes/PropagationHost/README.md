# Aspire RabbitMQ propagation proof

This reference AppHost runs a typed Aspire RabbitMQ resource, an edge process, a separate worker process and the CommunityToolkit OTel Collector. The edge publishes persisted messages with a stable work envelope and explicit trace metadata. The worker resolves a registered context, enters a local LlmCmd scope, executes the actual generated ExtractInvoice command and writes captured emitter records. Model content is synthetic; provider HTTP traffic is real loopback traffic to the edge's fake endpoint.

```sh
python3 scripts/verify_propagation.py \
  --dotnet /tmp/opencode/llmcmd-dotnet11/dotnet \
  --docker-config /tmp/opencode/llmcmd-docker-config
```

The script starts only `spikes/PropagationHost/apphost.cs`, waits for each resource through Aspire, runs TUnit and stops that exact AppHost in finally. It refuses to take over an already-running instance. It does not stop another workspace's AppHost. The isolated Docker configuration works around this machine's credential-helper stall. `ASPIRE_ENABLE_CONTAINER_TUNNEL=false` avoids the isolated config's missing buildx plugin. Aspire manages ports and RabbitMQ credentials. Neither credentials nor connection strings are passed in process arguments.

## What it proves

- Actual AMQP publish/consume between different .NET process IDs. The typed Aspire integration supplies the connection; core has no RabbitMQ SDK reference.
- Explicit context restoration for internal HTTP and queued work. DraftId/WorkId survive to the command, provider attempt, returned outcome and asynchronous observer records.
- Separate HTTP/consumer traces with an ActivityLink to the message creation span, received by the actual collector file exporter. The tests create independent request roots to avoid inheriting TUnit's single test activity.
- Commit-before-redelivery replay returns the original stored output reference without a second model call. The proof forces broker redelivery using Nack after the receipt commit; it does not simulate network-level packet loss.
- Malformed/unregistered messages make no model call and do not contaminate the next draft.
- Actual external HTTP requests contain no LlmCmd private headers/baggage under the configured instrumentation. Redirects return 302 and are not followed.

RabbitMQ.Client adds a numeric `x-dotnet-pub-seq-no` header when publisher confirmations are enabled. The thin broker binding leaves that opaque and decodes only context headers. This is why converting every broker header to text is incorrect. The generic binary codec still rejects an unsupported representation for a selected context field.

## Test-only storage and authority

The reference worker has a fixed synthetic actor and an exact in-memory context registration. These endpoints are local test endpoints, not production authentication. The consumer rechecks registration before looking up receipts. Production receivers need real worker authentication, grants, and authoritative draft/definition/input/dependency validation.

The host writes a pending intent before model work and flushes an immutable receipt file before acknowledgement. Same WorkId plus different fingerprint conflicts. A pending intent without a receipt stays indeterminate and is requeued only when explicitly drained; the proof does not automatically repeat inference after unknown spend. It has no lease/fencing recovery, cross-process distributed lock, storage transaction, durable HumanLedger, aggregate manifest, or production outbox. Work receipts survive on disk, but registration and HumanLedger are in memory; restart recovery is not claimed. No exactly-once inference/billing guarantee follows from idempotent replay.

The queue is durable inside this test container. The AppHost uses ephemeral resource lifetime and no data volume; normal test shutdown can discard queued test messages. The RabbitMQ image uses the local `management` tag, not a production image pin. HTTP permits inspecting local wire data; use deployment TLS policy for real services.

## Evidence

All artifacts are under `/tmp/opencode/llmcmd-propagation/`:

- `tests.xml`: context/parser/runtime TUnit contracts.
- `host-tests.xml`: actual RabbitMQ/HTTP/collector scenarios.
- `queue-evidence.json`, `isolation-evidence.json`, `http-wire-evidence.json`: endpoint evidence.
- `worker/*.pending`, `worker/*.receipt.json`, `worker/events.jsonl`: synthetic claim/receipt/observer files.
- `otel/traces.jsonl`: collector-received traces; `describe.json`: exact Aspire resources/endpoints.
- `start.log`, `wait-*.log`, `logs-*.txt`, `stop.log`: reproducible lifecycle evidence. Start logs can contain the local dashboard login token; treat them as local artifacts.

Pinned: .NET 11 RC3/C# 15, MEAI 10.11.0, RabbitMQ.Client 7.1.2, OTel core/exporter 1.20.1, HTTP/ASP.NET instrumentation 1.19.0, Aspire SDK/hosting 13.6.4, Collector toolkit 13.6.1 and contrib image 0.138.0. This tests one explicit stack, not arbitrary broker or framework interoperability.
