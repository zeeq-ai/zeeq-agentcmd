#pragma warning disable MEAI001
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExecutionCapture;
public sealed record Settings(string? Provider = null, string? Model = null, float? Temperature = null, int? MaxTokens = null)
{
    public Settings Overlay(Settings? high) => high is null ? this : new(high.Provider ?? Provider, high.Model ?? Model, high.Temperature ?? Temperature, high.MaxTokens ?? MaxTokens);
    public ChatOptions Options() => new ChatOptions { ModelId = Model, Temperature = Temperature, MaxOutputTokens = MaxTokens }.Clone();
}
public sealed record DemoCommand(string Name, int Count, string? ExplicitId = null, bool AllowNameOverride = false, bool AllowCountOverride = false)
{
    public const string Template = "Hello {name}, {name}: {count}. {{literal}}";
    public string Id => ExplicitId ?? typeof(DemoCommand).FullName!;
    public static Settings Defaults => new(Model: "command-model", Temperature: .3f);
}
public sealed record Snapshot(string? Template = null, IReadOnlyDictionary<string, JsonElement>? Parameters = null, Settings? Settings = null, string? Revision = null);
public interface ISnapshotProvider { ValueTask<Snapshot?> GetAsync(string id, CancellationToken ct); }
public sealed record Caller(string Member, string File, int Line);
public sealed record UsageSnapshot(long? InputTokenCount, long? OutputTokenCount, long? TotalTokenCount, long? CachedInputTokenCount, long? InputAudioTokenCount, long? InputTextTokenCount, long? OutputAudioTokenCount, long? OutputTextTokenCount, long? ReasoningTokenCount, IReadOnlyDictionary<string, long>? AdditionalCounts)
{
    public static UsageSnapshot? From(UsageDetails? u) => u is null ? null : new(u.InputTokenCount, u.OutputTokenCount, u.TotalTokenCount, u.CachedInputTokenCount, u.InputAudioTokenCount, u.InputTextTokenCount, u.OutputAudioTokenCount, u.OutputTextTokenCount, u.ReasoningTokenCount, u.AdditionalCounts is null ? null : new ReadOnlyDictionary<string, long>(u.AdditionalCounts.ToDictionary(k => k.Key, v => v.Value)));
}
public sealed record Attempt(string Request, string? Model, float? Temperature, int? MaxTokens, string Outcome, string? ResponseModel, UsageSnapshot? Usage);
public sealed record Cost(decimal? Amount, string? RateVersion, string? Reason);
public interface ICostEnricher { Cost Calculate(IReadOnlyList<Attempt> attempts); }
public sealed class FakeCost : ICostEnricher
{
    // Deliberately flat fake rates across models. This is not real pricing.
    public Cost Calculate(IReadOnlyList<Attempt> attempts) {
        if (attempts.Count == 0) return new(null, "fake-v1", "no observed attempts");
        decimal total = 0;
        foreach (var attempt in attempts) {
            var u = attempt.Usage;
            if (attempt.Outcome != "success" || u is null || u.InputTokenCount is null || u.OutputTokenCount is null || u.CachedInputTokenCount is not null || u.InputAudioTokenCount is not null || u.OutputAudioTokenCount is not null || u.InputTextTokenCount is not null || u.OutputTextTokenCount is not null || u.ReasoningTokenCount is not null || u.AdditionalCounts?.Count > 0)
                return new(null, "fake-v1", "unknown or unsupported attempt usage");
            total += u.InputTokenCount.Value * .001m + u.OutputTokenCount.Value * .002m;
        }
        return new(total, "fake-v1", null);
    }
}
public sealed record Capture(int SchemaVersion, Guid ExecutionId, string? TraceId, string? SpanId, DateTimeOffset StartedAt, double DurationMs, string Id, string DefinitionHash, string EffectiveHash, string? Revision, string OriginalTemplate, string Template, IReadOnlyDictionary<string, object?> OriginalParameters, IReadOnlyDictionary<string, object?> Parameters, IReadOnlyDictionary<string, string> ParameterOrigins, string FormattedPrompt, Settings Settings, Caller Caller, IReadOnlyList<Attempt> Attempts, string Outcome, string? Error, string? Response, long? ResponseInputTokens, long? ResponseOutputTokens, Cost Cost, string? EnrichmentWarning);
public interface ICollector { ValueTask CollectAsync(Capture record, CancellationToken ct); }
public closed record ExecutionResult
{
    public sealed record Success(ChatResponse Response, Capture Capture, string? CollectorError) : ExecutionResult;
    public sealed record Invalid(string Message, Capture Capture, string? CollectorError) : ExecutionResult;
    public sealed record Failed(string Message, Capture? Capture, string? CollectorError) : ExecutionResult;
    public sealed record Cancelled(Capture? Capture, string? CollectorError) : ExecutionResult;
    public T Match<T>(Func<Success,T> success, Func<Invalid,T> invalid, Func<Failed,T> failed, Func<Cancelled,T> cancelled) => this switch {
        Success value => success(value), Invalid value => invalid(value), Failed value => failed(value), Cancelled value => cancelled(value)
    };
}
public sealed class Executor : IDisposable
{
    public static readonly ActivitySource Activity = new("ExecutionCapture.Spike");
    public static readonly Meter Meter = new("ExecutionCapture.Spike");
    static readonly Counter<long> Executions = Meter.CreateCounter<long>("llmcmd.executions");
    static readonly AsyncLocal<List<Attempt>?> CurrentAttempts = new();
    static readonly AsyncLocal<CancellationToken> CallerToken = new();
    readonly Dictionary<string, IChatClient> clients = new();
    readonly ISnapshotProvider provider; readonly ICollector collector; readonly ICostEnricher costs; readonly Settings platform; readonly TimeSpan collectorTimeout;
    public Executor(IDictionary<string, Func<IChatClient>> factories, Action<ChatClientBuilder>? configure, ISnapshotProvider provider, ICollector collector, ICostEnricher costs, Settings platform, TimeSpan? collectorTimeout = null)
    {
        this.provider = provider; this.collector = collector; this.costs = costs; this.platform = platform; this.collectorTimeout = collectorTimeout ?? TimeSpan.FromSeconds(2);
        foreach (var (key, factory) in factories) {
            var builder = new ChatClientBuilder(factory());
            configure?.Invoke(builder);
            // Last registered middleware is closest to the provider in this builder.
            builder.Use(async (messages, options, next, ct) => {
                var text = string.Join("\n", messages.Select(m => m.Text)); var list = CurrentAttempts.Value; var model = options?.ModelId; var temperature = options?.Temperature; var maxTokens = options?.MaxOutputTokens;
                try { var response = await next.GetResponseAsync(messages, options, ct); list?.Add(new(text, model, temperature, maxTokens, "success", response.ModelId, UsageSnapshot.From(response.Usage))); return response; }
                catch (OperationCanceledException) { list?.Add(new(text, model, temperature, maxTokens, CallerToken.Value.IsCancellationRequested ? "cancelled" : "failed", null, null)); throw; }
                catch { list?.Add(new(text, model, temperature, maxTokens, "failed", null, null)); throw; }
            }, null);
            clients.Add(key, builder.Build());
        }
    }
    public async Task<ExecutionResult> ExecuteAsync(DemoCommand cmd, Settings? explicitSettings = null, CancellationToken cancellationToken = default,
        [CallerMemberName] string member = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var started = DateTimeOffset.UtcNow; var stopwatch = Stopwatch.StartNew(); var executionId = Guid.NewGuid();
        using var activity = Activity.StartActivity("llmcmd.execute");
        var trace = activity ?? System.Diagnostics.Activity.Current;
        var attempts = new List<Attempt>(); var prior = CurrentAttempts.Value; var priorToken = CallerToken.Value; CurrentAttempts.Value = attempts; CallerToken.Value = cancellationToken;
        string? template = null, formatted = null, revision = null; Settings effective = platform; var original = new Dictionary<string, object?> { ["name"] = cmd.Name, ["count"] = cmd.Count }; var values = new Dictionary<string, object?>(original); var origins = new Dictionary<string, string> { ["name"] = "caller", ["count"] = "caller" }; ChatResponse? response = null;
        var caller = new Caller(member, file, line);
        try {
            var (outcome, error) = await Run();
            return await Finish(outcome, error);
        } finally { CurrentAttempts.Value = prior; CallerToken.Value = priorToken; }
        async Task<(string Outcome, string? Error)> Run() {
            try {
                cancellationToken.ThrowIfCancellationRequested();
                if (cmd.Name is null) return ("invalid", "Name is required");
                var snap = await provider.GetAsync(cmd.Id, cancellationToken);
                template = snap?.Template ?? DemoCommand.Template; revision = snap?.Revision;
                var candidate = new Dictionary<string, object?>(values); var candidateOrigins = new Dictionary<string, string>(origins);
                if (snap?.Parameters is not null) foreach (var (name, value) in snap.Parameters) {
                    if (name == "name" && !cmd.AllowNameOverride || name == "count" && !cmd.AllowCountOverride) return ("invalid", $"Parameter override not allowed: {name}");
                    if (name == "name" && value.ValueKind == JsonValueKind.String) candidate[name] = value.GetString()!;
                    else if (name == "count" && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)) candidate[name] = n;
                    else return ("invalid", $"Unknown parameter or wrong type: {name}");
                    candidateOrigins[name] = "runtime";
                }
                values = candidate; origins = candidateOrigins;
                if (!TryFormat(template, values, out formatted, out var error)) return ("invalid", error);
                effective = platform.Overlay(DemoCommand.Defaults).Overlay(snap?.Settings).Overlay(explicitSettings);
                if (effective.MaxTokens is < 0 || (effective.Temperature is float t && (float.IsNaN(t) || float.IsInfinity(t)))) return ("invalid", "Invalid settings");
                if (effective.Provider is null || !clients.TryGetValue(effective.Provider, out var client)) return ("invalid", "Unknown provider");
                response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, formatted) }, effective.Options(), cancellationToken);
                return ("success", null);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return ("cancelled", null); }
            catch (OperationCanceledException) { return ("failed", "provider/source timeout or internal cancellation"); }
            catch (Exception e) { return ("failed", e.Message); }
        }
        async Task<ExecutionResult> Finish(string outcome, string? error) {
            activity?.SetTag("llmcmd.outcome", outcome); Executions.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
            stopwatch.Stop();
            var attemptSnapshot = Array.AsReadOnly(attempts.ToArray());
            Cost cost; string? enrichmentWarning = null;
            try { cost = costs.Calculate(attemptSnapshot); } catch (Exception e) { enrichmentWarning = e.Message; cost = new Cost(null, null, "cost enrichment failed"); }
            var cap = new Capture(1, executionId, trace?.TraceId.ToString(), trace?.SpanId.ToString(), started, stopwatch.Elapsed.TotalMilliseconds, cmd.Id, Hash(DemoCommand.Template), Hash(template ?? DemoCommand.Template), revision, DemoCommand.Template, template ?? DemoCommand.Template, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(original)), new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(values)), new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(origins)), formatted ?? "", effective, caller, attemptSnapshot, outcome, error, response?.Text, response?.Usage?.InputTokenCount, response?.Usage?.OutputTokenCount, cost, enrichmentWarning);
            string? collectorError = null;
            using var cts = new CancellationTokenSource();
            try {
                var task = collector.CollectAsync(cap, cts.Token).AsTask();
                try { await task.WaitAsync(collectorTimeout); }
                catch (TimeoutException) { cts.Cancel(); _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); collectorError = "collector timed out"; }
            } catch (Exception e) { collectorError = e.Message; }
            return outcome switch { "success" => new ExecutionResult.Success(response!, cap, collectorError), "cancelled" => new ExecutionResult.Cancelled(cap, collectorError), "invalid" => new ExecutionResult.Invalid(error ?? "invalid", cap, collectorError), _ => new ExecutionResult.Failed(error ?? "failure", cap, collectorError) };
        }
    }

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    public static bool TryFormat(string template, IReadOnlyDictionary<string, object?> values, out string result, out string? error) {
        var sb = new StringBuilder(); error = null;
        for (int i = 0; i < template.Length; i++) {
            char c = template[i];
            if (c == '{') {
                if (i + 1 < template.Length && template[i+1] == '{') { sb.Append('{'); i++; continue; }
                int end = template.IndexOf('}', i+1); if (end < 0) { result = ""; error = "Unclosed placeholder"; return false; }
                string name = template.Substring(i+1, end-i-1); if (!values.TryGetValue(name, out var value)) { result = ""; error = "Unknown placeholder: " + name; return false; }
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); i = end;
            } else if (c == '}') { if (i+1 < template.Length && template[i+1] == '}') { sb.Append('}'); i++; } else { result = ""; error = "Unescaped brace"; return false; } }
            else sb.Append(c);
        }
        result = sb.ToString(); return true;
    }
    public void Dispose() { foreach (var client in clients.Values) client.Dispose(); }
}
