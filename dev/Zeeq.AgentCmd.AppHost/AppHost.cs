var builder = DistributedApplication.CreateBuilder(args);
var proofProject = Path.GetFullPath(
    Path.Combine(
        builder.AppHostDirectory,
        "../../tests/AgentCmd.LangfuseReadback/AgentCmd.LangfuseReadback.csproj"
    )
);

// Why explicit: this optional six-container stack is heavy; ordinary builds and the
// AppHost itself must not start it. Aspire represents the tested Compose stack as one
// executable resource; its stores are managed by the pinned compose.yml, not separately
// by Aspire. The health check ensures `aspire wait langfuse` waits for Langfuse itself.
// The local proof generates isolated project keys; model-provider keys never enter this host.
// See tests/AgentCmd.LangfuseReadback/README.md (OTLP/readback and pricing cross-check),
// src/Zeeq.AgentCmd.Providers/Pricing (token estimates), and
// src/Zeeq.AgentCmd/Commands/Composition (aggregate execution and evaluation).
builder
    .AddExecutable(
        "langfuse",
        "dotnet",
        builder.AppHostDirectory,
        "run",
        "--project",
        proofProject,
        "--",
        "serve"
    )
    .WithExplicitStart()
    .WithHttpEndpoint(port: 38941, isProxied: false)
    .WithHttpHealthCheck("/api/public/health");

builder.Build().Run();
