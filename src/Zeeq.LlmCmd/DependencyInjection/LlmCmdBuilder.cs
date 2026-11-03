using System.Collections.Frozen;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Zeeq.LlmCmd;

/// <summary>Configures the shared command runtime.</summary>
public sealed class LlmCmdBuilder
{
    private Func<IServiceProvider, IChatClient>? client;
    private Func<IServiceProvider, Actor>? actor;
    private ProfileKey? defaultProfile;
    private readonly Dictionary<string, Profile> profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientRegistration> clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionProfile> routes = new(StringComparer.Ordinal);
    private ExecutionBudget budget = new();

    private sealed record ClientRegistration(
        Func<IServiceProvider, IChatClient> Factory,
        bool Owned,
        string? FixedModel,
        Func<Exception, FailureKind>? Classify
    );

    internal LlmCmdBuilder() { }

    /// <summary>
    /// Supplies a client for the runtime. The runtime disposes the returned client when the service provider is disposed.
    /// A caller-owned singleton must be wrapped in a non-owning client if it must outlive the runtime.
    /// </summary>
    public LlmCmdBuilder UseChatClient(Func<IServiceProvider, IChatClient> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (client is not null || clients.Count > 0)
        {
            throw new InvalidOperationException("A chat client is already configured.");
        }

        client = factory;
        return this;
    }

    /// <summary>
    /// Registers a named, runtime-owned pipeline. The factory must return a new
    /// client; use the keyed overload for clients already owned by DI.
    /// </summary>
    /// <param name="name">Case-sensitive client-registration name referenced by profiles.</param>
    /// <param name="factory">Creates the complete MEAI pipeline without replacing host middleware.</param>
    /// <param name="fixedModel">Explicit model or deployment binding; null declares per-request model selection.</param>
    /// <param name="classify">Optional provider exception classifier for retry and fallback.</param>
    public LlmCmdBuilder AddChatClient(
        string name,
        Func<IServiceProvider, IChatClient> factory,
        string? fixedModel = null,
        Func<Exception, FailureKind>? classify = null
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        return AddClient(name, new(factory, true, fixedModel, classify));
    }

    /// <summary>
    /// References a keyed MEAI client owned by the host service provider. Register
    /// its singleton-compatible pipeline with AddKeyedChatClient or keyed DI
    /// before resolving commands; the command runtime itself is a singleton.
    /// </summary>
    /// <param name="name">Case-sensitive client-registration name referenced by profiles.</param>
    /// <param name="serviceKey">DI service key; null uses the registration name.</param>
    /// <param name="fixedModel">Explicit model or deployment binding; null declares per-request model selection.</param>
    /// <param name="classify">Optional provider exception classifier for retry and fallback.</param>
    public LlmCmdBuilder AddChatClient(
        string name,
        object? serviceKey = null,
        string? fixedModel = null,
        Func<Exception, FailureKind>? classify = null
    ) =>
        AddClient(
            name,
            new(
                sp => sp.GetRequiredKeyedService<IChatClient>(serviceKey ?? name),
                false,
                fixedModel,
                classify
            )
        );

    private LlmCmdBuilder AddClient(string name, ClientRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (registration.FixedModel is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.FixedModel);
        }

        if (client is not null)
        {
            throw new InvalidOperationException(
                "UseChatClient cannot be combined with named client registrations."
            );
        }

        if (!clients.TryAdd(name, registration))
        {
            throw new InvalidOperationException("Client is already configured: " + name);
        }

        return this;
    }

    /// <summary>Sets the actor accessor used by commands and composition.</summary>
    public LlmCmdBuilder UseActor(Func<IServiceProvider, Actor> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (actor is not null)
        {
            throw new InvalidOperationException("An actor accessor is already configured.");
        }

        actor = factory;
        return this;
    }

    /// <summary>Adds a named model profile.</summary>
    public LlmCmdBuilder AddProfile(string name, string model, float? temperature = null) =>
        AddProfile(ProfileKey.Declare(name), model, temperature);

    /// <summary>Adds a named model profile.</summary>
    public LlmCmdBuilder AddProfile(ProfileKey key, string model, float? temperature = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model is required.", nameof(model));
        }

        if (temperature is float value && !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature));
        }

        if (routes.ContainsKey(key.Id) || !profiles.TryAdd(key.Id, new Profile(model, temperature)))
        {
            throw new InvalidOperationException("Profile is already configured: " + key.Id);
        }

        return this;
    }

    /// <summary>Maps a profile to a named client and explicit model or deployment.</summary>
    /// <param name="name">Profile identifier used by WithProfile.</param>
    /// <param name="client">Named client registration, independent of provider brand.</param>
    /// <param name="model">Requested model or Azure deployment identifier.</param>
    /// <param name="defaults">Portable inference defaults beneath explicit request options.</param>
    /// <param name="retry">Retry policy for this profile.</param>
    /// <param name="fallback">Next profile after eligible failures; never selected by guessing a model name.</param>
    /// <param name="attemptTimeout">Per-attempt deadline.</param>
    public LlmCmdBuilder AddProfile(
        string name,
        string client,
        string model,
        ProfileDefaults? defaults = null,
        RetryPolicy? retry = null,
        ProfileKey? fallback = null,
        TimeSpan? attemptTimeout = null
    ) =>
        AddProfile(
            ProfileKey.Declare(name),
            client,
            model,
            defaults,
            retry,
            fallback,
            attemptTimeout
        );

    /// <summary>Maps a typed profile key to a named client and explicit model or deployment.</summary>
    /// <param name="key">Typed profile identifier used by WithProfile.</param>
    /// <param name="client">Named client registration, independent of provider brand.</param>
    /// <param name="model">Requested model or Azure deployment identifier.</param>
    /// <param name="defaults">Portable inference defaults beneath explicit request options.</param>
    /// <param name="retry">Retry policy for this profile.</param>
    /// <param name="fallback">Next profile after eligible failures.</param>
    /// <param name="attemptTimeout">Per-attempt deadline.</param>
    public LlmCmdBuilder AddProfile(
        ProfileKey key,
        string client,
        string model,
        ProfileDefaults? defaults = null,
        RetryPolicy? retry = null,
        ProfileKey? fallback = null,
        TimeSpan? attemptTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (
            profiles.ContainsKey(key.Id)
            || !routes.TryAdd(
                key.Id,
                new(
                    key,
                    client,
                    model,
                    defaults?.Temperature,
                    retry,
                    fallback,
                    attemptTimeout,
                    defaults
                )
            )
        )
        {
            throw new InvalidOperationException("Profile is already configured: " + key.Id);
        }

        return this;
    }

    /// <summary>Sets the shared attempt and deadline budget for named-client routes.</summary>
    /// <param name="value">Budget shared across retries and fallbacks.</param>
    public LlmCmdBuilder UseBudget(ExecutionBudget value)
    {
        ArgumentNullException.ThrowIfNull(value);
        budget = value;
        return this;
    }

    /// <summary>Selects the profile for commands without an authored profile.</summary>
    public LlmCmdBuilder UseDefaultProfile(string name) =>
        UseDefaultProfile(ProfileKey.Declare(name));

    /// <summary>Selects the profile for commands without an authored profile.</summary>
    public LlmCmdBuilder UseDefaultProfile(ProfileKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (defaultProfile is not null)
        {
            throw new InvalidOperationException("A default profile is already configured.");
        }

        defaultProfile = key;
        return this;
    }

    internal void Register(IServiceCollection services)
    {
        var actorFactory =
            actor ?? throw new InvalidOperationException("An actor accessor must be configured.");
        if (client is null && clients.Count == 0)
        {
            throw new InvalidOperationException("A chat client must be configured.");
        }

        if (profiles.Count == 0 && routes.Count == 0)
        {
            throw new InvalidOperationException("At least one profile must be configured.");
        }

        if (
            defaultProfile is not null
            && !profiles.ContainsKey(defaultProfile.Id)
            && !routes.ContainsKey(defaultProfile.Id)
        )
        {
            throw new InvalidOperationException("The default profile is not configured.");
        }

        if (clients.Count > 0 && profiles.Count > 0 || client is not null && routes.Count > 0)
        {
            throw new InvalidOperationException(
                "Named-client profiles and the single-client profile overload cannot be mixed."
            );
        }

        var configuredRoutes = routes.Values.ToArray();
        var configuredClients = new Dictionary<string, ClientRegistration>(
            clients,
            StringComparer.Ordinal
        );
        var configuredBudget = budget;
        if (configuredRoutes.Length > 0)
        {
            var errors = ProfileRouter.ValidateConfiguration(
                configuredRoutes,
                configuredClients.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.FixedModel,
                    StringComparer.Ordinal
                ),
                configuredBudget
            );
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    "Invalid command routes: " + string.Join(", ", errors)
                );
            }
        }
        else if (budget != new ExecutionBudget())
        {
            throw new InvalidOperationException("UseBudget requires named-client profiles.");
        }

        if (
            services.Any(d =>
                d.ServiceType == typeof(CommandRuntime) || d.ServiceType == typeof(Func<Actor>)
            )
        )
        {
            throw new InvalidOperationException(
                "The command runtime or actor accessor is already registered."
            );
        }
        // Freeze host configuration before any client factory runs. Catalog
        // validation can then reject missing routes at setup without opening a
        // connection or leaving a command that can never succeed in the host.
        var configuredProfiles = new Dictionary<string, Profile>(profiles, StringComparer.Ordinal);
        var selectedDefault = defaultProfile;
        var clientFactory = client;
        Func<IServiceProvider, object> runtimeFactory = sp =>
        {
            var ledger = sp.GetRequiredService<IHumanFeedbackLedger>();
            var actors = sp.GetRequiredService<Func<Actor>>();
            var events = sp.GetRequiredService<IExecutionEventSink>();
            if (clientFactory is not null)
            {
                // Preserve the established single-client behavior and replay
                // identity. Named routes opt into router retries and ownership.
                return CommandRuntime.Create(
                    () =>
                        clientFactory(sp)
                        ?? throw new InvalidOperationException(
                            "The chat client factory returned null."
                        ),
                    configuredProfiles,
                    ledger,
                    actors,
                    events: events,
                    defaultProfile: selectedDefault
                );
            }

            var registrations = configuredClients.ToDictionary(
                pair => pair.Key,
                pair => new ProviderRegistration(
                    () => pair.Value.Factory(sp),
                    pair.Value.Classify,
                    pair.Value.Owned,
                    pair.Value.FixedModel
                ),
                StringComparer.Ordinal
            );
            var built = ProfileRouter.TryCreate(configuredRoutes, registrations, configuredBudget);
            if (built is not RouterBuildResult.Ready ready)
            {
                throw new InvalidOperationException(
                    "The configured client pipelines could not be created."
                );
            }

            try
            {
                return CommandRuntime.Create(
                    ready.Router,
                    ledger,
                    actors,
                    ownsRouter: true,
                    events: events,
                    defaultProfile: selectedDefault
                );
            }
            catch
            {
                ready.Router.Dispose();
                throw;
            }
        };
        var profileNames = profiles.Keys.Concat(routes.Keys).ToFrozenSet(StringComparer.Ordinal);
        var profileConfiguration = new LlmCmdProfileConfiguration(
            profileNames,
            selectedDefault?.Id ?? (profileNames.Count == 1 ? profileNames.Single() : null),
            runtimeFactory
        );
        services.AddSingleton(profileConfiguration);
        services.TryAddSingleton<IHumanFeedbackLedger, InMemoryFeedbackLedger>();
        services.TryAddSingleton<ICompositionStore, InMemoryCompositionStore>();
        // A default sink prevents the catalog's runtime-forwarding sink from creating a DI cycle.
        services.TryAddSingleton<IExecutionEventSink>(_ => new NullEvents());
        services.AddSingleton<Func<Actor>>(sp => () => actorFactory(sp));
        services.Add(ServiceDescriptor.Singleton(typeof(CommandRuntime), runtimeFactory));
        services.AddCompositionRuntime();
    }

    private sealed class NullEvents : IExecutionEventSink
    {
        public ValueTask EmitAsync(LlmEvent record, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}

/// <summary>Registers the command runtime and composition services.</summary>
public static class LlmCmdServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared command runtime, actor accessor and replaceable stores.
    /// Configuration is validated before client factories can open connections.
    /// </summary>
    public static IServiceCollection AddLlmCmd(
        this IServiceCollection services,
        Action<LlmCmdBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new LlmCmdBuilder();
        configure(builder);
        builder.Register(services);
        return services;
    }
}

// Immutable setup data lets catalog validation run without invoking provider factories.
internal sealed record LlmCmdProfileConfiguration(
    FrozenSet<string> Profiles,
    string? DefaultProfile,
    Func<IServiceProvider, object> RuntimeFactory
)
{
    internal void Validate(IReadOnlyList<CommandRegistration> commands)
    {
        var invalid = commands
            .Where(c =>
                !Profiles.Contains(
                    string.IsNullOrEmpty(c.DefaultProfile) ? DefaultProfile ?? "" : c.DefaultProfile
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
}
