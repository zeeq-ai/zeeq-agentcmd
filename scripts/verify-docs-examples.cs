using System.Diagnostics;
using System.Xml.Linq;

// The documentation is the source of truth: extracting named fences makes the
// package-only check fail when an example drifts, rather than testing a copy.
var repository = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var packageDirectory = Path.Combine(repository, "artifacts", "packages");
var packageNames = new[]
{
    "Zeeq.LlmCmd.0.1.0-alpha.1.nupkg",
    "Zeeq.LlmCmd.Providers.0.1.0-alpha.1.nupkg",
    "Zeeq.LlmCmd.OpenTelemetry.0.1.0-alpha.1.nupkg",
};
foreach (var name in packageNames)
{
    var path = Path.Combine(packageDirectory, name);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException("Run mise run pack before validating the docs.", path);
    }
}

var tempParent =
    OperatingSystem.IsMacOS() && Directory.Exists("/private/tmp")
        ? "/private/tmp"
        : Path.GetTempPath();
var tempRoot = Path.Combine(tempParent, "llmcmd-docs-" + Guid.NewGuid().ToString("N"));
var app = Path.Combine(tempRoot, "my-app");
var feed = Path.Combine(tempRoot, "artifacts", "packages");
var packages = Path.Combine(tempRoot, "nuget-cache");
Directory.CreateDirectory(app);
Directory.CreateDirectory(feed);

try
{
    foreach (var name in packageNames)
    {
        File.Copy(Path.Combine(packageDirectory, name), Path.Combine(feed, name));
    }
    File.Copy(Path.Combine(repository, "global.json"), Path.Combine(tempRoot, "global.json"));
    // Mirror the documented in-checkout layout, including its inherited central package settings.
    File.Copy(
        Path.Combine(repository, "Directory.Packages.props"),
        Path.Combine(tempRoot, "Directory.Packages.props")
    );
    var installation = Path.Combine(
        repository,
        "docs",
        "content",
        "1.getting-started",
        "2.installation.md"
    );
    var quickStart = Path.Combine(
        repository,
        "docs",
        "content",
        "1.getting-started",
        "3.quick-start.md"
    );
    var serialization = Path.Combine(
        repository,
        "docs",
        "content",
        "2.guides",
        "6.serialization-and-nativeaot.md"
    );
    var projectText = Fence(installation, "xml", "my-app/my-app.csproj");
    var project = XDocument.Parse(projectText);
    if (project.Descendants("ProjectReference").Any())
    {
        throw new InvalidOperationException("The installation project must be NuGet-only.");
    }

    File.WriteAllText(Path.Combine(app, "my-app.csproj"), projectText);
    File.WriteAllText(
        Path.Combine(app, "NuGet.Config"),
        Fence(installation, "xml", "my-app/NuGet.Config")
    );
    File.WriteAllText(
        Path.Combine(app, "ExtractInvoice.cs"),
        Fence(quickStart, "csharp", "ExtractInvoice.cs")
    );
    File.WriteAllText(Path.Combine(app, "Program.cs"), Fence(quickStart, "csharp", "Program.cs"));
    // This additive probe checks the build option without editing the excerpt under test.
    File.WriteAllText(
        Path.Combine(app, "ReflectionProbe.cs"),
        """
        using System.Runtime.CompilerServices;
        using System.Text.Json;
        internal static class ReflectionProbe
        {
            [ModuleInitializer]
            internal static void Check()
            {
                if (JsonSerializer.IsReflectionEnabledByDefault)
                {
                    throw new InvalidOperationException("JSON reflection must be disabled.");
                }
            }
        }
        """
    );

    await Run(
        app,
        "build",
        "my-app.csproj",
        "-p:TreatWarningsAsErrors=true",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:RestorePackagesPath=" + packages
    );
    var output = await Run(app, "run", "--no-build", "--project", "my-app.csproj");
    if (!output.Contains("12.50 USD", StringComparison.Ordinal))
    {
        throw new InvalidOperationException(
            "The exact quick-start program did not return the expected invoice."
        );
    }

    File.WriteAllText(
        Path.Combine(app, "ExplicitContext.cs"),
        Fence(serialization, "csharp", "ExplicitContext.cs")
    );
    await Run(
        app,
        "build",
        "my-app.csproj",
        "-p:TreatWarningsAsErrors=true",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:RestorePackagesPath=" + packages
    );
    var support = Path.Combine(
        repository,
        "docs",
        "content",
        "3.examples",
        "5.support-classification.md"
    );
    File.WriteAllText(
        Path.Combine(app, "SupportCommand.cs"),
        Fence(support, "csharp", "SupportCommand.cs")
    );
    await Run(
        app,
        "build",
        "my-app.csproj",
        "-p:TreatWarningsAsErrors=true",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:RestorePackagesPath=" + packages
    );
    var outcomes = Path.Combine(
        repository,
        "docs",
        "content",
        "2.guides",
        "2.outcomes-and-context.md"
    );
    File.WriteAllText(
        Path.Combine(app, "OutcomeInspector.cs"),
        Fence(outcomes, "csharp", "OutcomeInspector.cs")
    );
    var composition = Path.Combine(repository, "docs", "content", "2.guides", "3.composition.md");
    var bootstrap = Fence(composition, "csharp", "CompositionBootstrap.cs");
    var commands = File.ReadAllText(
        Path.Combine(repository, "examples", "Zeeq.LlmCmd.CompositionExample", "Commands.cs")
    );
    const string headerMarker =
        "public sealed partial class ExtractHeader : PromptCommand<SourceText, Header>";
    var headerStart = commands.IndexOf(headerMarker, StringComparison.Ordinal);
    var headerEnd =
        headerStart < 0 ? -1 : commands.IndexOf("\n}", headerStart, StringComparison.Ordinal);
    if (headerEnd < 0)
    {
        throw new InvalidOperationException("Could not locate the example's header command.");
    }

    // Compile the documentation's exact header declaration with the example's other DTOs and leaves.
    var header = Fence(composition, "csharp", "ExtractHeader.cs").TrimEnd();
    var compositionCommands = commands[..headerStart] + header + commands[(headerEnd + 2)..];
    File.WriteAllText(Path.Combine(app, "CompositionCommands.cs"), compositionCommands);
    File.WriteAllText(
        Path.Combine(app, "BootstrapValidation.cs"),
        """
        using CompositionDemo;
        using Microsoft.Extensions.AI;
        using Microsoft.Extensions.DependencyInjection;
        using Zeeq.LlmCmd;
        using Zeeq.LlmCmd.Generated;
        internal static class BootstrapValidation
        {
            internal static void Validate(IChatClient client)
            {
        """
            + Environment.NewLine
            + bootstrap
            + Environment.NewLine
            + """
                }
            }
            """
    );
    await Run(
        app,
        "build",
        "my-app.csproj",
        "-p:TreatWarningsAsErrors=true",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:RestorePackagesPath=" + packages
    );

    // Validate the published telemetry fences in a second NuGet-only app. It
    // exercises the capture pipeline and ledger without contacting a collector.
    var telemetryApp = Path.Combine(tempRoot, "telemetry-app");
    Directory.CreateDirectory(telemetryApp);
    var packageGroup = project.Descendants("PackageReference").Single().Parent!;
    packageGroup.Add(
        new XElement(
            "PackageReference",
            new XAttribute("Include", "Zeeq.LlmCmd.Providers"),
            new XAttribute("Version", "0.1.0-alpha.1")
        ),
        new XElement(
            "PackageReference",
            new XAttribute("Include", "Zeeq.LlmCmd.OpenTelemetry"),
            new XAttribute("Version", "0.1.0-alpha.1")
        )
    );
    project.Save(Path.Combine(telemetryApp, "telemetry-app.csproj"));
    File.Copy(Path.Combine(app, "NuGet.Config"), Path.Combine(telemetryApp, "NuGet.Config"));
    File.Copy(
        Path.Combine(app, "ReflectionProbe.cs"),
        Path.Combine(telemetryApp, "ReflectionProbe.cs")
    );
    File.WriteAllText(
        Path.Combine(telemetryApp, "ExtractInvoice.cs"),
        Fence(quickStart, "csharp", "ExtractInvoice.cs")
    );
    var telemetryExample = Path.Combine(
        repository,
        "docs",
        "content",
        "3.examples",
        "6.telemetry-and-evals.md"
    );
    var telemetryGuide = Path.Combine(
        repository,
        "docs",
        "content",
        "2.guides",
        "5.accounting-and-observability.md"
    );
    File.WriteAllText(
        Path.Combine(telemetryApp, "CaptureSetup.cs"),
        Fence(telemetryExample, "csharp", "CaptureSetup.cs")
    );
    File.WriteAllText(
        Path.Combine(telemetryApp, "EvaluationCapture.cs"),
        Fence(telemetryExample, "csharp", "EvaluationCapture.cs")
    );
    File.WriteAllText(
        Path.Combine(telemetryApp, "Program.cs"),
        Fence(telemetryExample, "csharp", "TelemetryProgram.cs")
    );
    File.WriteAllText(
        Path.Combine(telemetryApp, "TelemetryExport.cs"),
        Fence(telemetryGuide, "csharp", "TelemetryExport.cs")
    );
    await Run(
        telemetryApp,
        "build",
        "telemetry-app.csproj",
        "-p:TreatWarningsAsErrors=true",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:RestorePackagesPath=" + packages
    );
    var telemetryOutput = await Run(
        telemetryApp,
        "run",
        "--no-build",
        "--project",
        "telemetry-app.csproj"
    );
    if (
        !telemetryOutput.Contains(
            "Prompt: invoice.extract; input tokens: 7",
            StringComparison.Ordinal
        )
        || !telemetryOutput.Contains(
            "Template: Extract the invoice from {Document}.",
            StringComparison.Ordinal
        )
        || !telemetryOutput.Contains("Document: Invoice for USD 12.50", StringComparison.Ordinal)
        || !telemetryOutput.Contains("Stored output:", StringComparison.Ordinal)
        || !telemetryOutput.Contains(
            "The exact stored total was read and graded.",
            StringComparison.Ordinal
        )
    )
    {
        throw new InvalidOperationException("The exact telemetry example did not pass.");
    }

    Console.WriteLine(
        "Exact quick-start and telemetry examples ran; context, support, outcome, composition and exporter fences compiled; JSON reflection is disabled."
    );
}
finally
{
    Directory.Delete(tempRoot, recursive: true);
}

static string Fence(string file, string language, string label)
{
    var lines = File.ReadAllLines(file);
    var prefix = "```" + language + " [" + label + "]";
    var starts = Enumerable
        .Range(0, lines.Length)
        .Where(index => lines[index].StartsWith(prefix, StringComparison.Ordinal))
        .ToArray();
    if (starts.Length != 1)
    {
        throw new InvalidOperationException(
            $"Expected one {language} fence named '{label}' in {file}."
        );
    }

    var start = starts[0] + 1;
    var end = Array.FindIndex(lines, start, line => line == "```");
    if (end < 0)
    {
        throw new InvalidOperationException($"Unclosed fence '{label}' in {file}.");
    }

    return string.Join(Environment.NewLine, lines[start..end]) + Environment.NewLine;
}

static async Task<string> Run(string workingDirectory, params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    start.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process =
        Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await stdout;
    var errors = await stderr;
    Console.Write(output);
    Console.Error.Write(errors);
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"dotnet {arguments[0]} failed with exit code {process.ExitCode}."
        );
    }

    return output;
}
