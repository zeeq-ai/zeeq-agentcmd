using System.Text.Json;

namespace ProviderAdapters.Platforms;

/// <summary>
/// Anthropic Messages wire shape. Native <c>input_tokens</c> EXCLUDES cache reads and writes; the inclusive MEAI input equals
/// input_tokens + cache_read_input_tokens + cache_creation_input_tokens. The 5m/1h fields partition cache creation and are never added on top of it.
/// Thinking tokens are within output. Web search/fetch counters are requests, not tokens.
/// </summary>
public sealed class AnthropicMessagesAdapter : IProviderResponseAdapter
{
    public string Id => "anthropic.messages";
    public string Version => "0.1.0";
    public string RulesRevision => "provider-adapters.md@2026-11-01#anthropic-messages";
    public string? ProviderKey => "anthropic";
    public ApiKind Api => ApiKind.Messages;
    public StreamUsageMode StreamUsage => StreamUsageMode.LatestSnapshot;
    public IReadOnlyDictionary<string, AdditionalCountRule> AdditionalCounts { get; } = new Dictionary<string, AdditionalCountRule>
    {
        ["CacheCreationInputTokens"] = new(Dim.CacheWrite, Unit.Tokens, Relation.SubsetOfParent, Dim.Input),
        ["WebSearchRequests"] = new(Dim.WebSearchRequests, Unit.Requests, Relation.Independent, null),
        ["WebFetchRequests"] = new(Dim.WebFetchRequests, Unit.Requests, Relation.Independent, null),
    };

    public void Enrich(AdapterContext ctx)
    {
        if (ctx.Wire.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String) ctx.NativeStopReason = sr.GetString();
        if (ctx.ReadObject(ctx.Wire, "usage", "$") is not { } usage)
        {
            if (!ctx.Wire.TryGetProperty("usage", out var u) || u.ValueKind == JsonValueKind.Null)
                ctx.Report(Severity.Warning, "wire-usage-absent", "$.usage", "The response body carried no usage object; counts stay as reported by the normalized layer");
            return;
        }
        const string U = "$.usage";
        void Obs(string dim, long? v, Unit unit, Relation rel, string? parent, string path, string? note = null)
        { if (v is { } n) ctx.Usage.Observe(dim, n, unit, rel, parent, Origin.Native, "wire:" + path, note); }

        var uncached = ctx.ReadCount(usage, "input_tokens", U);
        var read = ctx.ReadCount(usage, "cache_read_input_tokens", U);
        var write = ctx.ReadCount(usage, "cache_creation_input_tokens", U);
        Obs(Dim.Uncached, uncached, Unit.Tokens, Relation.PartOfParent, Dim.Input, "usage.input_tokens", "native input_tokens excludes cache reads and writes");
        Obs(Dim.Cached, read, Unit.Tokens, Relation.SubsetOfParent, Dim.Input, "usage.cache_read_input_tokens");
        Obs(Dim.CacheWrite, write, Unit.Tokens, Relation.SubsetOfParent, Dim.Input, "usage.cache_creation_input_tokens");
        Obs(Dim.Output, ctx.ReadCount(usage, "output_tokens", U), Unit.Tokens, Relation.Total, null, "usage.output_tokens");
        if (uncached is { } a && read is { } b && write is { } c)
            ctx.Usage.Observe(Dim.Input, a + b + c, Unit.Tokens, Relation.Total, null, Origin.Computed, "computed:input_tokens+cache_read_input_tokens+cache_creation_input_tokens");
        else if (ctx.Usage.Peek(Dim.Input) is null)
            ctx.Report(Severity.Warning, "input-total-not-derivable", "$.usage", "Inclusive input needs input_tokens, cache_read_input_tokens and cache_creation_input_tokens; a missing term stays unknown");

        if (ctx.ReadObject(usage, "cache_creation", U) is { } ttl)
        {
            const string T = U + ".cache_creation";
            var m5 = ctx.ReadCount(ttl, "ephemeral_5m_input_tokens", T);
            var h1 = ctx.ReadCount(ttl, "ephemeral_1h_input_tokens", T);
            Obs(Dim.CacheWrite5m, m5, Unit.Tokens, Relation.PartOfParent, Dim.CacheWrite, "usage.cache_creation.ephemeral_5m_input_tokens");
            Obs(Dim.CacheWrite1h, h1, Unit.Tokens, Relation.PartOfParent, Dim.CacheWrite, "usage.cache_creation.ephemeral_1h_input_tokens");
            var unknown = ttl.EnumerateObject().Any(p => p.Name is not ("ephemeral_5m_input_tokens" or "ephemeral_1h_input_tokens"));
            ctx.ReportUnknownFields(ttl, T, "ephemeral_5m_input_tokens", "ephemeral_1h_input_tokens");
            if (unknown) ctx.Report(Severity.Warning, "ttl-split-incomplete", T, "An unrecognized TTL bucket exists, so the known buckets are not proven to cover the aggregate; no sum check or residual was made");
            else if (m5 is null || h1 is null) ctx.Report(Severity.Warning, "ttl-split-incomplete", T, "Only part of the 5m/1h split was present; no residual was invented");
            else
            {
                if (ctx.Usage.Peek(Dim.CacheWrite) is null) ctx.Usage.Observe(Dim.CacheWrite, m5.Value + h1.Value, Unit.Tokens, Relation.SubsetOfParent, Dim.Input, Origin.Computed, "computed:ephemeral_5m+ephemeral_1h");
                ctx.Usage.CheckSum(Dim.CacheWrite, [Dim.CacheWrite5m, Dim.CacheWrite1h]);
            }
        }
        if (ctx.ReadObject(usage, "output_tokens_details", U) is { } od)
        {
            Obs(Dim.Reasoning, ctx.ReadCount(od, "thinking_tokens", U + ".output_tokens_details"), Unit.Tokens, Relation.SubsetOfParent, Dim.Output, "usage.output_tokens_details.thinking_tokens",
                "provider re-tokenization of underlying thinking; not the length of returned thinking text; within output");
            ctx.ReportUnknownFields(od, U + ".output_tokens_details", "thinking_tokens");
        }
        if (ctx.ReadObject(usage, "server_tool_use", U) is { } st)
        {
            Obs(Dim.WebSearchRequests, ctx.ReadCount(st, "web_search_requests", U + ".server_tool_use"), Unit.Requests, Relation.Independent, null, "usage.server_tool_use.web_search_requests");
            Obs(Dim.WebFetchRequests, ctx.ReadCount(st, "web_fetch_requests", U + ".server_tool_use"), Unit.Requests, Relation.Independent, null, "usage.server_tool_use.web_fetch_requests");
            ctx.ReportUnknownFields(st, U + ".server_tool_use", "web_search_requests", "web_fetch_requests");
        }
        ctx.ReportUnknownFields(usage, U, "input_tokens", "output_tokens", "cache_read_input_tokens", "cache_creation_input_tokens", "cache_creation", "output_tokens_details", "server_tool_use", "service_tier");

        if (ctx.Usage.Peek(Dim.Total) is null && ctx.Usage.Peek(Dim.Input) is { } i && ctx.Usage.Peek(Dim.Output) is { } o)
            ctx.Usage.Observe(Dim.Total, i + o, Unit.Tokens, Relation.Total, null, Origin.Computed, "computed:input+output");
        ctx.Usage.CheckSum(Dim.Total, [Dim.Input, Dim.Output]);
    }
}
