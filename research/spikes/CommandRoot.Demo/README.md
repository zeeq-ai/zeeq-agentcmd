# Minimal generated command

[ExtractInvoice.cs](ExtractInvoice.cs) is the full authored command: two ordinary records, one typed partial class, a stable ID and a template. The generator supplies activation, descriptor data, rendering, JSON contract rules and registration. The executor stays inside the runtime library.

The [project file](CommandRoot.Demo.csproj) imports `../CommandRoot/build/LlmCmd.Precompile.targets`. That step writes the serializer-context input before compilation, so no app-authored `JsonSerializable` context is needed. The demo also disables reflection-based JSON serialization.

[Program.cs](Program.cs) creates one host runtime and calls the generated `AddLlmCommandsFromCommandRoot_Demo(runtime)` registration extension. Registration validates contracts and default profiles. The injected command accepts typed parameter values and infers the result type at `ExecuteAsync`. A single host profile supplies the default; call sites need no profile selection unless they override it.

```sh
/tmp/opencode/llmcmd-dotnet11/dotnet run --project spikes/CommandRoot.Demo/CommandRoot.Demo.csproj
```

The fake MEAI client returns an invoice. The program checks the typed value, optional constructor default, required schema members and stored output. A second response omits a required member and must return `InvalidOutput`. It makes no network call. This demonstrates generated metadata with reflection disabled; it does not test a real provider or a NativeAOT deployment.

See [CommandRoot](../CommandRoot/README.md) for requiredness rules, explicit serializer overrides, optional partial hooks, typed profile selection and prototype limits.
