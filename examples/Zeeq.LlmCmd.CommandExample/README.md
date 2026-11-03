# Generated command example

Run `mise run demo:command` from the repository root. This application references the actual `src/Zeeq.LlmCmd` library. It uses an offline model client and disables reflection serialization.

[ExtractInvoice.cs](ExtractInvoice.cs) contains the full authoring contract: ordinary input/result records, a typed partial command, a stable ID and one template. The command has no authored constructor, serializer context, JSON schema or registration table.

[Program.cs](Program.cs) configures the host with `AddLlmCmd`, registers the application's generated catalog with `AddGeneratedCommands`, resolves the typed command from DI, creates an immutable invocation with `WithParameters(...)`, and calls `ExecuteAsync()` to start work. It verifies the result and delivery, optional constructor defaults, schema requiredness, and rejection of a missing required field.

The host provides the MEAI `IChatClient` and actor accessor. Its sole configured profile is the default. Configuration on an invocation returns a copy; use the returned chain. The DI runtime owns the client produced by its factory.

The [project](Zeeq.LlmCmd.CommandExample.csproj) selects source-checkout generation with `LlmCmdGenerateCommands`. A NuGet consumer receives the same analyzer and build target automatically from the core package; the package integration test verifies this outside the repository.

See the [library README](../../README.md) and [foundation decisions](../../docs/architecture/library-foundation.md) for the supported API and honest alpha limits.
