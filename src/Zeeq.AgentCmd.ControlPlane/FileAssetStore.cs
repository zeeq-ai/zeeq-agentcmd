using System.Security.Cryptography;
using System.Text;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Persistent local immutable assets with independent namespace paths and verification on read.</summary>
/// <param name="root">Application-owned persistent disk directory.</param>
/// <param name="maximumBytes">Per-asset admission bound.</param>
public sealed class FileAssetStore(string root, long maximumBytes = 32 * 1024 * 1024)
    : IControlPlaneAssetStore
{
    /// <inheritdoc />
    public async Task<StoredAsset> PutAsync(
        WorkspaceScope scope,
        Stream source,
        CancellationToken cancellationToken = default
    )
    {
        var directory = DirectoryFor(scope);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            long length = 0;
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (
                var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    true
                )
            )
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    length += read;
                    if (length > maximumBytes)
                    {
                        throw new ManagementException("asset_too_large", 413);
                    }
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            var hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            var destination = Path.Combine(directory, hash);
            try
            {
                File.Move(temporary, destination, false);
            }
            catch (IOException) when (File.Exists(destination))
            {
                // A concurrent identical upload is safe only when the retained bytes verify.
                await using var existing = await OpenAsync(scope, hash, cancellationToken);
                if (existing.Length != length)
                {
                    throw new ManagementException("asset_corrupt", 409);
                }
            }
            return new(hash, length);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenAsync(
        WorkspaceScope scope,
        string hash,
        CancellationToken cancellationToken = default
    )
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
        {
            throw new ManagementException("asset_identity_invalid");
        }
        var path = Path.Combine(DirectoryFor(scope), hash);
        if (!File.Exists(path))
        {
            throw new ManagementException("asset_not_retained", 404);
        }
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            true
        );
        try
        {
            var actual = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken)
            );
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                throw new ManagementException("asset_corrupt", 409);
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private string DirectoryFor(WorkspaceScope scope) =>
        Path.Combine(
            Path.GetFullPath(root),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scope.Key)))
        );
}
