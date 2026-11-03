#:sdk Aspire.AppHost.Sdk@13.6.4
#:property AspireUseCliBundle=true
#:package CommunityToolkit.Aspire.Hosting.OpenTelemetryCollector@13.6.1

var builder = DistributedApplication.CreateBuilder(args);
var collector = builder.AddOpenTelemetryCollector("collector", settings => { settings.ForceNonSecureReceiver = true; settings.CollectorTag = "0.138.0"; settings.Registry = "docker.io"; settings.Image = "otel/opentelemetry-collector-contrib"; })
    .WithConfig("./collector.yaml")
    .WithBindMount("/tmp/opencode/llmcmd-otel", "/evidence");
builder.AddProject("producer", "./Producer/Producer.csproj")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("CAPTURE_OTLP", collector.GetEndpoint("grpc"))
    .WaitFor(collector);
builder.Build().Run();
