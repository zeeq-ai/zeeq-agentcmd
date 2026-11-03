#:sdk Aspire.AppHost.Sdk@13.6.4
#:property AspireUseCliBundle=true
#:package Aspire.Hosting.RabbitMQ@13.6.4
#:package CommunityToolkit.Aspire.Hosting.OpenTelemetryCollector@13.6.1

var builder = DistributedApplication.CreateBuilder(args);
var rabbit = builder.AddRabbitMQ("queue").WithImageTag("management");
var collector = builder.AddOpenTelemetryCollector("collector", settings =>
{
    settings.ForceNonSecureReceiver = true;
    settings.CollectorTag = "0.138.0";
    settings.Registry = "docker.io";
    settings.Image = "otel/opentelemetry-collector-contrib";
}).WithConfig("../Observability/collector.yaml")
  .WithBindMount("/tmp/opencode/llmcmd-propagation/otel", "/evidence");

// HTTP permits inspecting the exact unencrypted loopback wire in this local-only proof.
var edge = builder.AddProject("edge", "./Node/Node.csproj")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ROLE", "edge")
    .WithEnvironment("CAPTURE_OTLP", collector.GetEndpoint("grpc"))
    .WithReference(rabbit).WaitFor(rabbit).WaitFor(collector);

var worker = builder.AddProject("worker", "./Node/Node.csproj")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ROLE", "worker")
    .WithEnvironment("EDGE_URL", edge.GetEndpoint("http"))
    .WithEnvironment("CAPTURE_OTLP", collector.GetEndpoint("grpc"))
    .WithReference(rabbit).WaitFor(rabbit).WaitFor(edge);
edge.WithEnvironment("WORKER_URL", worker.GetEndpoint("http"));
builder.Build().Run();
