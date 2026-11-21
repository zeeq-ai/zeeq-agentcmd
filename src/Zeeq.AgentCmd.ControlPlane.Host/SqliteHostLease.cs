namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Keeps SQLite's management worker and outbox ownership inside one local host process.</summary>
internal sealed class SqliteHostLease : IDisposable
{
    private readonly FileStream? lease;

    internal SqliteHostLease(ControlPlaneOptions options)
    {
        if (options.Provider != "Sqlite")
        {
            return;
        }
        var database = Path.GetFullPath(options.DatabasePath);
        if (
            File.Exists(database)
            && (File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new Zeeq.AgentCmd.ControlPlane.ManagementException(
                "sqlite_database_alias_unsupported",
                503
            );
        }
        var path = database + ".host-lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            lease = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        catch (IOException)
        {
            throw new Zeeq.AgentCmd.ControlPlane.ManagementException(
                "sqlite_host_already_running",
                503
            );
        }
    }

    public void Dispose() => lease?.Dispose();
}
