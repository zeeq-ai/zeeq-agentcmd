# TUnit interface and execution tests

This project targets .NET 11/C# 15 and pins TUnit 1.78.0. It runs with Microsoft.Testing.Platform. The original 30 runtime contracts are individual TUnit tests; additional tests exercise snapshot, cost, collector, executor, and generated command outputs. Tests use isolated fake providers. The telemetry tests run alone because their listeners observe a process-wide meter and activity source.

With the isolated SDK installed at the location used in this research spike:

```sh
/tmp/opencode/llmcmd-dotnet11/dotnet build tests/LlmCmd.Tests/LlmCmd.Tests.csproj
TUNIT_ENABLE_JUNIT_REPORTER=1 /tmp/opencode/llmcmd-dotnet11/dotnet run --no-build --project tests/LlmCmd.Tests/LlmCmd.Tests.csproj -- --junit-output-path /tmp/opencode/llmcmd-tunit.xml
```

The second command writes a JUnit XML report at the specified absolute path. `dotnet run` uses MTP's executable test runner; arguments after `--` are runner arguments. The separate TypedCommands Python compiler cases still exercise compile-negative behavior.

References: [TUnit documentation index](https://tunit.dev/llms.txt), [assertions](https://tunit.dev/docs/assertions/getting-started.md), [parallelism](https://tunit.dev/docs/execution/parallelism.md), and [command-line flags](https://tunit.dev/docs/reference/command-line-flags.md). TUnit version was checked against the [NuGet package](https://www.nuget.org/packages/TUnit/1.78.0).
