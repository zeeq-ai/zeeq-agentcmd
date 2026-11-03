using System.Diagnostics;
using System.Xml.Linq;

// The documentation is the source of truth: extracting named fences makes the
// package-only check fail when an example drifts, rather than testing a copy.
var repository = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
var package = Path.Combine(repository, "artifacts", "packages", "Zeeq.LlmCmd.0.1.0-alpha.1.nupkg");
if (!File.Exists(package))
{
    throw new FileNotFoundException(
        "Build the local alpha package before validating the docs.",
        package
    );
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
    File.Copy(package, Path.Combine(feed, Path.GetFileName(package)));
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
    Console.WriteLine(
        "Exact quick-start fences ran; explicit context, support command, outcome inspector, header command, and composition bootstrap compiled; JSON reflection is disabled."
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
