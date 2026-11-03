# Local Langfuse readback

This opt-in executable targets a dedicated self-hosted Langfuse at `127.0.0.1:38941`. It makes no model requests. Docker and Docker Compose are required. The start command creates private temporary credentials and project-scoped containers and volumes. The stop command removes only that dedicated project's containers and volumes, and its credential file.

```sh
dotnet run --project tests/LlmCmd.LangfuseReadback -- start
dotnet run --project tests/LlmCmd.LangfuseReadback
dotnet run --project tests/LlmCmd.LangfuseReadback -- stop
```

The generated state is in `/tmp/zeeq-llmcmd-langfuse` by default and is read directly by the executable. No shell sourcing is needed. Set `LLMCMD_LANGFUSE_STATE` to use another private state directory. Set `LLMCMD_LANGFUSE_PORT` for start and `LLMCMD_LANGFUSE_BASE_URL` for readback to use another loopback port. This is an explicit test and is not part of the default suite. Images are pinned by digest.

Alternatively, start the explicitly stopped `langfuse` resource in the [development Aspire host](../../dev/Zeeq.LlmCmd.AppHost/README.md). For an independent Langfuse-managed model price comparison, run `dotnet run --project tests/LlmCmd.LangfuseReadback -- --cross-check <capture.json>`. The comparison sends no cost and checks the returned managed model, token usage and inferred cost. It is limited to complete estimates and zero cache partitions; a missing model is reported as unavailable.

Run `dotnet run --project tests/LlmCmd.LangfuseReadback` for a synthetic generation. Append `-- <capture.json>` to replay a sanitized live-provider capture, preserving its timestamps, usage and billing context. The tool checks OTLP HTTP acceptance and polls the v2 observations API for model, agreed usage, and any complete explicit cost. Pricing assumes standard global routing and standard speed; Azure also assumes the short context tier. Use this only for a capture known to use that scope. Missing or partial estimates are not emitted as explicit cost. Langfuse can infer cost independently.

Self-host setup and headless initialization: https://langfuse.com/self-hosting/deployment/docker-compose and https://langfuse.com/self-hosting/administration/headless-initialization.
