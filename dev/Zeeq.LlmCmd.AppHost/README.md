# Optional Langfuse development host

This host is independent of ordinary build and test tasks. Starting the host leaves the `langfuse` resource stopped. Start it explicitly when readback is needed:

```sh
aspire start --non-interactive --apphost dev/Zeeq.LlmCmd.AppHost/Zeeq.LlmCmd.AppHost.csproj
aspire describe --non-interactive --apphost dev/Zeeq.LlmCmd.AppHost/Zeeq.LlmCmd.AppHost.csproj
aspire resource langfuse start --non-interactive --apphost dev/Zeeq.LlmCmd.AppHost/Zeeq.LlmCmd.AppHost.csproj
aspire wait langfuse --non-interactive --apphost dev/Zeeq.LlmCmd.AppHost/Zeeq.LlmCmd.AppHost.csproj
```

The single Aspire executable resource manages the pinned local Compose stack. The six child containers are Compose-managed, not separate Aspire resources. It generates local project credentials in private temporary state. Provider credentials are not passed to it. The stack may remain available after the resource or host stops. To remove its dedicated containers, volumes and temporary credentials when finished, stop the Aspire resource, then run `dotnet run --project tests/LlmCmd.LangfuseReadback -- stop`.

The opt-in [readback tool](../../tests/LlmCmd.LangfuseReadback/README.md) checks actual OTLP ingestion and supports independent Langfuse price comparison. The estimate covers documented token rates, not invoices; see [pricing source](../../src/Zeeq.LlmCmd.Providers/Pricing) and [aggregate execution](../../src/Zeeq.LlmCmd/Commands/Composition).
