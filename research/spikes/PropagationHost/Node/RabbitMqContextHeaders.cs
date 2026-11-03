using LlmCmd.ContextPropagation;
using RabbitMQ.Client;

/// <summary>Reference broker binding. Numeric broker headers are opaque, not malformed context text.</summary>
static class RabbitMqContextHeaders
{
    public static HeaderValues Snapshot(IReadOnlyBasicProperties properties)
    {
        var headers=properties.Headers;
        if(headers is null) return HeaderValues.Empty;
        if(headers.Count>64) throw new FormatException("Too many message headers");
        IReadOnlyList<KeyValuePair<string,object>> selected=headers
            .Where(h=>h.Key.StartsWith("llmcmd-",StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("traceparent",StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("tracestate",StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("baggage",StringComparison.OrdinalIgnoreCase))
            .Select(h=>new KeyValuePair<string,object>(h.Key,h.Value!)).ToArray();
        return new BinaryCarrierCodec().Read(selected);
    }
}
