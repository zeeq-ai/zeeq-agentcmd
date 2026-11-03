#:sdk Aspire.AppHost.Sdk@13.6.4
#:property AspireUseCliBundle=true
#:property UserSecretsId=zeeq-llmcmd-secret-propagation-spike

var builder = DistributedApplication.CreateBuilder(args);
var placeholder = builder.AddParameter("secret-propagation-placeholder", secret: true);
var evidence = builder.Configuration["SpikeEvidenceDirectory"]
    ?? throw new InvalidOperationException("Run scripts/verify_secret_propagation.py");
var digest = builder.Configuration["SpikeExpectedSha256"]
    ?? throw new InvalidOperationException("Expected placeholder digest is required");

foreach (var name in new[] { "receiving-tests", "control-tests" })
{
    // Both are real TUnit processes. Explicit startup keeps a build or AppHost
    // start from running tests; the verification driver starts them individually.
    var tests = builder.AddProject(name,
            "../../tests/LlmCmd.SecretPropagation.Tests/LlmCmd.SecretPropagation.Tests.csproj")
        .WithEnvironment("SPIKE_RESOURCE", name)
        .WithEnvironment("SPIKE_EXPECTED_SHA256", digest)
        .WithEnvironment("SPIKE_START_GATE", Path.Combine(evidence, name + ".gate"))
        .WithEnvironment("TUNIT_ENABLE_JUNIT_REPORTER", "1")
        .WithArgs("--junit-output-path", Path.Combine(evidence, name + ".xml"))
        .WithExplicitStart();

    // Having the same OS user and AppHost does not grant every child the secret.
    // Only this binding transfers its resolved value to the receiving process.
    if (name == "receiving-tests")
        tests.WithEnvironment("LLMCMD_SPIKE_SECRET", placeholder);
}

builder.Build().Run();
