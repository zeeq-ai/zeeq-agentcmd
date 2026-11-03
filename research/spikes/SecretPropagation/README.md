# AppHost secret propagation spike

This spike uses a generated placeholder to prove that an AppHost user secret can reach an explicitly started TUnit process. It has no vendor, model, or container dependency.

The [AppHost](apphost.cs) declares `UserSecretsId=zeeq-llmcmd-secret-propagation-spike` and a secret parameter named `secret-propagation-placeholder`. The driver writes `Parameters:secret-propagation-placeholder` with `dotnet user-secrets set --file`, supplying the JSON through stdin. The value is outside the repository, is absent from command arguments, and is never printed.

The pinned .NET 11 RC SDK does **not** support `dotnet user-secrets init --file`. Its error requests a `#:property UserSecretsId=...` declaration. The AppHost therefore declares the ID directly. `set --file` works with that declaration.

Both resources run the same [TUnit project](../../tests/LlmCmd.SecretPropagation.Tests/SecretTests.cs):

- `receiving-tests` receives the parameter through `.WithEnvironment("LLMCMD_SPIKE_SECRET", placeholder)`. Its test reads only `Environment.GetEnvironmentVariable` and compares against an independently calculated SHA-256 digest.
- `control-tests` has no secret binding. Its test verifies that the same environment variable is absent, despite sharing the AppHost and operating-system user.

Each process also checks that the placeholder is absent from its command arguments. Boolean assertions prevent credential values from appearing in failure reports. Both resources use `WithExplicitStart()`. The driver verifies `NotStarted` before starting them with `aspire resource <name> start`. A small file gate holds the tests until `aspire wait --status up` observes their processes; it prevents a short console test from exiting before the CLI can observe it.

Run from the repository root:

```sh
mise run spike:secrets
```

The driver builds the test project, starts only this AppHost, explicitly runs both resources, validates their JUnit reports, and stops the AppHost in `finally`. It leaves the harmless placeholder in this spike's dedicated user-secrets store. Subsequent runs replace it with a fresh placeholder.

Evidence is written under `/tmp/opencode/llmcmd-secret-propagation/`: `proof.json`, `before-explicit-start.json`, `after-tests.json`, and the two `*-tests.xml` reports. Stored resource descriptions contain status and exit-code fields only, because full Aspire descriptions can contain process environment values.

This proves AppHost-managed process injection. A test process launched independently from an IDE, `dotnet run`, or Python does not automatically receive these bindings. User-secrets is development storage outside the repository; it does not encrypt the values.
