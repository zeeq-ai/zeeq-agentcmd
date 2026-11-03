using System.Collections.ObjectModel;
using System.Text;
using Zeeq.LlmCmd;

namespace Zeeq.LlmCmd.OpenTelemetry;

/// <summary>One raw header occurrence, including duplicates.</summary>
/// <param name="Name">The transport header name.</param>
/// <param name="Value">The decoded header value.</param>
public sealed record HeaderValue(string Name, string Value);

/// <summary>An immutable snapshot of raw header occurrences.</summary>
public sealed class HeaderValues
{
    /// <summary>Header occurrences in their original order.</summary>
    public IReadOnlyList<HeaderValue> Entries { get; }

    /// <summary>Copies the supplied header occurrences into an immutable snapshot.</summary>
    public HeaderValues(IEnumerable<HeaderValue> entries) =>
        Entries = Array.AsReadOnly(entries.ToArray());

    /// <summary>Enumerates all values with a case-insensitive matching name.</summary>
    public IEnumerable<string> GetAll(string key) =>
        Entries
            .Where(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value);

    /// <summary>A carrier snapshot with no headers.</summary>
    public static HeaderValues Empty { get; } = new([]);
}

/// <summary>Read all raw occurrences. Create a new carrier; do not partially mutate an existing message.</summary>
public interface ICarrierCodec<TCarrier>
{
    /// <summary>Reads every header occurrence from a transport carrier.</summary>
    HeaderValues Read(TCarrier carrier);

    /// <summary>Creates a new transport carrier from the supplied headers.</summary>
    TCarrier Create(HeaderValues headers);
}

/// <summary>Copies text header snapshots without changing their order or duplicates.</summary>
public sealed class TextCarrierCodec : ICarrierCodec<HeaderValues>
{
    /// <inheritdoc />
    public HeaderValues Read(HeaderValues carrier) => new(carrier.Entries);

    /// <inheritdoc />
    public HeaderValues Create(HeaderValues headers) => new(headers.Entries);
}

/// <summary>Example binary/object header binding. Unsupported types and malformed UTF-8 fail closed.</summary>
public sealed class BinaryCarrierCodec : ICarrierCodec<IReadOnlyList<KeyValuePair<string, object>>>
{
    static readonly UTF8Encoding utf8 = new(false, true);

    /// <inheritdoc />
    public HeaderValues Read(IReadOnlyList<KeyValuePair<string, object>> carrier) =>
        new(
            carrier.Select(h => new HeaderValue(
                h.Key,
                h.Value switch
                {
                    string s => s,
                    byte[] bytes => utf8.GetString(bytes),
                    ReadOnlyMemory<byte> bytes => utf8.GetString(bytes.Span),
                    _ => throw new FormatException("Unsupported header representation"),
                }
            ))
        );

    /// <inheritdoc />
    public IReadOnlyList<KeyValuePair<string, object>> Create(HeaderValues headers) =>
        new ReadOnlyCollection<KeyValuePair<string, object>>(
            headers
                .Entries.Select(e => new KeyValuePair<string, object>(
                    e.Name,
                    utf8.GetBytes(e.Value)
                ))
                .ToArray()
        );
}

/// <summary>Bounds untrusted carrier and work-envelope sizes before parsing.</summary>
/// <param name="MaxBytes">Maximum combined UTF-8 bytes in header names and values.</param>
/// <param name="MaxEntries">Maximum number of raw header occurrences.</param>
/// <param name="MaxValueBytes">Maximum UTF-8 bytes in one header value.</param>
/// <param name="MaxEnvelopeBytes">Maximum serialized work-envelope bytes.</param>
public sealed record PropagationLimits(
    int MaxBytes = 16_384,
    int MaxEntries = 64,
    int MaxValueBytes = 8_192,
    int MaxEnvelopeBytes = 65_536
);

/// <summary>Controls whether private context and work envelopes are accepted on ingress.</summary>
public enum IngressPolicy
{
    /// <summary>Ignore incoming propagation data.</summary>
    None,

    /// <summary>Accept optional context or a work envelope.</summary>
    Auxiliary,

    /// <summary>Require a work envelope with a component slot.</summary>
    Component,
}
