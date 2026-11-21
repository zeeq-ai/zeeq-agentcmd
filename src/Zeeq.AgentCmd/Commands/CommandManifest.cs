using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeeq.AgentCmd;

/// <summary>
/// Portable generated catalog data, independent of command construction and application startup.
/// Its digest identifies the exact UTF-8 document; it does not authenticate a publisher or prove deployment.
/// </summary>
public sealed class CommandManifest
{
    private CommandManifest(string json, JsonElement content)
    {
        Json = json;
        Content = content;
        FormatVersion = content.GetProperty("formatVersion").GetInt32();
        CatalogId = content.GetProperty("catalogId").GetString()!;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>The exact supplied JSON; generated documents omit timestamps and machine-specific paths.</summary>
    public string Json { get; }

    /// <summary>Lowercase SHA-256 of <see cref="Json"/> encoded as UTF-8 without a BOM. Generated documents have no trailing newline.</summary>
    public string Sha256 { get; }

    /// <summary>Version of the portable representation, independently of command and build revisions.</summary>
    public int FormatVersion { get; }

    /// <summary>Generated assembly/catalog identity; the registering host supplies application and build authority.</summary>
    public string CatalogId { get; }

    /// <summary>Owned JSON content for registration without reflection or loading application assemblies.</summary>
    public JsonElement Content { get; }

    /// <summary>
    /// Reads a generated manifest envelope without resolving any CLR types or executing validators.
    /// Consumers must still authorize registration and perform typed preflight against the selected build.
    /// </summary>
    /// <param name="json">Exact UTF-8-representable JSON content in manifest format version one.</param>
    /// <returns>An immutable envelope and digest of the supplied representation.</returns>
    /// <exception cref="JsonException">The JSON or required version-one envelope is invalid.</exception>
    /// <exception cref="ArgumentNullException">The content is null.</exception>
    public static CommandManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("formatVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var formatVersion)
            || formatVersion != 1
            || !root.TryGetProperty("catalogId", out var catalog)
            || catalog.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(catalog.GetString())
            || !root.TryGetProperty("commands", out var commands)
            || commands.ValueKind != JsonValueKind.Array
        )
        {
            throw new JsonException("Unsupported or incomplete command manifest envelope.");
        }

        return new(json, root.Clone());
    }
}
