namespace AgentCmd.LiveProviders.Tests;

internal static class EvidencePaths
{
    internal static string BeginRun(string fileName)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var target = Path.Combine(Directory, fileName);
        if (File.Exists(target))
        {
            // Preserve prior accounting, but do not let a failed new run leave
            // its predecessor looking like the latest successful verification.
            var history = Path.Combine(Directory, "history");
            System.IO.Directory.CreateDirectory(history);
            File.Move(target, Path.Combine(history, Guid.NewGuid().ToString("N") + "." + fileName));
        }
        return target;
    }

    // TUnit can set its working directory to bin/. Keep sanitized receipts in
    // one ignored repository location across CLI, mise and IDE launches.
    internal static string Directory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("AGENTCMD_LIVE_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            for (
                var current = new DirectoryInfo(AppContext.BaseDirectory);
                current is not null;
                current = current.Parent
            )
            {
                if (File.Exists(Path.Combine(current.FullName, "Zeeq.AgentCmd.slnx")))
                {
                    return Path.Combine(current.FullName, "artifacts", "live-providers");
                }
            }

            throw new InvalidOperationException(
                "Set AGENTCMD_LIVE_EVIDENCE when running outside the source checkout."
            );
        }
    }
}
