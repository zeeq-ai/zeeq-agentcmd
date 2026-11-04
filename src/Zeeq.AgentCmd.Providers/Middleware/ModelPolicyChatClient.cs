using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Zeeq.AgentCmd.Providers;

/// <summary>A local, nontransient policy violation. Retry classifiers must not send the same invalid request again.</summary>
public sealed class ModelPolicyViolationException : InvalidOperationException
{
    /// <summary>Copies bounded field names without exposing option values or request content.</summary>
    public ModelPolicyViolationException(IEnumerable<string> violations)
        : base("The request violates the selected model policy.")
    {
        ArgumentNullException.ThrowIfNull(violations);
        Violations = Array.AsReadOnly(violations.ToArray());
    }

    /// <summary>Rejected field names or model-resolution codes.</summary>
    public IReadOnlyList<string> Violations { get; }
}

/// <summary>
/// Opt-in validation of standard MEAI options. Place inside retry/fallback and
/// outside extraction so a rejected request does not create a provider attempt.
/// Opaque SDK option factories remain the host's responsibility.
/// </summary>
public sealed class ModelPolicyChatClient : DelegatingChatClient
{
    readonly ProviderIdentity registration;
    readonly ModelRequestCatalog catalog;
    readonly IReadOnlyDictionary<string, ProviderModelIdentity>? bindings;
    readonly bool allowUnknownModels;

    /// <summary>
    /// Freezes target-to-model bindings. Azure requires an explicit deployment
    /// map; its deployment name is not evidence of the underlying model.
    /// </summary>
    public ModelPolicyChatClient(
        IChatClient inner,
        ProviderIdentity registration,
        ModelRequestCatalog? catalog = null,
        IEnumerable<ModelTargetBinding>? modelBindings = null,
        bool allowUnknownModels = false
    )
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (
            string.Equals(
                registration.ProviderKey,
                "azureopenai",
                StringComparison.OrdinalIgnoreCase
            ) && modelBindings is null
        )
        {
            throw new ArgumentException(
                "Azure requires explicit deployment-to-model bindings.",
                nameof(modelBindings)
            );
        }

        this.registration = registration;
        this.catalog = catalog ?? ModelRequestCatalog.Default;
        if (modelBindings is not null)
        {
            var copy = new Dictionary<string, ProviderModelIdentity>(StringComparer.Ordinal);
            foreach (var binding in modelBindings)
            {
                if (
                    binding is null
                    || string.IsNullOrWhiteSpace(binding.Target)
                    || binding.Model is null
                    || !string.Equals(
                        binding.Model.ProviderKey,
                        registration.ProviderKey,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || !copy.TryAdd(binding.Target, binding.Model)
                )
                {
                    throw new ArgumentException(
                        "Invalid or duplicate model target binding.",
                        nameof(modelBindings)
                    );
                }
            }
            bindings = new System.Collections.ObjectModel.ReadOnlyDictionary<
                string,
                ProviderModelIdentity
            >(copy);
        }
        this.allowUnknownModels = allowUnknownModels;
    }

    void Validate(ChatOptions? options)
    {
        var target =
            options?.ModelId ?? InnerClient.GetService<ChatClientMetadata>()?.DefaultModelId;
        string? model = target;
        if (bindings is not null)
        {
            model =
                target is not null && bindings.TryGetValue(target, out var bound)
                    ? bound.ModelId
                    : null;
        }

        var profile = model is null
            ? null
            : catalog.Find(registration.ProviderKey, registration.Api, model);
        if (profile is null)
        {
            if (allowUnknownModels)
            {
                return;
            }

            throw new ModelPolicyViolationException(["unknown_model_or_binding"]);
        }

        var violations = profile.Validate(options);
        if (violations.Count != 0)
        {
            throw new ModelPolicyViolationException(violations);
        }
    }

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Validate(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        Validate(options);
        await foreach (
            var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }
}

/// <summary>Optional model validation in an ordinary MEAI pipeline.</summary>
public static class ModelPolicyBuilderExtensions
{
    /// <summary>Adds validation outside provider extraction; host retry must classify policy violations as nontransient.</summary>
    public static ChatClientBuilder UseModelPolicy(
        this ChatClientBuilder builder,
        ProviderIdentity registration,
        ModelRequestCatalog? catalog = null,
        IEnumerable<ModelTargetBinding>? modelBindings = null,
        bool allowUnknownModels = false
    ) =>
        builder.Use(inner => new ModelPolicyChatClient(
            inner,
            registration,
            catalog,
            modelBindings,
            allowUnknownModels
        ));
}
