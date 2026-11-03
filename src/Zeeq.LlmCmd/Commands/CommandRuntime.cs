using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;

namespace Zeeq.LlmCmd;

/// <summary>
/// Runs typed commands through one validation, provider and storage boundary.
/// Observers cannot change the outcome; the host's ledger and journal remain
/// responsible for authorization and recording work before provider spend.
/// </summary>
public sealed partial class CommandRuntime : IDisposable
{
    static readonly ActivitySource Activity = new("Zeeq.LlmCmd");
    readonly IChatClient? client;
    readonly ProfileRouter? router;
    readonly bool ownsRouter;
    readonly IReadOnlyDictionary<string, Profile> profiles;
    readonly IHumanFeedbackLedger ledger;
    readonly Func<Actor> actorProvider;
    readonly Action<ExecutionOutcomeRecord>? sink;
    readonly IExecutionEventSink? events;
    readonly string? defaultProfile;
    bool disposed;

    private CommandRuntime(
        IChatClient client,
        IReadOnlyDictionary<string, Profile> profiles,
        IHumanFeedbackLedger ledger,
        Func<Actor> actorProvider,
        Action<ExecutionOutcomeRecord>? sink,
        IExecutionEventSink? events,
        ProfileKey? defaultProfile
    )
    {
        this.client = client;
        this.profiles = profiles;
        this.ledger = ledger;
        this.actorProvider = actorProvider;
        this.sink = sink;
        this.events = events;
        this.defaultProfile = DefaultProfile(profiles, defaultProfile);
    }

    /// <summary>Creates a runtime owning a client from the supplied factory and using the supplied profiles, ledger and observers.</summary>
    public static CommandRuntime Create(
        Func<IChatClient> factory,
        IReadOnlyDictionary<string, Profile> profiles,
        IHumanFeedbackLedger ledger,
        Func<Actor> actorProvider,
        Action<ChatClientBuilder>? configure = null,
        Action<ExecutionOutcomeRecord>? sink = null,
        IExecutionEventSink? events = null,
        ProfileKey? defaultProfile = null
    )
    {
        var b = new ChatClientBuilder(factory());
        configure?.Invoke(b);
        return new(
            b.Build(),
            new Dictionary<string, Profile>(profiles),
            ledger,
            actorProvider,
            sink,
            events,
            defaultProfile
        );
    }

    private CommandRuntime(
        ProfileRouter router,
        IHumanFeedbackLedger ledger,
        Func<Actor> actorProvider,
        Action<ExecutionOutcomeRecord>? sink,
        bool ownsRouter,
        IExecutionEventSink? events,
        ProfileKey? defaultProfile
    )
    {
        this.router = router;
        this.profiles = router.Profiles.ToDictionary(
            p => p.Key.Id,
            p => new Profile(p.Model, p.Temperature ?? p.Defaults?.Temperature),
            StringComparer.Ordinal
        );
        this.ledger = ledger;
        this.actorProvider = actorProvider;
        this.sink = sink;
        this.ownsRouter = ownsRouter;
        this.events = events;
        this.defaultProfile = DefaultProfile(profiles, defaultProfile);
    }

    /// <summary>Creates a runtime using a profile router; router disposal is controlled by ownsRouter.</summary>
    public static CommandRuntime Create(
        ProfileRouter router,
        IHumanFeedbackLedger ledger,
        Func<Actor> actorProvider,
        Action<ExecutionOutcomeRecord>? sink = null,
        bool ownsRouter = false,
        IExecutionEventSink? events = null,
        ProfileKey? defaultProfile = null
    ) => new(router, ledger, actorProvider, sink, ownsRouter, events, defaultProfile);

    static string? DefaultProfile(
        IReadOnlyDictionary<string, Profile> profiles,
        ProfileKey? selected
    )
    {
        if (selected is not null && !profiles.ContainsKey(selected.Id))
        {
            throw new ArgumentException("Default profile is not configured", nameof(selected));
        }

        return selected?.Id ?? (profiles.Count == 1 ? profiles.Keys.Single() : null);
    }

    internal void ValidateProfiles(IReadOnlyList<CommandRegistration> commands)
    {
        var invalid = commands
            .Where(c =>
                !profiles.ContainsKey(
                    string.IsNullOrEmpty(c.DefaultProfile) ? defaultProfile ?? "" : c.DefaultProfile
                )
            )
            .Select(c => c.Id)
            .ToArray();
        if (invalid.Length > 0)
        {
            throw new InvalidOperationException(
                "Command default profiles are not configured: " + string.Join(", ", invalid)
            );
        }
    }

    internal async Task<Outcome<TR>> ExecuteAsync<TP, TR>(
        PromptDescriptor<TP, TR> descriptor,
        TP parameters,
        string profile,
        float? temperature,
        CommandExecutionContext context,
        string? pendingError,
        CallSite caller,
        CancellationToken ct,
        IExecutionJournal? journal = null,
        PreparedCommandInput? prepared = null
    )
    {
        var id = context.ExecutionId;
        var run = context.Operation.RunId;
        ExecutionSnapshot? snapshot = null;
        using var activity = Activity.StartActivity("llmcmd.execute");
        context.Tag(activity);
        activity?.SetTag("gen_ai.prompt.name", descriptor.Id);
        activity?.SetTag(
            "gen_ai.prompt.version",
            descriptor.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        await ExecutionEvents.EmitSafelyAsync(
            events,
            new ExecutionEvent("execution.started", context)
        );
        Outcome<TR> outcome;
        try
        {
            outcome = await Run();
        }
        catch (Exception)
        {
            outcome = new Outcome<TR>.Failed(
                new ExecutionFailure.ConfigurationFailure(
                    context,
                    "Execution pipeline failed",
                    snapshot
                )
            );
        }
        string kind = outcome switch
        {
            Outcome<TR>.Success => "success",
            Outcome<TR>.Failed failed => failed.Failure.Kind,
        };
        activity?.SetTag("llmcmd.execution.outcome", kind);
        if (kind is not "success" and not "cancelled")
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        try
        {
            sink?.Invoke(new(id, kind, snapshot, context));
        }
        catch (Exception)
        { /* Independent delivery; a sink cannot replace the outcome. */
        }
        await ExecutionEvents.EmitSafelyAsync(
            events,
            new ExecutionEvent("execution.finished", context, Outcome: kind)
        );
        return outcome;
        async Task<Outcome<TR>> Run()
        {
            if (pendingError is not null)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(context, pendingError)
                );
            }

            if (disposed)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidInput(context, "Runtime disposed")
                );
            }

            Actor actor;
            try
            {
                actor = actorProvider();
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(context, "Actor context unavailable")
                );
            }
            if (
                actor is null
                || string.IsNullOrWhiteSpace(actor.Tenant)
                || string.IsNullOrWhiteSpace(actor.Subject)
            )
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(context, "Actor context invalid")
                );
            }

            string? invalid;
            try
            {
                invalid =
                    prepared is null ? descriptor.ValidateInput(parameters)
                    : prepared.HookPhase == "ValidateInput" ? throw new InvalidOperationException()
                    : prepared.InvalidReason;
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.HookFailure(context, "ValidateInput")
                );
            }
            if (invalid is not null)
            {
                return new Outcome<TR>.Failed(new ExecutionFailure.InvalidInput(context, invalid));
            }

            var settings = prepared?.Settings ?? FreezeSettings(descriptor, profile, temperature);
            profile = settings.Profile;
            if (settings.Error == "profile_invalid")
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidInput(context, "Run or profile invalid")
                );
            }

            if (settings.Error == "defaults_hook_failed")
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.HookFailure(context, "ConfigureDefaults")
                );
            }

            if (settings.Error == "default_temperature_invalid")
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "Default temperature invalid"
                    )
                );
            }

            float? effective = settings.Temperature;
            snapshot = new(
                descriptor.Id,
                descriptor.Revision,
                descriptor.DefinitionHash,
                descriptor.UserTemplate,
                null,
                Array.Empty<ParameterRecord>(),
                profile,
                settings.Model,
                effective
            );
            if (ct.IsCancellationRequested)
            {
                return new Outcome<TR>.Failed(new ExecutionFailure.Cancelled(context, snapshot));
            }

            if (run == Guid.Empty)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidInput(context, "Run or profile invalid", snapshot)
                );
            }

            if (effective is float t && !float.IsFinite(t))
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidInput(context, "Temperature invalid", snapshot)
                );
            }

            try
            {
                // Discover contract errors before creating work or paying for an
                // answer. Schema and deserialization must describe the same DTO.
                descriptor.Contract?.Validate();
                if (descriptor.Contract is null && descriptor.ResultType is null)
                {
                    throw new InvalidOperationException();
                }
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "JSON contract invalid",
                        snapshot
                    )
                );
            }
            Guid runId;
            try
            {
                runId = run ?? ledger.CreateRun(actor);
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "Run creation failed",
                        snapshot
                    )
                );
            }
            if (runId == Guid.Empty)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "Run creation failed",
                        snapshot
                    )
                );
            }

            if (prepared is not null && !ledger.CanAccessRun(runId, actor))
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "Composition run not accessible",
                        snapshot
                    )
                );
            }

            string prompt,
                system;
            try
            {
                if (prepared?.HookPhase == "Render")
                {
                    throw new InvalidOperationException();
                }

                prompt = prepared?.Prompt ?? descriptor.Render(parameters);
                system = prepared?.System ?? descriptor.RenderSystem(parameters);
                snapshot = snapshot with { FormattedPrompt = prompt };
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.HookFailure(context, "Render", snapshot)
                );
            }
            IReadOnlyList<ParameterRecord> projected;
            try
            {
                if (prepared?.HookPhase == "Project")
                {
                    throw new InvalidOperationException();
                }

                projected = prepared?.Parameters ?? descriptor.Project(parameters);
                snapshot = snapshot with { Parameters = projected };
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.HookFailure(context, "Project", snapshot)
                );
            }
            ChatResponseFormat format;
            try
            {
                format = ChatResponseFormat.ForJsonSchema(
                    descriptor.Schema.Value,
                    descriptor.SchemaName
                );
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "Schema generation failed",
                        snapshot
                    )
                );
            }
            ChatResponse response;
            activity?.SetTag("llmcmd.run.id", runId.ToString());
            var messages =
                system.Length == 0
                    ? new[] { new ChatMessage(ChatRole.User, prompt) }
                    : new[]
                    {
                        new ChatMessage(ChatRole.System, system),
                        new ChatMessage(ChatRole.User, prompt),
                    };
            RoutedProfileClient? routed = router?.For(
                ProfileKey.Declare(profile),
                context,
                events,
                settings.DefaultTemperature,
                journal
            );
            try
            {
                response = await (
                    routed is not null
                        ? routed.GetResponseAsync(
                            messages,
                            new ChatOptions { Temperature = temperature, ResponseFormat = format },
                            ct
                        )
                        : SingleAttemptAsync()
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (routed is not null)
                {
                    snapshot = snapshot with { Routing = routed.Capture };
                }

                return new Outcome<TR>.Failed(new ExecutionFailure.Cancelled(context, snapshot));
            }
            catch (ExecutionJournalUnavailableException)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ConfigurationFailure(
                        context,
                        "Execution journal unavailable",
                        snapshot
                    )
                );
            }
            catch (Exception)
            {
                if (routed is not null)
                {
                    snapshot = snapshot with { Routing = routed.Capture };
                }

                return new Outcome<TR>.Failed(
                    new ExecutionFailure.ProviderFailure(context, "Model request failed", snapshot)
                );
            }
            finally
            {
                if (routed is not null)
                {
                    snapshot = snapshot with { Routing = routed.Capture };
                }
            }
            TR? result;
            try
            {
                result = JsonSerializer.Deserialize(
                    response.Text,
                    descriptor.Contract?.Result ?? descriptor.ResultType!
                );
            }
            catch (JsonException)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidOutput(context, "Invalid JSON result", snapshot)
                );
            }
            if (result is null)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidOutput(context, "Null result", snapshot)
                );
            }

            string? validation;
            try
            {
                validation = descriptor.ValidateResult(result);
            }
            catch (Exception)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.HookFailure(context, "ValidateResult", snapshot)
                );
            }
            if (validation is not null)
            {
                return new Outcome<TR>.Failed(
                    new ExecutionFailure.InvalidOutput(context, validation, snapshot)
                );
            }

            OutputDelivery delivery;
            try
            {
                // Preserve the exact provider JSON as evidence. Typed projection
                // is for consumers; reserializing it could erase unknown fields
                // or explicit nulls needed by later audits and field-level grades.
                using var wire = JsonDocument.Parse(response.Text);
                var stored = ledger.RecordOutput(
                    runId,
                    id,
                    wire.RootElement,
                    descriptor.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    activity?.Context ?? default,
                    actor
                );
                delivery = stored switch
                {
                    LedgerResult<OutputArtifact>.Accepted ok => new OutputDelivery.Stored(ok.Value),
                    LedgerResult<OutputArtifact>.Rejected e => new OutputDelivery.Rejected(e.Code),
                };
            }
            catch (Exception)
            {
                delivery = new OutputDelivery.Failed();
            }
            await ExecutionEvents.EmitSafelyAsync(
                events,
                new ExecutionEvent(
                    "output.delivery",
                    context,
                    Outcome: delivery switch
                    {
                        OutputDelivery.Stored => "stored",
                        OutputDelivery.Rejected => "rejected",
                        OutputDelivery.Failed => "failed",
                    }
                )
            );
            return new Outcome<TR>.Success(result, runId, delivery, caller, snapshot, context);
            async Task<ChatResponse> SingleAttemptAsync()
            {
                var attemptId = Guid.NewGuid();
                ChatResponse? answer = null;
                var attemptOutcome = "failed";
                // The journal is a gate, unlike best-effort event delivery. A
                // failed pre-send append must stop this attempt before spend.
                await ExecutionJournal.AppendAsync(
                    journal,
                    new("attempt.started", context, attemptId, 1),
                    ct
                );
                await ExecutionEvents.EmitSafelyAsync(
                    events,
                    new ExecutionEvent("attempt.started", context, attemptId, 1)
                );
                using var attempt = Activity.StartActivity("llmcmd.provider.attempt");
                context.Tag(attempt);
                attempt?.SetTag("llmcmd.attempt.id", attemptId.ToString());
                try
                {
                    answer = await client!.GetResponseAsync(
                        messages,
                        new ChatOptions
                        {
                            Temperature = effective,
                            ModelId = settings.Model,
                            ResponseFormat = format,
                        },
                        ct
                    );
                    attemptOutcome = "success";
                    return answer;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    attemptOutcome = "cancelled";
                    throw;
                }
                finally
                {
                    var finished = new ExecutionEvent(
                        "attempt.finished",
                        context,
                        attemptId,
                        1,
                        Outcome: attemptOutcome,
                        InputTokens: answer?.Usage?.InputTokenCount,
                        OutputTokens: answer?.Usage?.OutputTokenCount
                    );
                    // A failed post-send journal write must not discard a good
                    // response. Its reservation remains unknown until the host reconciles it.
                    try
                    {
                        await ExecutionJournal.AppendAsync(
                            journal,
                            finished,
                            CancellationToken.None
                        );
                    }
                    catch (ExecutionJournalUnavailableException) { }
                    await ExecutionEvents.EmitSafelyAsync(events, finished);
                }
            }
        }
    }

    /// <summary>Disposes the owned client and any router explicitly owned by this runtime.</summary>
    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            client?.Dispose();
            if (ownsRouter)
            {
                router?.Dispose();
            }
        }
    }
}
