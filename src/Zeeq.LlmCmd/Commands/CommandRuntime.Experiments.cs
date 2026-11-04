using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeeq.LlmCmd;

/// <summary>
/// Separates a hook failure from an invalid release, so fallback cannot hide the
/// exception and spend on a call the author intended to reject.
/// </summary>
internal sealed class ValidationHookException : Exception { }

// A composition freezes these values at registration, before async source resolution.
internal sealed record FrozenExperimentInput<T>(
    T Input,
    bool SafeSnapshot,
    string Prompt,
    string System,
    IReadOnlyDictionary<string, string?> Scalars,
    IReadOnlyList<ParameterRecord> Parameters,
    string? Invalid
);

internal sealed record PreparedExperiment<T>(
    T Input,
    string Prompt,
    string System,
    IReadOnlyList<ParameterRecord> Parameters,
    PreparedCommandSettings Settings,
    ResolvedRoute? Route,
    ExperimentEvidence Evidence,
    ExperimentResolution Resolution,
    string Fingerprint,
    string? Invalid
);

public sealed partial class CommandRuntime
{
    internal FrozenExperimentInput<TP> FreezeExperimentInput<TP, TR>(
        PromptDescriptor<TP, TR> descriptor,
        TP input
    )
    {
        var binding =
            descriptor.RuntimeBinding
            ?? throw new InvalidOperationException("Runtime binding unavailable");
        var safe = binding.TrySnapshot(input, out var frozen);
        var current = safe ? frozen : input;
        var invalid = ValidateExperimentInput(descriptor, current);
        return new(
            current,
            safe,
            descriptor.Render(current),
            descriptor.RenderSystem(current),
            new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(binding.Freeze(current), StringComparer.Ordinal)
            ),
            Array.AsReadOnly(
                descriptor
                    .Project(current)
                    .Select(p => p with { Value = p.Value.Clone() })
                    .ToArray()
            ),
            invalid
        );
    }

    internal ConfiguredDefaultView GetConfiguredExperimentDefault<TP, TR>(
        PromptDescriptor<TP, TR> descriptor
    )
    {
        var binding =
            descriptor.RuntimeBinding
            ?? throw new InvalidOperationException("Runtime binding unavailable");
        var initial = new ConfiguredDefaultView(
            descriptor.Id,
            binding.ContractSignature,
            descriptor.SystemTemplate,
            descriptor.UserTemplate,
            new Dictionary<string, JsonElement>(),
            null,
            descriptor.Revision
        );
        var configured = Experiments?.ConfigureDefault?.Invoke(initial) ?? initial;
        if (
            configured.CommandId != descriptor.Id
            || configured.CommandRevision != descriptor.Revision
            || configured.ContractSignature != binding.ContractSignature
            || configured.UserTemplate is null
        )
        {
            throw new InvalidOperationException("Configured default contract invalid");
        }
        return configured with
        {
            ParameterValues = new ReadOnlyDictionary<string, JsonElement>(
                configured.ParameterValues.ToDictionary(
                    p => p.Key,
                    p => p.Value.Clone(),
                    StringComparer.Ordinal
                )
            ),
        };
    }

    internal async ValueTask<PreparedExperiment<TP>> PrepareExperimentAsync<TP, TR>(
        PromptDescriptor<TP, TR> descriptor,
        FrozenExperimentInput<TP> frozen,
        string profile,
        float? temperature,
        bool explicitProfile,
        CommandExecutionContext context,
        Actor actor,
        ExperimentResolution? pinned,
        CancellationToken ct,
        PreparedCommandSettings? frozenSettings = null,
        string? shadowGroup = null
    )
    {
        var options =
            Experiments ?? throw new InvalidOperationException("Experiments are not configured");
        var binding =
            descriptor.RuntimeBinding
            ?? throw new InvalidOperationException("Runtime binding unavailable");
        var configured =
            pinned is ExperimentResolution.ConfiguredDefault pinnedDefault ? pinnedDefault.View
            : pinned is null ? GetConfiguredExperimentDefault(descriptor)
            : null;
        if (!ExperimentLookup.TryCreate(actor.Tenant, null, out var lookup))
        {
            throw new InvalidOperationException("Invalid authorized partition");
        }
        var resolution =
            pinned
            ?? await options
                .Resolver.ResolveAsync(
                    lookup!,
                    configured!,
                    context.Operation,
                    actor.Subject,
                    options.AssignmentUnit?.Invoke(actor),
                    ct
                )
                .ConfigureAwait(false);
        var prepared = TryPrepare(resolution);
        if (prepared is not null)
        {
            return prepared;
        }
        // A pinned view cannot change after other parts of a composition may have run.
        if (pinned is not null)
        {
            throw new InvalidOperationException("Pinned experiment invalid");
        }
        // Invalid releases fall back as a complete local view before a provider send.
        resolution = new ExperimentResolution.ConfiguredDefault(
            configured!,
            ExperimentFallbackReason.InvalidRelease
        );
        return TryPrepare(resolution)
            ?? throw new InvalidOperationException("Configured default invalid");

        PreparedExperiment<TP>? TryPrepare(ExperimentResolution selected)
        {
            try
            {
                var assignment = (selected as ExperimentResolution.Treatment)?.Assignment;
                if (
                    assignment is not null
                    && (
                        assignment.View.CommandId != descriptor.Id
                        || assignment.View.CommandRevision != descriptor.Revision
                        || assignment.View.ContractSignature != binding.ContractSignature
                    )
                )
                {
                    return null;
                }
                if (pinned is null && assignment?.Role == ExperimentRole.Shadow)
                {
                    return null;
                }
                if (
                    shadowGroup is not null
                    && assignment is not null
                    && (
                        assignment.Role != ExperimentRole.Shadow
                        || assignment.ComparisonGroupId != shadowGroup
                    )
                )
                {
                    return null;
                }
                var variant = assignment?.Variant;
                var fallback = selected as ExperimentResolution.ConfiguredDefault;
                if (
                    fallback is not null
                    && (
                        fallback.View.CommandId != descriptor.Id
                        || fallback.View.CommandRevision != descriptor.Revision
                        || fallback.View.ContractSignature != binding.ContractSignature
                    )
                )
                {
                    return null;
                }
                var overrides = variant?.ParameterValues ?? fallback!.View.ParameterValues;
                var input = frozen.Input;
                if (
                    overrides.Count > 0
                    && (
                        !frozen.SafeSnapshot
                        || !binding.TryApplyOverrides(input, overrides, out input, out _)
                    )
                )
                {
                    return null;
                }
                var invalid =
                    overrides.Count > 0
                        ? ValidateExperimentInput(descriptor, input)
                        : frozen.Invalid;
                if (invalid is not null && assignment is not null)
                {
                    return null;
                }
                var scalars = overrides.Count > 0 ? binding.Freeze(input) : frozen.Scalars;
                var records =
                    overrides.Count > 0
                        ? descriptor
                            .Project(input)
                            .Select(p => p with { Value = p.Value.Clone() })
                            .ToArray()
                        : frozen.Parameters;
                var userTemplate = variant?.Prompt.User switch
                {
                    TemplateChange.Replaced replace => replace.Text,
                    TemplateChange.Removed => null,
                    _ => fallback?.View.UserTemplate ?? descriptor.UserTemplate,
                };
                var systemTemplate = variant?.Prompt.System switch
                {
                    TemplateChange.Replaced replace => replace.Text,
                    TemplateChange.Removed => "",
                    _ => fallback is null
                        ? descriptor.SystemTemplate
                        : fallback.View.SystemTemplate,
                };
                if (userTemplate is null)
                {
                    return null;
                }
                var user = RuntimeTemplate.Render(userTemplate, scalars);
                var system = RuntimeTemplate.Render(systemTemplate ?? "", scalars);
                var settings = frozenSettings ?? FreezeSettings(descriptor, profile, temperature);
                if (settings.Error is not null)
                {
                    return null;
                }
                var routeDefinition = variant?.Route ?? fallback?.View.Route;
                ResolvedRoute? route = null;
                if (!explicitProfile && routeDefinition is not null)
                {
                    if (
                        router is null
                        || options.RoutePolicy is null
                        || router.TryResolveRoute(
                            routeDefinition,
                            candidate => options.RoutePolicy(actor, EffectivePolicyRoute(candidate))
                        )
                            is not DynamicRouteResult.Ready ready
                    )
                    {
                        return null;
                    }
                    route = ready.Route;
                    settings = settings with
                    {
                        Profile = route.Profile.Key.Id,
                        Model = route.Profile.Model,
                        Temperature =
                            temperature
                            ?? route.Profile.Temperature
                            ?? route.Profile.Defaults?.Temperature
                            ?? settings.DefaultTemperature,
                        RouteFingerprint = route.Fingerprint,
                    };
                }
                // A call-site temperature also applies to configured fallbacks. When a host
                // supplied policy, it must validate those effective values before any send.
                if (
                    route is null
                    && temperature is not null
                    && router is not null
                    && options.RoutePolicy is not null
                )
                {
                    var configuredRoutes = router.Profiles.ToDictionary(
                        item => item.Key.Id,
                        StringComparer.Ordinal
                    );
                    if (!configuredRoutes.TryGetValue(settings.Profile, out var candidate))
                    {
                        return null;
                    }
                    while (true)
                    {
                        if (!options.RoutePolicy(actor, EffectivePolicyRoute(candidate)))
                        {
                            return null;
                        }
                        if (candidate.Fallback is null)
                        {
                            break;
                        }
                        if (!configuredRoutes.TryGetValue(candidate.Fallback.Id, out candidate))
                        {
                            return null;
                        }
                    }
                }
                ExecutionProfile EffectivePolicyRoute(ExecutionProfile candidate)
                {
                    var effectiveTemperature =
                        temperature
                        ?? candidate.Temperature
                        ?? candidate.Defaults?.Temperature
                        ?? settings.DefaultTemperature;
                    return candidate with
                    {
                        Temperature = effectiveTemperature,
                        Defaults = (candidate.Defaults ?? new ProfileDefaults()) with
                        {
                            Temperature = effectiveTemperature,
                        },
                    };
                }
                var configuredRoute =
                    route?.Profile
                    ?? router?.Profiles.FirstOrDefault(p => p.Key.Id == settings.Profile);
                var effectiveDefaults = configuredRoute?.Defaults ?? new ProfileDefaults();
                effectiveDefaults = effectiveDefaults with { Temperature = settings.Temperature };
                var evidence = new ExperimentEvidence(
                    assignment is null ? "default" : "experiment",
                    assignment?.View.Revision.Value,
                    assignment?.ExperimentId,
                    assignment?.VariantId,
                    fallback?.Reason.ToString(),
                    shadowGroup is not null
                        ? ExperimentRole.Shadow
                        : assignment?.Role ?? ExperimentRole.Primary,
                    shadowGroup ?? assignment?.ComparisonGroupId,
                    systemTemplate,
                    userTemplate,
                    system,
                    user,
                    records
                        .Select(p => new ExperimentParameter(
                            p.Name,
                            p.TypeId,
                            p.Value.Clone(),
                            overrides.ContainsKey(p.Name)
                                ? (assignment is null ? "default" : "override")
                                : "caller",
                            p.JsonName
                        ))
                        .ToArray(),
                    configuredRoute?.Provider,
                    settings.Model,
                    effectiveDefaults,
                    CommandId: descriptor.Id,
                    CommandRevision: descriptor.Revision,
                    DefinitionHash: descriptor.DefinitionHash
                );
                return new(
                    input,
                    user,
                    system,
                    records,
                    settings,
                    route,
                    evidence,
                    selected,
                    ExperimentFingerprint(
                        descriptor.Id,
                        descriptor.Revision,
                        descriptor.DefinitionHash,
                        user,
                        system,
                        records,
                        settings,
                        evidence
                    ),
                    invalid
                );
            }
            catch (ValidationHookException)
            {
                throw;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return null;
            }
        }
    }

    private static string? ValidateExperimentInput<TP, TR>(
        PromptDescriptor<TP, TR> descriptor,
        TP input
    )
    {
        try
        {
            return descriptor.ValidateInput(input);
        }
        catch (Exception)
        {
            throw new ValidationHookException();
        }
    }

    static string ExperimentFingerprint(
        string id,
        int revision,
        string hash,
        string prompt,
        string system,
        IReadOnlyList<ParameterRecord> parameters,
        PreparedCommandSettings settings,
        ExperimentEvidence evidence
    )
    {
        var buffer = new StringBuilder();
        void Add(string? value)
        {
            value ??= "";
            buffer.Append(value.Length).Append(':').Append(value);
        }
        Add(id);
        Add(revision.ToString(CultureInfo.InvariantCulture));
        Add(hash);
        Add(prompt);
        Add(system);
        Add(settings.Profile);
        Add(settings.Model);
        Add(settings.Temperature?.ToString("R", CultureInfo.InvariantCulture));
        Add(settings.DefaultTemperature?.ToString("R", CultureInfo.InvariantCulture));
        Add(settings.RouteFingerprint);
        Add(evidence.Source);
        Add(evidence.Revision);
        Add(evidence.ExperimentId);
        Add(evidence.VariantId);
        Add(evidence.Reason);
        Add(evidence.Role.ToString());
        Add(evidence.ComparisonGroupId);
        Add(evidence.SystemTemplate);
        Add(evidence.UserTemplate);
        foreach (var item in evidence.Parameters ?? [])
        {
            Add(item.Name);
            Add(item.Origin);
        }
        foreach (var item in parameters)
        {
            Add(item.Name);
            Add(item.TypeId);
            Add(item.JsonName);
            Add(item.Value.GetRawText());
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(buffer.ToString())));
    }
}
