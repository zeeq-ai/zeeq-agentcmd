using System.Diagnostics;
using System.Security.Cryptography;

internal static class LocalLangfuse
{
    internal static string State =>
        Environment.GetEnvironmentVariable("LLMCMD_LANGFUSE_STATE") ?? "/tmp/zeeq-llmcmd-langfuse";
    private const string Project = "zeeq-llmcmd-langfuse-test";
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDir = PrivateFile | UnixFileMode.UserExecute;

    internal static async Task<int> RunAsync(string action, bool waitForHealth = true)
    {
        if (OperatingSystem.IsWindows())
        {
            return Fail("This local test requires a Unix host.");
        }

        var portText = Environment.GetEnvironmentVariable("LLMCMD_LANGFUSE_PORT") ?? "38941";
        if (!int.TryParse(portText, out var port) || port is < 1024 or > 65535)
        {
            return Fail("Invalid local port.");
        }

        var directory = new DirectoryInfo(State);
        if (
            directory.Exists
            && (
                directory.LinkTarget is not null || (File.GetUnixFileMode(State) & ~PrivateDir) != 0
            )
        )
        {
            return Fail("State directory must be private and not a symlink.");
        }

        Directory.CreateDirectory(State, PrivateDir);
        var envFile = Path.Combine(State, ".env");
        if (action == "start" && !File.Exists(envFile))
        {
            string Token() =>
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var pg = Token();
            var values = new Dictionary<string, string>
            {
                ["LLMCMD_LANGFUSE_PORT"] = port.ToString(),
                ["NEXTAUTH_URL"] = $"http://127.0.0.1:{port}",
                ["POSTGRES_PASSWORD"] = pg,
                ["DATABASE_URL"] = $"postgresql://postgres:{pg}@postgres:5432/postgres",
                ["SALT"] = Token(),
                ["ENCRYPTION_KEY"] = Convert
                    .ToHexString(RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant(),
                ["NEXTAUTH_SECRET"] = Token(),
                ["CLICKHOUSE_PASSWORD"] = Token(),
                ["REDIS_AUTH"] = Token(),
                ["MINIO_PASSWORD"] = Token(),
                ["LANGFUSE_INIT_USER_PASSWORD"] = Token(),
                ["LANGFUSE_INIT_PROJECT_PUBLIC_KEY"] = "pk-lf-" + Token(),
                ["LANGFUSE_INIT_PROJECT_SECRET_KEY"] = "sk-lf-" + Token(),
            };
            using var file = new FileStream(
                envFile,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = PrivateFile,
                }
            );
            using var writer = new StreamWriter(file);
            foreach (var (key, value) in values)
            {
                writer.WriteLine($"{key}={value}");
            }
        }
        if (
            !File.Exists(envFile)
            || new FileInfo(envFile).LinkTarget is not null
            || (File.GetUnixFileMode(envFile) & ~PrivateFile) != 0
        )
        {
            return Fail("Private local state is missing or invalid.");
        }

        if (
            action == "start"
            && !File.ReadLines(envFile).Any(x => x == "LLMCMD_LANGFUSE_PORT=" + port)
        )
        {
            return Fail("State uses another port; stop it first.");
        }

        var process = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Do not pass unrelated environment values to Compose interpolation.
        var env = new[] { "PATH", "HOME", "DOCKER_HOST", "DOCKER_CONFIG" }
            .Select(k => (k, v: Environment.GetEnvironmentVariable(k)))
            .Where(x => x.v is not null)
            .ToArray();
        process.Environment.Clear();
        foreach (var (k, v) in env)
        {
            process.Environment[k] = v;
        }

        foreach (
            var arg in new[]
            {
                "compose",
                "--project-name",
                Project,
                "--env-file",
                envFile,
                "-f",
                Path.Combine(AppContext.BaseDirectory, "compose.yml"),
            }
        )
        {
            process.ArgumentList.Add(arg);
        }

        foreach (
            var arg in action == "start"
                ? new[] { "up", "-d" }
                : new[] { "down", "--volumes", "--remove-orphans" }
        )
        {
            process.ArgumentList.Add(arg);
        }

        try
        {
            using var child = Process.Start(process);
            if (child is null)
            {
                return Fail("Unable to start Docker Compose.");
            }

            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr); // Never print Compose diagnostics, which can contain configuration.
            if (child.ExitCode != 0)
            {
                return Fail(
                    "Dedicated test project operation failed; check Docker availability and image access."
                );
            }
        }
        catch (Exception)
        {
            return Fail("Unable to run Docker Compose.");
        }
        if (action == "stop")
        {
            File.Delete(envFile);
            Console.WriteLine("Dedicated Langfuse test project stopped and its data removed.");
            return 0;
        }
        // Aspire owns readiness checks when this process runs as its explicit-start resource.
        if (!waitForHealth)
        {
            return 0;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 120; i++)
        {
            try
            {
                using var result = await http.GetAsync(
                    $"http://127.0.0.1:{port}/api/public/health"
                );
                if (result.IsSuccessStatusCode)
                {
                    Console.WriteLine(
                        $"Dedicated Langfuse test instance ready on 127.0.0.1:{port}."
                    );
                    return 0;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(2000);
        }
        return Fail("Dedicated test instance did not become ready; run stop to remove it.");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
