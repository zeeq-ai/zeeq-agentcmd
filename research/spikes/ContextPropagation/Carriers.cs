using System.Collections.ObjectModel;
using System.Text;

namespace LlmCmd.ContextPropagation;

public sealed record HeaderValue(string Name, string Value);
public sealed class HeaderValues
{
    public IReadOnlyList<HeaderValue> Entries { get; }
    public HeaderValues(IEnumerable<HeaderValue> entries) => Entries = Array.AsReadOnly(entries.ToArray());
    public IEnumerable<string> GetAll(string key) => Entries.Where(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase)).Select(e => e.Value);
    public static HeaderValues Empty { get; } = new([]);
}

/// <summary>Read all raw occurrences. Create a new carrier; do not partially mutate an existing message.</summary>
public interface ICarrierCodec<TCarrier>
{
    HeaderValues Read(TCarrier carrier);
    TCarrier Create(HeaderValues headers);
}

public sealed class TextCarrierCodec : ICarrierCodec<HeaderValues>
{
    public HeaderValues Read(HeaderValues carrier) => new(carrier.Entries);
    public HeaderValues Create(HeaderValues headers) => new(headers.Entries);
}

/// <summary>Example binary/object header binding. Unsupported types and malformed UTF-8 fail closed.</summary>
public sealed class BinaryCarrierCodec : ICarrierCodec<IReadOnlyList<KeyValuePair<string, object>>>
{
    static readonly UTF8Encoding utf8 = new(false, true);
    public HeaderValues Read(IReadOnlyList<KeyValuePair<string, object>> carrier) => new(carrier.Select(h => new HeaderValue(h.Key, h.Value switch
    {
        string s => s,
        byte[] bytes => utf8.GetString(bytes),
        ReadOnlyMemory<byte> bytes => utf8.GetString(bytes.Span),
        _ => throw new FormatException("Unsupported header representation")
    })));
    public IReadOnlyList<KeyValuePair<string, object>> Create(HeaderValues headers) =>
        new ReadOnlyCollection<KeyValuePair<string, object>>(headers.Entries.Select(e =>
            new KeyValuePair<string, object>(e.Name, utf8.GetBytes(e.Value))).ToArray());
}

public sealed record PropagationLimits(int MaxBytes = 16_384, int MaxEntries = 64,
    int MaxValueBytes = 8_192, int MaxEnvelopeBytes = 65_536);
public enum IngressPolicy { None, Auxiliary, Component }
