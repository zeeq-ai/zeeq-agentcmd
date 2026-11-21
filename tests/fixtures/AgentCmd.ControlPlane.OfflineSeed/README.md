# Offline control-plane workflow fixture

This executable creates retained development evidence with generated typed commands and fake chat clients. It never runs on application startup. It uses a local SQLite database and a local asset directory. The ledger is in memory; saved control-plane receipts remain available after the process exits, but the ledger is not a durable output service.

Run from the repository root only when fixture data is wanted:

```sh
AGENTCMD_OFFLINE_FIXTURE=1 \
AGENTCMD_OFFLINE_FIXTURE_ROOT=artifacts/control-plane/verified \
  mise exec -- dotnet run --project tests/fixtures/AgentCmd.ControlPlane.OfflineSeed/OfflineSeed.csproj -- --seed
```

The process appends a fresh, prefixed fixture and writes `fixture-ids.json` in the selected directory. It does not publish a runtime configuration or contact a provider. The reassessment retains original outputs and remains `AwaitingAssessment` after its deterministic criteria run because human and judge criteria still need assessment. Guardrail results remain unknown.
