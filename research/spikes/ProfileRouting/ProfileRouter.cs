using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using LlmCmd.ContextPropagation;

namespace ProfileRouting;

public sealed record ProfileKey
{
    public string Id { get; }
    private ProfileKey(string id) => Id = id;
    public static bool TryCreate(string? id, out ProfileKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        key = new(id); return true;
    }
    // A generator can call this from a consumer assembly. Not a security boundary.
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static ProfileKey Declare(string id) => TryCreate(id, out var key) ? key! : throw new ArgumentException("Invalid declared profile ID", nameof(id));
    public override string ToString() => Id;
}
public interface IProfileCatalog<TSelf> where TSelf : IProfileCatalog<TSelf> { static abstract TSelf Instance { get; } }
[AttributeUsage(AttributeTargets.Property)]
public sealed class ModelProfileAttribute(string id) : Attribute { public string Id { get; } = id; }
public enum FailureKind { Transient, RateLimited, Timeout, Permanent, Cancelled }
public sealed record RetryPolicy(int MaxRetries = 0, TimeSpan? InitialDelay = null, bool UseJitter = true);
public sealed record ExecutionBudget(int MaxAttempts = 6, TimeSpan? Timeout = null);
public sealed record ExecutionProfile(ProfileKey Key, string Provider, string Model, float? Temperature = null,
    RetryPolicy? Retry = null, ProfileKey? Fallback = null, TimeSpan? AttemptTimeout = null);
public sealed record ProviderRegistration(Func<IChatClient> Factory, Func<Exception, FailureKind>? Classify = null);
public sealed record Attempt(int Number, string Profile, string Provider, string RequestModel, float? Temperature, string Kind, string Outcome,
    FailureKind? Failure, string? ResponseModel, string? ResponseId, long? InputTokens, long? OutputTokens, double DurationSeconds,
    CommandExecutionContext? Context = null, Guid AttemptId = default);
public sealed record RouteCapture(string RequestedProfile, string? ServedProfile, IReadOnlyList<Attempt> Attempts);
public closed record RouterBuildResult
{
    public sealed record Ready(ProfileRouter Router) : RouterBuildResult;
    public sealed record Invalid(IReadOnlyList<string> Errors) : RouterBuildResult;
}
public sealed class RouteException(FailureKind kind, string code, Exception? inner = null) : Exception(code, inner)
{ public FailureKind Kind { get; } = kind; }

public static class ProfileTelemetry
{
    public const string SourceName = "LlmCmd.ProfileRouting";
    public static readonly ActivitySource Activity = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
    internal static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("llmcmd.profile.fallbacks", "{transition}");
    internal static readonly Counter<long> Attempts = Meter.CreateCounter<long>("llmcmd.provider.attempts", "{attempt}");
    internal static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>("llmcmd.provider.attempt.duration", "s");
}

/// <summary>Owns newly-created clients; each For call returns an independent, non-owning request view.</summary>
public sealed class ProfileRouter : IDisposable
{
    readonly IReadOnlyDictionary<string, ExecutionProfile> profiles;
    readonly IReadOnlyDictionary<string, (IChatClient Client, Func<Exception, FailureKind> Classify)> providers;
    readonly ExecutionBudget budget;
    bool disposed;
    public IReadOnlyCollection<ExecutionProfile> Profiles => profiles.Values.ToArray();

    // A profile name is not a route identity: deployments can point the same
    // name at another model or fallback. Composition fingerprints the requested
    // route before spending; the actual serving model remains attempt evidence.
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public string GetRouteFingerprint(ProfileKey key)
    {
        var value = new StringBuilder();
        void Part(string? text) { text ??= ""; value.Append(text.Length).Append(':').Append(text); }
        string Number<T>(T number) where T : IFormattable => number.ToString(null,CultureInfo.InvariantCulture);
        Part(Number(budget.MaxAttempts)); Part(Number((budget.Timeout ?? TimeSpan.FromSeconds(30)).Ticks));
        var profile = profiles[key.Id];
        while (true)
        {
            Part(profile.Key.Id); Part(profile.Provider); Part(profile.Model);
            Part(profile.Temperature?.ToString("R",CultureInfo.InvariantCulture));
            var retry=profile.Retry ?? new();
            Part(Number(retry.MaxRetries)); Part(retry.InitialDelay?.Ticks.ToString(CultureInfo.InvariantCulture));
            Part(retry.UseJitter ? "true" : "false");
            Part(profile.AttemptTimeout?.Ticks.ToString(CultureInfo.InvariantCulture));
            if(profile.Fallback is null) break;
            profile=profiles[profile.Fallback.Id];
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
    private ProfileRouter(Dictionary<string, ExecutionProfile> profiles,
        Dictionary<string, (IChatClient, Func<Exception, FailureKind>)> providers, ExecutionBudget budget)
    { this.profiles = new ReadOnlyDictionary<string, ExecutionProfile>(profiles); this.providers = providers; this.budget = budget; }

    public static RouterBuildResult TryCreate(IEnumerable<ExecutionProfile> definitions,
        IReadOnlyDictionary<string, ProviderRegistration> registrations, ExecutionBudget? budget = null)
    {
        budget ??= new(); var errors = new List<string>(); var profiles = new Dictionary<string, ExecutionProfile>(StringComparer.Ordinal);
        if (budget.MaxAttempts is < 1 or > 100 || budget.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))) errors.Add("Invalid execution budget");
        foreach (var p in definitions)
        {
            if (p.Key is null || !profiles.TryAdd(p.Key.Id, p)) { errors.Add("Missing or duplicate profile ID"); continue; }
            if (string.IsNullOrWhiteSpace(p.Provider) || !registrations.ContainsKey(p.Provider)) errors.Add($"Provider missing for '{p.Key.Id}'");
            if (string.IsNullOrWhiteSpace(p.Model) || p.Temperature is float t && !float.IsFinite(t)) errors.Add($"Invalid settings for '{p.Key.Id}'");
            if (p.Retry is { } r && (r.MaxRetries is < 0 or > 20 || r.InitialDelay is { } d && (d < TimeSpan.Zero || d > TimeSpan.FromMinutes(1)))) errors.Add($"Invalid retry policy for '{p.Key.Id}'");
            if (p.AttemptTimeout is { } a && (a <= TimeSpan.Zero || a > TimeSpan.FromHours(1))) errors.Add($"Invalid attempt timeout for '{p.Key.Id}'");
        }
        foreach (var p in profiles.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); ExecutionProfile? current = p;
            while (current is not null)
            {
                if (!seen.Add(current.Key.Id)) { errors.Add($"Fallback cycle at '{p.Key.Id}'"); break; }
                if (current.Fallback is null) break;
                if (!profiles.TryGetValue(current.Fallback.Id, out current)) { errors.Add($"Missing fallback for '{p.Key.Id}'"); break; }
            }
        }
        if (errors.Count > 0) return new RouterBuildResult.Invalid(errors.AsReadOnly());
        var clients = new Dictionary<string, (IChatClient, Func<Exception, FailureKind>)>(StringComparer.Ordinal);
        try
        {
            foreach (var name in profiles.Values.Select(p => p.Provider).Distinct(StringComparer.Ordinal))
            {
                var registration = registrations[name]; var client = registration.Factory();
                if (client is null) throw new InvalidOperationException("Null provider");
                clients.Add(name, (client, registration.Classify ?? ClassifyDefault));
            }
        }
        catch
        {
            foreach (var client in clients.Values.Select(x => x.Item1).Distinct<IChatClient>(ReferenceEqualityComparer.Instance)) try { client.Dispose(); } catch { }
            return new RouterBuildResult.Invalid(Array.AsReadOnly(new[] { "A provider factory failed" }));
        }
        return new RouterBuildResult.Ready(new(profiles, clients, budget));
    }
    public RoutedProfileClient For(ProfileKey key,CommandExecutionContext? context=null,IExecutionEventSink? events=null,float? defaultTemperature=null,IExecutionJournal? journal=null) => new(this, key,context,events,defaultTemperature,journal);
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var client in providers.Values.Select(x => x.Client).Distinct<IChatClient>(ReferenceEqualityComparer.Instance)) try { client.Dispose(); } catch { }
    }
    static FailureKind ClassifyDefault(Exception error) => error switch
    {
        OperationCanceledException or TimeoutException => FailureKind.Timeout,
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => FailureKind.RateLimited,
        HttpRequestException { StatusCode: null } => FailureKind.Transient,
        HttpRequestException { StatusCode: { } s } when (int)s >= 500 || s == HttpStatusCode.RequestTimeout => FailureKind.Transient,
        _ => FailureKind.Permanent
    };
    static bool Eligible(FailureKind kind) => kind is FailureKind.Transient or FailureKind.RateLimited or FailureKind.Timeout;
    static async Task<ChatResponse> AwaitProvider(Task<ChatResponse> task, CancellationToken ct)
    {
        try { return await task.WaitAsync(ct).ConfigureAwait(false); }
        finally { if (!task.IsCompleted) _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    }

    internal async Task<ChatResponse> ExecuteAsync(RoutedProfileClient view, IEnumerable<ChatMessage> input, ChatOptions? patch, CancellationToken callerToken)
    {
        var records = new List<Attempt>(); string? served = null; var requested = view.Key.Id;
        var parent = System.Diagnostics.Activity.Current;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromSeconds(30));
        var ct = deadline.Token; var kind = "initial";
        try
        {
            if (disposed) throw new RouteException(FailureKind.Permanent, "router_disposed");
            if (!profiles.TryGetValue(requested, out var profile)) throw new RouteException(FailureKind.Permanent, "profile_not_configured");
            // The experiment supports text completion. Do not silently replay tool actions.
            if (patch?.Tools is { Count: > 0 }) throw new RouteException(FailureKind.Permanent, "tools_require_inner_model_call_routing");
            var messages = input.Select(m => m.Clone()).ToArray();
            parent?.SetTag("llmcmd.profile.requested", requested);
            while (true)
            {
                var retry = profile.Retry ?? new(); FailureKind failure = FailureKind.Permanent; Exception? last = null;
                for (int i = 0; i <= retry.MaxRetries; i++)
                {
                    if (callerToken.IsCancellationRequested) throw new OperationCanceledException(callerToken);
                    if (ct.IsCancellationRequested) throw new RouteException(FailureKind.Timeout, "execution_deadline");
                    if (records.Count >= budget.MaxAttempts) throw new RouteException(FailureKind.Permanent, "attempt_budget_exhausted");
                    using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptToken.CancelAfter(profile.AttemptTimeout ?? TimeSpan.FromSeconds(10));
                    var options = patch?.Clone() ?? new(); options.ModelId = profile.Model; options.Temperature = patch?.Temperature ?? profile.Temperature ?? view.DefaultTemperature;
                    var number = records.Count + 1; var attemptId=Guid.NewGuid(); var started = Stopwatch.StartNew(); ChatResponse? response = null, observed = null;
                    if(view.Context is { } execution)
                    {
                        // Reserve before every send, including retries and fallback.
                        // A telemetry failure may be ignored; a journal failure may not.
                        var reserved=new ExecutionEvent("attempt.started",execution,attemptId,number,profile.Provider);
                        await ExecutionJournal.AppendAsync(view.Journal,reserved,callerToken);
                        await ExecutionEvents.EmitSafelyAsync(view.Events,reserved);
                    }
                    using (var span = ProfileTelemetry.Activity.StartActivity("llmcmd.provider.attempt", ActivityKind.Internal))
                    {
                        view.Context?.Tag(span); span?.SetTag("llmcmd.attempt.id",attemptId.ToString());
                        span?.SetTag("llmcmd.profile", profile.Key.Id); span?.SetTag("gen_ai.provider.name", profile.Provider);
                        span?.SetTag("gen_ai.request.model", options.ModelId); span?.SetTag("llmcmd.attempt.number", number); span?.SetTag("llmcmd.attempt.kind", kind);
                        try
                        {
                            observed = await AwaitProvider(providers[profile.Provider].Client.GetResponseAsync(messages.Select(m => m.Clone()), options, attemptToken.Token), attemptToken.Token).ConfigureAwait(false);
                            callerToken.ThrowIfCancellationRequested();
                            if (attemptToken.IsCancellationRequested) throw new TimeoutException("Attempt exceeded its deadline");
                            response = observed;
                            failure = default;
                        }
                        catch (Exception error)
                        {
                            last = error;
                            if (callerToken.IsCancellationRequested) failure = FailureKind.Cancelled;
                            else if (attemptToken.IsCancellationRequested) failure = FailureKind.Timeout;
                            else { try { failure = providers[profile.Provider].Classify(error); if (failure == FailureKind.Cancelled) failure = FailureKind.Timeout; } catch { failure = FailureKind.Permanent; } }
                            span?.SetStatus(ActivityStatusCode.Error); span?.SetTag("error.type", failure.ToString());
                        }
                        started.Stop(); var outcome = response is not null ? "success" : failure == FailureKind.Cancelled ? "cancelled" : "failed";
                        var attemptTags = new TagList { { "llmcmd.profile", profile.Key.Id }, { "gen_ai.provider.name", profile.Provider }, { "llmcmd.attempt.kind", kind }, { "outcome", outcome } };
                        ProfileTelemetry.Attempts.Add(1, attemptTags); ProfileTelemetry.AttemptDuration.Record(started.Elapsed.TotalSeconds, attemptTags);
                        span?.SetTag("gen_ai.response.model", observed?.ModelId); span?.SetTag("gen_ai.response.id", observed?.ResponseId);
                        records.Add(new(number, profile.Key.Id, profile.Provider, profile.Model, options.Temperature, kind, outcome, response is null ? failure : null,
                            observed?.ModelId, observed?.ResponseId, observed?.Usage?.InputTokenCount, observed?.Usage?.OutputTokenCount, started.Elapsed.TotalSeconds,view.Context,attemptId));
                        if(view.Context is { } completed)
                        {
                            var finished=new ExecutionEvent("attempt.finished",completed,attemptId,number,profile.Provider,outcome,observed?.Usage?.InputTokenCount,observed?.Usage?.OutputTokenCount);
                            try { await ExecutionJournal.AppendAsync(view.Journal,finished,CancellationToken.None); } catch(ExecutionJournalUnavailableException) { }
                            await ExecutionEvents.EmitSafelyAsync(view.Events,finished);
                        }
                    }
                    if (response is not null) { served = profile.Key.Id; parent?.SetTag("llmcmd.profile.served", served); return response; }
                    if (callerToken.IsCancellationRequested) throw new OperationCanceledException(callerToken);
                    if (ct.IsCancellationRequested) throw new RouteException(FailureKind.Timeout, "execution_deadline", last);
                    if (!Eligible(failure)) throw new RouteException(failure, "provider_failure", last);
                    if (records.Count >= budget.MaxAttempts) throw new RouteException(FailureKind.Permanent, "attempt_budget_exhausted", last);
                    if (i < retry.MaxRetries)
                    {
                        var delay = Math.Min(30_000, (retry.InitialDelay ?? TimeSpan.FromMilliseconds(100)).TotalMilliseconds * Math.Pow(2, i));
                        if (retry.UseJitter) delay *= .5 + Random.Shared.NextDouble() * .5;
                        try { await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { throw new RouteException(FailureKind.Timeout, "execution_deadline"); }
                        kind = "retry";
                    }
                }
                if (profile.Fallback is null) throw new RouteException(failure, "fallback_exhausted", last);
                if (records.Count >= budget.MaxAttempts) throw new RouteException(FailureKind.Permanent, "attempt_budget_exhausted");
                if (callerToken.IsCancellationRequested) throw new OperationCanceledException(callerToken);
                if (ct.IsCancellationRequested) throw new RouteException(FailureKind.Timeout, "execution_deadline");
                var next = profiles[profile.Fallback.Id];
                var tags = new TagList { { "llmcmd.profile.requested", requested }, { "llmcmd.profile.from", profile.Key.Id },
                    { "llmcmd.profile.to", next.Key.Id }, { "llmcmd.fallback.reason", failure.ToString() } };
                parent?.AddEvent(new ActivityEvent("llmcmd.profile.fallback", tags: new ActivityTagsCollection(tags)));
                ProfileTelemetry.Fallbacks.Add(1, tags);
                profile = next; kind = "fallback";
            }
        }
        finally { view.Capture = new(requested, served, records.AsReadOnly()); }
    }
}

public sealed class RoutedProfileClient : IChatClient
{
    readonly ProfileRouter router; internal ProfileKey Key { get; }
    internal CommandExecutionContext? Context { get; }
    internal IExecutionEventSink? Events { get; }
    internal IExecutionJournal? Journal { get; }
    internal float? DefaultTemperature { get; }
    public RouteCapture? Capture { get; internal set; }
    internal RoutedProfileClient(ProfileRouter router, ProfileKey key,CommandExecutionContext? context,IExecutionEventSink? events,float? defaultTemperature,IExecutionJournal? journal)
    { this.router = router; Key = key; Context=context; Events=events; DefaultTemperature=defaultTemperature; Journal=journal; }
    // Create one view per execution; provider clients and policies are shared by the owning router.
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => router.ExecuteAsync(this, messages, options, cancellationToken);
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("Streaming commitment needs a separate routing implementation");
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { } // View never disposes router-owned provider clients.
}
