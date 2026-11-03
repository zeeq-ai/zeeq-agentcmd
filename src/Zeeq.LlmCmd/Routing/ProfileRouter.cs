using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Zeeq.LlmCmd;

/// <summary>Validated, case-sensitive identifier of a configured routing profile.</summary>
public sealed record ProfileKey
{
    /// <summary>Validated profile identifier.</summary>
    public string Id
    {
        get;
    }
    private ProfileKey(string id) => Id = id;
    /// <summary>Validates a profile identifier without throwing.</summary>
    /// <param name="id">One to 128 ASCII letters, digits, periods, underscores or hyphens.</param>
    /// <param name="key">Validated key on success; otherwise null.</param>
    /// <returns>True when the identifier satisfies the profile-key rules.</returns>
    public static bool TryCreate(string? id, out ProfileKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return false;
        }

        key = new(id);
        return true;
    }
    // A generator can call this from a consumer assembly. Not a security boundary.
    /// <summary>Creates a declared profile key or throws for an invalid identifier; intended for generated code.</summary>
    /// <param name="id">Profile identifier to validate.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static ProfileKey Declare(string id) => TryCreate(id, out var key) ? key! : throw new ArgumentException("Invalid declared profile ID", nameof(id));
    /// <inheritdoc/>
    public override string ToString() => Id;
}
/// <summary>Static entry point for a generated catalog of declared profiles.</summary>
/// <typeparam name="TSelf">Concrete catalog type.</typeparam>
public interface IProfileCatalog<TSelf> where TSelf : IProfileCatalog<TSelf>
{
    /// <summary>Shared catalog instance.</summary>
    static abstract TSelf Instance
    {
        get;
    }
}
/// <summary>Declares a profile identifier on a catalog property for code generation.</summary>
/// <param name="id">Declared identifier, validated when the generated key is created.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ModelProfileAttribute(string id) : Attribute
{
    /// <summary>Profile identifier supplied to the declaration.</summary>
    public string Id { get; } = id;
}
/// <summary>Classification used to decide whether routing retries or falls back.</summary>
public enum FailureKind
{
    /// <summary>Temporary provider or transport failure eligible for retry.</summary>
    Transient,
    /// <summary>Provider rate limit eligible for retry.</summary>
    RateLimited,
    /// <summary>Provider or attempt timeout eligible for retry.</summary>
    Timeout,
    /// <summary>Non-retryable failure.</summary>
    Permanent,
    /// <summary>Caller-requested cancellation, which stops routing.</summary>
    Cancelled
}
/// <summary>Retry settings applied per profile before its fallback.</summary>
/// <param name="MaxRetries">Maximum retries after the initial attempt.</param>
/// <param name="InitialDelay">Initial exponential backoff delay; null uses the router default.</param>
/// <param name="UseJitter">Whether to randomize the backoff delay.</param>
public sealed record RetryPolicy(int MaxRetries = 0, TimeSpan? InitialDelay = null, bool UseJitter = true);
/// <summary>Shared limits across all retries and fallbacks in one execution.</summary>
/// <param name="MaxAttempts">Maximum total provider attempts.</param>
/// <param name="Timeout">Overall deadline; null uses the router default.</param>
public sealed record ExecutionBudget(int MaxAttempts = 6, TimeSpan? Timeout = null);
/// <summary>Provider, model and resilience policy associated with one profile key.</summary>
/// <param name="Key">Validated profile identity.</param>
/// <param name="Provider">Registered provider name.</param>
/// <param name="Model">Requested model or deployment identifier.</param>
/// <param name="Temperature">Optional profile default; a request override takes precedence.</param>
/// <param name="Retry">Retry policy; null selects the default.</param>
/// <param name="Fallback">Next profile after eligible retries are exhausted, if any.</param>
/// <param name="AttemptTimeout">Per-attempt deadline; null uses the router default.</param>
/// <param name="Defaults">Immutable inference defaults, applied below explicit request options.</param>
public sealed record ExecutionProfile(ProfileKey Key, string Provider, string Model, float? Temperature = null,
    RetryPolicy? Retry = null, ProfileKey? Fallback = null, TimeSpan? AttemptTimeout = null, ProfileDefaults? Defaults = null);
/// <summary>Provider client factory and optional exception classifier.</summary>
/// <param name="Factory">Creates or resolves the configured client pipeline.</param>
/// <param name="Classify">Maps provider exceptions to routing failure kinds; null uses the default classifier.</param>
/// <param name="OwnsClient">Whether the router disposes the client; false for DI-owned keyed clients.</param>
/// <param name="FixedModel">Explicit model or deployment binding; null declares per-request model selection.</param>
public sealed record ProviderRegistration(Func<IChatClient> Factory, Func<Exception, FailureKind>? Classify = null,
    bool OwnsClient = true, string? FixedModel = null);
/// <summary>Observed outcome of one provider call, including retries and fallback calls.</summary>
/// <param name="Number">One-based attempt number within the route.</param>
/// <param name="Profile">Profile selected for this attempt.</param>
/// <param name="Provider">Registered provider used.</param>
/// <param name="RequestModel">Model requested from the provider.</param>
/// <param name="Temperature">Effective request temperature, if any.</param>
/// <param name="Kind">Initial, retry or fallback attempt label.</param>
/// <param name="Outcome">Success, failure or cancellation outcome label.</param>
/// <param name="Failure">Classified failure, or null on success.</param>
/// <param name="ResponseModel">Model reported by the provider, if available.</param>
/// <param name="ResponseId">Provider response identifier, if available.</param>
/// <param name="InputTokens">Reported input usage, or null when unavailable.</param>
/// <param name="OutputTokens">Reported output usage, or null when unavailable.</param>
/// <param name="DurationSeconds">Elapsed attempt duration in seconds.</param>
/// <param name="Context">Optional execution context associated with the attempt.</param>
/// <param name="AttemptId">Identifier used to correlate attempt events.</param>
public sealed record Attempt(int Number, string Profile, string Provider, string RequestModel, float? Temperature, string Kind, string Outcome,
    FailureKind? Failure, string? ResponseModel, string? ResponseId, long? InputTokens, long? OutputTokens, double DurationSeconds,
    CommandExecutionContext? Context = null, Guid AttemptId = default);
/// <summary>Evidence of the requested profile, serving profile and attempts for one route.</summary>
/// <param name="RequestedProfile">Initially requested profile identifier.</param>
/// <param name="ServedProfile">Profile that succeeded, or null when no attempt succeeded.</param>
/// <param name="Attempts">Ordered, read-only attempt evidence.</param>
public sealed record RouteCapture(string RequestedProfile, string? ServedProfile, IReadOnlyList<Attempt> Attempts);
/// <summary>Validated router or configuration and factory errors.</summary>
public closed record RouterBuildResult
{
    /// <summary>Successfully constructed router that owns its provider clients.</summary>
    /// <param name="Router">Created router.</param>
    public sealed record Ready(ProfileRouter Router) : RouterBuildResult;
    /// <summary>Router could not be created.</summary>
    /// <param name="Errors">Validation or provider-factory errors.</param>
    public sealed record Invalid(IReadOnlyList<string> Errors) : RouterBuildResult;
}
/// <summary>Routing failure with a classified kind and stable error code in the exception message.</summary>
/// <param name="kind">Failure classification.</param>
/// <param name="code">Stable routing error code.</param>
/// <param name="inner">Underlying exception, when available.</param>
public sealed class RouteException(FailureKind kind, string code, Exception? inner = null) : Exception(code, inner)
{
    /// <summary>Failure classification for this route.</summary>
    public FailureKind Kind { get; } = kind;
}

/// <summary>Activity and metric instruments for routing observations.</summary>
public static class ProfileTelemetry
{
    /// <summary>Shared instrumentation name for routing traces and metrics.</summary>
    public const string SourceName = "Zeeq.LlmCmd.Routing";
    /// <summary>Source of provider-attempt activities.</summary>
    public static readonly ActivitySource Activity = new(SourceName);
    /// <summary>Meter for provider attempts, duration and fallback counts.</summary>
    public static readonly Meter Meter = new(SourceName);
    internal static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("llmcmd.profile.fallbacks", "{transition}");
    internal static readonly Counter<long> Attempts = Meter.CreateCounter<long>("llmcmd.provider.attempts", "{attempt}");
    internal static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>("llmcmd.provider.attempt.duration", "s");
}

/// <summary>Routes through owned or borrowed clients; each For call returns an independent, non-owning request view.</summary>
public sealed class ProfileRouter : IDisposable
{
    readonly IReadOnlyDictionary<string, ExecutionProfile> profiles;
    sealed record RegisteredClient(IChatClient Client, Func<Exception, FailureKind> Classify, bool Owned, string? FixedModel);
    readonly IReadOnlyDictionary<string, RegisteredClient> providers;
    readonly ExecutionBudget budget;
    bool disposed;
    /// <summary>Snapshot of the configured profiles.</summary>
    public IReadOnlyCollection<ExecutionProfile> Profiles => profiles.Values.ToArray();

    // A profile name is not a route identity: deployments can point the same
    // name at another model or fallback. Composition fingerprints the requested
    // route before spending; the actual serving model remains attempt evidence.
    /// <summary>Computes a deterministic fingerprint of the requested route and effective retry budget.</summary>
    /// <param name="key">Configured profile key at the start of the fallback chain.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public string GetRouteFingerprint(ProfileKey key)
    {
        var value = new StringBuilder();
        void Part(string? text)
        {
            text ??= "";
            value.Append(text.Length).Append(':').Append(text);
        }
        string Number<T>(T number) where T : IFormattable => number.ToString(null, CultureInfo.InvariantCulture);
        Part(Number(budget.MaxAttempts));
        Part(Number((budget.Timeout ?? TimeSpan.FromSeconds(30)).Ticks));
        var profile = profiles[key.Id];
        while (true)
        {
            Part(profile.Key.Id);
            Part(profile.Provider);
            Part(profile.Model);
            Part(profile.Temperature?.ToString("R", CultureInfo.InvariantCulture));
            var retry = profile.Retry ?? new();
            Part(Number(retry.MaxRetries));
            Part(retry.InitialDelay?.Ticks.ToString(CultureInfo.InvariantCulture));
            Part(retry.UseJitter ? "true" : "false");
            Part(profile.AttemptTimeout?.Ticks.ToString(CultureInfo.InvariantCulture));
            if (profile.Defaults is { IsEmpty: false } || providers[profile.Provider].FixedModel is not null)
            {
                // Append only for the new configuration surface. Existing
                // routes must keep their exact fingerprints across this upgrade.
                Part("profile-defaults-v1");
                Part(providers[profile.Provider].FixedModel);
                Part(profile.Defaults?.Temperature?.ToString("R", CultureInfo.InvariantCulture));
                Part(profile.Defaults?.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture));
                Part(profile.Defaults?.ReasoningEffort?.ToString());
                Part(profile.Defaults?.TopP?.ToString("R", CultureInfo.InvariantCulture));
                Part(profile.Defaults?.TopK?.ToString(CultureInfo.InvariantCulture));
                Part(profile.Defaults?.ReasoningOutput?.ToString());
            }
            if (profile.Fallback is null)
            {
                break;
            }

            profile = profiles[profile.Fallback.Id];
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }
    private ProfileRouter(Dictionary<string, ExecutionProfile> profiles,
        Dictionary<string, RegisteredClient> providers, ExecutionBudget budget)
    {
        this.profiles = new ReadOnlyDictionary<string, ExecutionProfile>(profiles);
        this.providers = providers;
        this.budget = budget;
    }

    /// <summary>Validates profile definitions and resolves configured clients only after validation succeeds.</summary>
    /// <param name="definitions">Profiles and their fallback chains.</param>
    /// <param name="registrations">Named provider factories and classifiers.</param>
    /// <param name="budget">Shared attempt and deadline limits; null uses defaults.</param>
    /// <returns>A router, or validation or factory errors. Owned clients created before a factory failure are disposed.</returns>
    public static RouterBuildResult TryCreate(IEnumerable<ExecutionProfile> definitions,
        IReadOnlyDictionary<string, ProviderRegistration> registrations, ExecutionBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(registrations);
        budget ??= new();
        var configured = definitions.ToArray();
        var configuredRegistrations = registrations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (configuredRegistrations.Values.Any(registration => registration is null || registration.Factory is null))
        {
            return new RouterBuildResult.Invalid(Array.AsReadOnly(new[] { "A provider registration is missing its factory" }));
        }
        var errors = ValidateConfiguration(configured, configuredRegistrations.ToDictionary(pair => pair.Key, pair => pair.Value.FixedModel, StringComparer.Ordinal), budget);
        if (errors.Count > 0)
        {
            return new RouterBuildResult.Invalid(errors);
        }

        var profiles = configured.ToDictionary(profile => profile.Key.Id, StringComparer.Ordinal);
        var clients = new Dictionary<string, RegisteredClient>(StringComparer.Ordinal);
        try
        {
            foreach (var name in profiles.Values.Select(p => p.Provider).Distinct(StringComparer.Ordinal))
            {
                var registration = configuredRegistrations[name];
                var client = registration.Factory();
                if (client is null)
                {
                    throw new InvalidOperationException("Null provider");
                }

                clients.Add(name, new(client, registration.Classify ?? ClassifyDefault, registration.OwnsClient, registration.FixedModel));
                if (registration.FixedModel is { } fixedModel
                    && client.GetService<ChatClientMetadata>()?.DefaultModelId is { } actualModel
                    && !string.Equals(fixedModel, actualModel, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Client model binding does not match its registration");
                }
            }
        }
        catch
        {
            DisposeOwned(clients.Values);
            return new RouterBuildResult.Invalid(Array.AsReadOnly(new[] { "A provider factory or model binding failed" }));
        }
        return new RouterBuildResult.Ready(new(profiles, clients, budget));
    }

    // Setup validation must run without creating SDK clients or resolving DI.
    // Keep it shared with TryCreate so the fluent and low-level paths agree.
    internal static IReadOnlyList<string> ValidateConfiguration(IReadOnlyList<ExecutionProfile> definitions,
        IReadOnlyDictionary<string, string?> clientModels, ExecutionBudget budget)
    {
        var errors = new List<string>();
        var profiles = new Dictionary<string, ExecutionProfile>(StringComparer.Ordinal);
        if (budget.MaxAttempts is < 1 or > 100 || budget.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)))
        {
            errors.Add("Invalid execution budget");
        }

        foreach (var p in definitions)
        {
            if (p is null || p.Key is null || !profiles.TryAdd(p.Key.Id, p))
            {
                errors.Add("Missing or duplicate profile ID");
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.Provider) || !clientModels.TryGetValue(p.Provider, out var fixedModel))
            {
                errors.Add($"Provider missing for '{p.Key.Id}'");
            }
            else if (fixedModel is not null && !string.Equals(fixedModel, p.Model, StringComparison.Ordinal))
            {
                errors.Add($"Client model binding differs for '{p.Key.Id}'");
            }

            if (string.IsNullOrWhiteSpace(p.Model) || p.Temperature is float t && !float.IsFinite(t))
            {
                errors.Add($"Invalid settings for '{p.Key.Id}'");
            }

            if (p.Defaults is { } defaults && (!defaults.IsValid
                || p.Temperature is { } temperature && defaults.Temperature is { } otherTemperature && temperature != otherTemperature))
            {
                errors.Add($"Invalid request defaults for '{p.Key.Id}'");
            }

            if (p.Retry is { } r && (r.MaxRetries is < 0 or > 20 || r.InitialDelay is { } d && (d < TimeSpan.Zero || d > TimeSpan.FromMinutes(1))))
            {
                errors.Add($"Invalid retry policy for '{p.Key.Id}'");
            }

            if (p.AttemptTimeout is { } a && (a <= TimeSpan.Zero || a > TimeSpan.FromHours(1)))
            {
                errors.Add($"Invalid attempt timeout for '{p.Key.Id}'");
            }
        }
        foreach (var p in profiles.Values)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ExecutionProfile? current = p;
            while (current is not null)
            {
                if (!seen.Add(current.Key.Id))
                {
                    errors.Add($"Fallback cycle at '{p.Key.Id}'");
                    break;
                }
                if (current.Fallback is null)
                {
                    break;
                }

                if (!profiles.TryGetValue(current.Fallback.Id, out current))
                {
                    errors.Add($"Missing fallback for '{p.Key.Id}'");
                    break;
                }
            }
        }
        return errors.AsReadOnly();
    }
    /// <summary>Creates an independent non-owning request view for one route execution.</summary>
    /// <param name="key">Initially requested profile.</param>
    /// <param name="context">Optional execution identity for attempt events.</param>
    /// <param name="events">Optional observational event sink.</param>
    /// <param name="defaultTemperature">Fallback temperature after request and profile settings.</param>
    /// <param name="journal">Optional authoritative execution journal.</param>
    /// <returns>A view that does not own the router's provider clients.</returns>
    public RoutedProfileClient For(ProfileKey key, CommandExecutionContext? context = null, IExecutionEventSink? events = null, float? defaultTemperature = null, IExecutionJournal? journal = null) => new(this, key, context, events, defaultTemperature, journal);
    /// <summary>Disposes each distinct provider client owned by this router.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DisposeOwned(providers.Values);
    }

    static void DisposeOwned(IEnumerable<RegisteredClient> registrations)
    {
        var clients = registrations.ToArray();
        // Borrowed aliases remain DI-owned even if an owned factory returned
        // that same instance. Each distinct owned client is disposed once.
        foreach (var client in clients.Where(item => item.Owned)
            .Select(item => item.Client).Distinct<IChatClient>(ReferenceEqualityComparer.Instance)
            .Where(client => !clients.Any(item => !item.Owned && ReferenceEquals(item.Client, client))))
        {
            try
            {
                client.Dispose();
            }
            catch { }
        }
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
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (!task.IsCompleted)
            {
                _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    internal async Task<ChatResponse> ExecuteAsync(RoutedProfileClient view, IEnumerable<ChatMessage> input, ChatOptions? patch, CancellationToken callerToken)
    {
        var records = new List<Attempt>();
        string? served = null;
        var requested = view.Key.Id;
        var parent = System.Diagnostics.Activity.Current;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var kind = "initial";
        try
        {
            if (disposed)
            {
                throw new RouteException(FailureKind.Permanent, "router_disposed");
            }

            if (!profiles.TryGetValue(requested, out var profile))
            {
                throw new RouteException(FailureKind.Permanent, "profile_not_configured");
            }
            // The experiment supports text completion. Do not silently replay tool actions.
            if (patch?.Tools is { Count: > 0 })
            {
                throw new RouteException(FailureKind.Permanent, "tools_require_inner_model_call_routing");
            }

            var messages = input.Select(m => m.Clone()).ToArray();
            parent?.SetTag("llmcmd.profile.requested", requested);
            while (true)
            {
                var retry = profile.Retry ?? new();
                FailureKind failure = FailureKind.Permanent;
                Exception? last = null;
                for (int i = 0; i <= retry.MaxRetries; i++)
                {
                    if (callerToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(callerToken);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        throw new RouteException(FailureKind.Timeout, "execution_deadline");
                    }

                    if (records.Count >= budget.MaxAttempts)
                    {
                        throw new RouteException(FailureKind.Permanent, "attempt_budget_exhausted");
                    }

                    using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attemptToken.CancelAfter(profile.AttemptTimeout ?? TimeSpan.FromSeconds(10));
                    var options = patch?.Clone() ?? new();
                    options.ModelId = profile.Model;
                    options.Temperature = patch?.Temperature ?? profile.Temperature ?? profile.Defaults?.Temperature ?? view.DefaultTemperature;
                    profile.Defaults?.Apply(options);
                    var number = records.Count + 1;
                    var attemptId = Guid.NewGuid();
                    var started = Stopwatch.StartNew();
                    ChatResponse? response = null, observed = null;
                    if (view.Context is { } execution)
                    {
                        // Reserve before every send, including retries and fallback.
                        // A telemetry failure may be ignored; a journal failure may not.
                        var reserved = new ExecutionEvent("attempt.started", execution, attemptId, number, profile.Provider);
                        await ExecutionJournal.AppendAsync(view.Journal, reserved, callerToken);
                        await ExecutionEvents.EmitSafelyAsync(view.Events, reserved);
                    }
                    using (var span = ProfileTelemetry.Activity.StartActivity("llmcmd.provider.attempt", ActivityKind.Internal))
                    {
                        view.Context?.Tag(span);
                        span?.SetTag("llmcmd.attempt.id", attemptId.ToString());
                        span?.SetTag("llmcmd.profile", profile.Key.Id);
                        span?.SetTag("gen_ai.provider.name", profile.Provider);
                        span?.SetTag("gen_ai.request.model", options.ModelId);
                        span?.SetTag("llmcmd.attempt.number", number);
                        span?.SetTag("llmcmd.attempt.kind", kind);
                        try
                        {
                            observed = await AwaitProvider(providers[profile.Provider].Client.GetResponseAsync(messages.Select(m => m.Clone()), options, attemptToken.Token), attemptToken.Token).ConfigureAwait(false);
                            callerToken.ThrowIfCancellationRequested();
                            if (attemptToken.IsCancellationRequested)
                            {
                                throw new TimeoutException("Attempt exceeded its deadline");
                            }

                            response = observed;
                            failure = default;
                        }
                        catch (Exception error)
                        {
                            last = error;
                            if (callerToken.IsCancellationRequested)
                            {
                                failure = FailureKind.Cancelled;
                            }
                            else if (attemptToken.IsCancellationRequested)
                            {
                                failure = FailureKind.Timeout;
                            }
                            else
                            {
                                try
                                {
                                    failure = providers[profile.Provider].Classify(error);
                                    if (failure == FailureKind.Cancelled)
                                    {
                                        failure = FailureKind.Timeout;
                                    }
                                }
                                catch { failure = FailureKind.Permanent; }
                            }
                            span?.SetStatus(ActivityStatusCode.Error);
                            span?.SetTag("error.type", failure.ToString());
                        }
                        started.Stop();
                        var outcome = response is not null ? "success" : failure == FailureKind.Cancelled ? "cancelled" : "failed";
                        var attemptTags = new TagList { { "llmcmd.profile", profile.Key.Id }, { "gen_ai.provider.name", profile.Provider }, { "llmcmd.attempt.kind", kind }, { "outcome", outcome } };
                        ProfileTelemetry.Attempts.Add(1, attemptTags);
                        ProfileTelemetry.AttemptDuration.Record(started.Elapsed.TotalSeconds, attemptTags);
                        span?.SetTag("gen_ai.response.model", observed?.ModelId);
                        span?.SetTag("gen_ai.response.id", observed?.ResponseId);
                        records.Add(new(number, profile.Key.Id, profile.Provider, profile.Model, options.Temperature, kind, outcome, response is null ? failure : null,
                            observed?.ModelId, observed?.ResponseId, observed?.Usage?.InputTokenCount, observed?.Usage?.OutputTokenCount, started.Elapsed.TotalSeconds, view.Context, attemptId));
                        if (view.Context is { } completed)
                        {
                            var finished = new ExecutionEvent("attempt.finished", completed, attemptId, number, profile.Provider, outcome, observed?.Usage?.InputTokenCount, observed?.Usage?.OutputTokenCount);
                            try
                            {
                                await ExecutionJournal.AppendAsync(view.Journal, finished, CancellationToken.None);
                            }
                            catch (ExecutionJournalUnavailableException) { }
                            await ExecutionEvents.EmitSafelyAsync(view.Events, finished);
                        }
                    }
                    if (response is not null)
                    {
                        served = profile.Key.Id;
                        parent?.SetTag("llmcmd.profile.served", served);
                        return response;
                    }
                    if (callerToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(callerToken);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        throw new RouteException(FailureKind.Timeout, "execution_deadline", last);
                    }

                    if (!Eligible(failure))
                    {
                        throw new RouteException(failure, "provider_failure", last);
                    }

                    if (records.Count >= budget.MaxAttempts)
                    {
                        throw new RouteException(FailureKind.Permanent, "attempt_budget_exhausted", last);
                    }

                    if (i < retry.MaxRetries)
                    {
                        var delay = Math.Min(30_000, (retry.InitialDelay ?? TimeSpan.FromMilliseconds(100)).TotalMilliseconds * Math.Pow(2, i));
                        if (retry.UseJitter)
                        {
                            delay *= .5 + Random.Shared.NextDouble() * .5;
                        }

                        try
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(delay), ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { throw new RouteException(FailureKind.Timeout, "execution_deadline"); }
                        kind = "retry";
                    }
                }
                if (profile.Fallback is null)
                {
                    throw new RouteException(failure, "fallback_exhausted", last);
                }

                if (records.Count >= budget.MaxAttempts)
                {
                    throw new RouteException(FailureKind.Permanent, "attempt_budget_exhausted");
                }

                if (callerToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(callerToken);
                }

                if (ct.IsCancellationRequested)
                {
                    throw new RouteException(FailureKind.Timeout, "execution_deadline");
                }

                var next = profiles[profile.Fallback.Id];
                var tags = new TagList { { "llmcmd.profile.requested", requested }, { "llmcmd.profile.from", profile.Key.Id },
                    { "llmcmd.profile.to", next.Key.Id }, { "llmcmd.fallback.reason", failure.ToString() } };
                parent?.AddEvent(new ActivityEvent("llmcmd.profile.fallback", tags: new ActivityTagsCollection(tags)));
                ProfileTelemetry.Fallbacks.Add(1, tags);
                profile = next;
                kind = "fallback";
            }
        }
        finally { view.Capture = new(requested, served, records.AsReadOnly()); }
    }
}

/// <summary>Single-execution routing view over shared router-owned provider clients; streaming is not supported.</summary>
public sealed class RoutedProfileClient : IChatClient
{
    readonly ProfileRouter router; internal ProfileKey Key
    {
        get;
    }
    internal CommandExecutionContext? Context
    {
        get;
    }
    internal IExecutionEventSink? Events
    {
        get;
    }
    internal IExecutionJournal? Journal
    {
        get;
    }
    internal float? DefaultTemperature
    {
        get;
    }
    /// <summary>Attempt evidence from the completed route, including unsuccessful routes; null before completion.</summary>
    public RouteCapture? Capture
    {
        get; internal set;
    }
    internal RoutedProfileClient(ProfileRouter router, ProfileKey key, CommandExecutionContext? context, IExecutionEventSink? events, float? defaultTemperature, IExecutionJournal? journal)
    {
        this.router = router;
        Key = key;
        Context = context;
        Events = events;
        DefaultTemperature = defaultTemperature;
        Journal = journal;
    }
    // Create one view per execution; provider clients and policies are shared by the owning router.
    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => router.ExecuteAsync(this, messages, options, cancellationToken);
    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Streaming routing is not implemented.</exception>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("Streaming commitment needs a separate routing implementation");
    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    /// <summary>Releases the view; router-owned provider clients remain owned by the router.</summary>
    public void Dispose()
    {
    } // View never disposes router-owned provider clients.
}
