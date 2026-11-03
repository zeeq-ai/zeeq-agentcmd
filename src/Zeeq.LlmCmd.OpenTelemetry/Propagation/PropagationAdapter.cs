using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTelemetry.Context.Propagation;
using Zeeq.LlmCmd;

namespace Zeeq.LlmCmd.OpenTelemetry;

/// <summary>Versioned work intent transported between trusted internal components.</summary>
/// <param name="Version">Envelope schema version.</param>
/// <param name="Context">Reference to the operation context, resolved and authorized on ingress.</param>
/// <param name="WorkId">Identifier of this work item.</param>
/// <param name="SlotId">Optional component slot; required for component ingress.</param>
/// <param name="Payload">Work payload owned by the consuming application.</param>
public sealed record WorkEnvelope(int Version, OperationContextReference Context, Guid WorkId,
    string? SlotId, JsonElement Payload);
/// <summary>Parsed incoming references that must be independently authenticated and resolved before use.</summary>
/// <param name="Reference">Untrusted operation reference.</param>
/// <param name="Work">Optional parsed work intent.</param>
/// <param name="Fingerprint">SHA-256 fingerprint of a parsed envelope, when present.</param>
/// <param name="Producer">Extracted producer trace context, used as a link.</param>
/// <param name="Diagnostics">Bounded notes about ignored optional data.</param>
public sealed record UntrustedIncoming(OperationContextReference Reference, WorkEnvelope? Work,
    string? Fingerprint, ActivityContext Producer, IReadOnlyList<string> Diagnostics);
/// <summary>Outcome of parsing an untrusted internal carrier.</summary>
public closed record ParseResult
{
    /// <summary>No applicable context was present or ingress was disabled.</summary>
    public sealed record Absent : ParseResult;
    /// <summary>Parsing succeeded; the reference is not yet authorization.</summary>
    /// <param name="Incoming">Untrusted context and optional work intent.</param>
    public sealed record Parsed(UntrustedIncoming Incoming) : ParseResult;
    /// <summary>The received schema version is not supported.</summary>
    /// <param name="Version">The unsupported version number.</param>
    public sealed record UnsupportedVersion(int Version) : ParseResult;
    /// <summary>The carrier or envelope did not meet the required shape.</summary>
    /// <param name="Code">A bounded failure code without carrier contents.</param>
    public sealed record Malformed(string Code) : ParseResult;
    /// <summary>Input exceeded a configured propagation limit.</summary>
    /// <param name="Limit">The name of the exceeded limit.</param>
    public sealed record LimitExceeded(string Limit) : ParseResult;
}
/// <summary>Outcome of creating a new carrier containing internal propagation data.</summary>
public closed record InjectionResult<T>
{
    /// <summary>Injection produced a new carrier.</summary>
    /// <param name="Carrier">The new transport carrier.</param>
    public sealed record Injected(T Carrier) : InjectionResult<T>;
    /// <summary>Injection was refused without mutating the original carrier.</summary>
    /// <param name="Code">A bounded reason for rejection.</param>
    public sealed record Rejected(string Code) : InjectionResult<T>;
}

/// <summary>Encodes internal references and trace context without installing global baggage or changing a global propagator.</summary>
/// <remarks>References are not authorization. The receiver must authenticate its actor and resolve the reference independently; baggage is not a credential transport.</remarks>
public sealed class PropagationAdapter
{
    /// <summary>Private carrier header containing the versioned operation reference.</summary>
    public const string ContextHeader = "llmcmd-context";
    // Only these infrastructure envelopes cross the propagation boundary.
    // Their closed context keeps serialization independent of host reflection.
    static readonly PropagationJsonContext json = PropagationJsonContext.Default;
    static readonly UTF8Encoding utf8 = new(false, true);
    readonly TraceContextPropagator traces = new();
    readonly PropagationLimits limits;
    /// <summary>Creates an adapter with positive limits, using defaults when omitted.</summary>
    public PropagationAdapter(PropagationLimits? limits = null)
    {
        this.limits = limits ?? new();
        if (this.limits.MaxBytes < 1 || this.limits.MaxEntries < 1 || this.limits.MaxValueBytes < 1 || this.limits.MaxEnvelopeBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limits));
        }
    }

    /// <summary>Serializes a validated version-one work intent; invalid intent or an oversized envelope throws.</summary>
    public byte[] EncodeWork(OperationContext context, Guid workId, string? slotId, JsonElement payload)
    {
        if (workId == Guid.Empty || payload.ValueKind == JsonValueKind.Undefined
            || slotId is not null && (!OperationContextValidation.IsValidKey(slotId) || context.Reference.Draft is null))
        {
            throw new ArgumentException("Invalid work intent");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new WorkEnvelope(1, context.Reference, workId, slotId, payload), json.WorkEnvelope);
        if (bytes.Length > limits.MaxEnvelopeBytes)
        {
            throw new ArgumentException("Work envelope exceeds limit");
        }

        return bytes;
    }

    /// <summary>Creates an internal-destination carrier with a private reference and trace context; rejects external destinations and invalid headers.</summary>
    public InjectionResult<T> Inject<T>(OperationContext context, ActivityContext trace,
        T carrier, ICarrierCodec<T> codec, bool internalDestination)
    {
        if (!internalDestination)
        {
            return new InjectionResult<T>.Rejected("destination_not_internal");
        }

        try
        {
            var existing = codec.Read(carrier);
            if (Validate(existing) is { } invalid)
            {
                return new InjectionResult<T>.Rejected(Code(invalid));
            }

            var data = new List<HeaderValue>(existing.Entries.Where(e => !Owned(e.Name)));
            var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ContextWire(1, context.Reference), json.ContextWire));
            data.Add(new(ContextHeader, encoded));
            traces.Inject(new(trace, default), data, static (entries, key, value) => entries.Add(new(key, value)));
            var headers = new HeaderValues(data);
            if (Validate(headers) is { } error)
            {
                return new InjectionResult<T>.Rejected(Code(error));
            }

            return new InjectionResult<T>.Injected(codec.Create(headers));
        }
        catch { return new InjectionResult<T>.Rejected("carrier_invalid"); }
    }

    /// <summary>Parses bounded, untrusted carrier data without inheriting ambient baggage; component ingress requires a work envelope.</summary>
    public ParseResult Extract<T>(T carrier, ICarrierCodec<T> codec,
        ReadOnlyMemory<byte> envelope = default, IngressPolicy policy = IngressPolicy.Auxiliary)
    {
        if (policy == IngressPolicy.None)
        {
            return new ParseResult.Absent();
        }

        try
        {
            var headers = codec.Read(carrier);
            if (Validate(headers) is { } error)
            {
                return error;
            }
            // Never seed extraction from a preceding message's Activity.Current or Baggage.Current.
            var trace = traces.Extract(default, headers, static (h, k) => h.GetAll(k)).ActivityContext;
            var privateValues = headers.GetAll(ContextHeader).ToArray();
            var diagnostics = new List<string>();
            if (headers.GetAll("baggage").Any())
            {
                diagnostics.Add("baggage_ignored");
            }

            if (!envelope.IsEmpty)
            {
                if (envelope.Length > limits.MaxEnvelopeBytes)
                {
                    return new ParseResult.LimitExceeded("envelope_bytes");
                }

                using var doc = JsonDocument.Parse(envelope, new()
                {
                    MaxDepth = 32
                });
                if (DuplicateProperty(doc.RootElement))
                {
                    return new ParseResult.Malformed("duplicate_envelope_field");
                }

                var version = Version(doc.RootElement);
                if (version != 1)
                {
                    return version is { } v ? new ParseResult.UnsupportedVersion(v) : new ParseResult.Malformed("missing_version");
                }

                var work = doc.RootElement.Deserialize(json.WorkEnvelope);
                if (work is null || !OperationContextValidation.IsValid(work.Context) || work.WorkId == Guid.Empty
                    || work.Payload.ValueKind == JsonValueKind.Undefined
                    || work.SlotId is not null && (!OperationContextValidation.IsValidKey(work.SlotId) || work.Context.Draft is null)
                    || policy == IngressPolicy.Component && work.SlotId is null)
                {
                    return new ParseResult.Malformed("invalid_work_envelope");
                }
                // Optional headers never retarget or veto a valid required envelope.
                if (privateValues.Length > 0)
                {
                    var optional = privateValues.Length == 1 ? TryParseHeader(privateValues[0]) : new ParseResult.Malformed("duplicate_context_header");
                    if (optional is not ParseResult.Parsed p || p.Incoming.Reference != work.Context)
                    {
                        diagnostics.Add("optional_context_ignored");
                    }
                }
                work = work with
                {
                    Payload = work.Payload.Clone()
                };
                var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(work, json.WorkEnvelope)));
                return new ParseResult.Parsed(new(work.Context, work, fingerprint, trace, diagnostics.AsReadOnly()));
            }
            if (policy == IngressPolicy.Component)
            {
                return new ParseResult.Malformed("work_envelope_required");
            }

            if (privateValues.Length == 0)
            {
                return new ParseResult.Absent();
            }

            if (privateValues.Length > 1)
            {
                return new ParseResult.Malformed("duplicate_context_header");
            }

            var parsedHeader = TryParseHeader(privateValues[0]);
            return parsedHeader is ParseResult.Parsed parsed
                ? new ParseResult.Parsed(parsed.Incoming with
                {
                    Producer = trace,
                    Diagnostics = diagnostics.AsReadOnly()
                })
                : parsedHeader;
        }
        catch { return new ParseResult.Malformed("invalid_encoding_or_shape"); }
    }

    ParseResult TryParseHeader(string value)
    {
        try
        {
            return ParseHeader(value);
        }
        catch { return new ParseResult.Malformed("invalid_context_encoding"); }
    }
    ParseResult ParseHeader(string value)
    {
        using var doc = JsonDocument.Parse(Convert.FromBase64String(value), new()
        {
            MaxDepth = 32
        });
        if (DuplicateProperty(doc.RootElement))
        {
            return new ParseResult.Malformed("duplicate_context_field");
        }

        var version = Version(doc.RootElement);
        if (version != 1)
        {
            return version is { } v ? new ParseResult.UnsupportedVersion(v) : new ParseResult.Malformed("missing_version");
        }

        var wire = doc.RootElement.Deserialize(json.ContextWire);
        return wire is not null && OperationContextValidation.IsValid(wire.Context)
            ? new ParseResult.Parsed(new(wire.Context, null, null, default, Array.Empty<string>()))
            : new ParseResult.Malformed("invalid_context_reference");
    }
    ParseResult? Validate(HeaderValues headers)
    {
        if (headers.Entries.Count > limits.MaxEntries)
        {
            return new ParseResult.LimitExceeded("header_count");
        }

        long total = 0;
        foreach (var entry in headers.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || entry.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))
                || entry.Value is null || entry.Value.Any(c => c is '\r' or '\n' or '\0'))
            {
                return new ParseResult.Malformed("invalid_header");
            }

            var size = utf8.GetByteCount(entry.Value);
            if (size > limits.MaxValueBytes)
            {
                return new ParseResult.LimitExceeded("header_value_bytes");
            }

            total += utf8.GetByteCount(entry.Name) + size;
            if (total > limits.MaxBytes)
            {
                return new ParseResult.LimitExceeded("header_bytes");
            }
        }
        return null;
    }
    static bool Owned(string key) => string.Equals(key, ContextHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "traceparent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(key, "tracestate", StringComparison.OrdinalIgnoreCase);
    static string Code(ParseResult result) => result switch
    {
        ParseResult.LimitExceeded r => r.Limit,
        ParseResult.Malformed r => r.Code,
        _ => "carrier_invalid"
    };
    static int? Version(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("version", out var v) && v.TryGetInt32(out var version) ? version : null;
    static bool DuplicateProperty(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!seen.Add(p.Name) || DuplicateProperty(p.Value))
                {
                    return true;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (DuplicateProperty(item))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
