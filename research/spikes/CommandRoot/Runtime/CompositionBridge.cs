using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;
using HumanGrading;
using LlmCmd.ContextPropagation;

namespace CommandRoot;

internal sealed record PreparedCommandInput(string? Prompt, string? System, IReadOnlyList<ParameterRecord> Parameters,
    string? InvalidReason = null, string? HookPhase = null, PreparedCommandSettings? Settings = null);
internal sealed record PreparedCommandSettings(string Profile, string? Model, float? Temperature,
    float? DefaultTemperature, string RouteFingerprint, string? Error = null);
internal sealed record InvocationIdentity(string CommandId, int Revision, string DefinitionHash, string Profile,
    string Fingerprint, IReadOnlyList<ParameterRecord> Parameters);

public sealed partial class Invocation<TP, TR>
{
    readonly PreparedCommandInput? prepared;
    private Invocation(CommandRuntime runtime, PromptDescriptor<TP, TR> descriptor, TP parameters,
        InvocationState state, PreparedCommandInput? prepared) : this(runtime, descriptor, parameters, state) => this.prepared = prepared;

    internal CommandRuntime RuntimeOwner => runtime;
    internal JsonTypeInfo<TR> ResultContract => descriptor.Contract?.Result ?? descriptor.ResultType
        ?? throw new InvalidOperationException("Result contract missing");
    internal string? ValidateStoredResult(TR value) => descriptor.ValidateResult(value);

    // Freeze the observable call inputs, not just a record reference. Records may
    // contain mutable collections. Serializer roundtripping is not a safe clone:
    // WhenWritingNull can turn explicit null into an optional constructor default.
    internal Invocation<TP, TR> FreezeForComposition()
    {
        if(prepared is not null) return this;
        var records=Array.Empty<ParameterRecord>(); string? prompt=null, system=null, invalid;
        try { invalid=descriptor.ValidateInput(parameters); }
        catch { return new(runtime,descriptor,parameters,state,new(null,null,records,HookPhase:"ValidateInput")); }
        if(invalid is not null) return new(runtime,descriptor,parameters,state,new(null,null,records,invalid));
        try { prompt=descriptor.Render(parameters); system=descriptor.RenderSystem(parameters); }
        catch { return new(runtime,descriptor,parameters,state,new(prompt,system,records,HookPhase:"Render")); }
        try { records=descriptor.Project(parameters).Select(p=>p with { Value=p.Value.Clone() }).ToArray(); }
        catch { return new(runtime,descriptor,parameters,state,new(prompt,system,records,HookPhase:"Project")); }
        return new(runtime,descriptor,parameters,state,new(prompt,system,Array.AsReadOnly(records),
            Settings:runtime.FreezeSettings(descriptor,state.Profile,state.Temperature)));
    }

    internal InvocationIdentity GetCompositionIdentity()
    {
        var frozen=prepared ?? throw new InvalidOperationException("Freeze the invocation before capturing its identity");
        var b=new StringBuilder();
        void Part(string? value) { value??=""; b.Append(value.Length).Append(':').Append(value); }
        Part(descriptor.Id); Part(descriptor.Revision.ToString(CultureInfo.InvariantCulture)); Part(descriptor.DefinitionHash); Part(state.Profile);
        Part(state.Temperature?.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        if(frozen.Settings is { } settings)
        {
            Part(settings.Profile); Part(settings.Model); Part(settings.Temperature?.ToString("R",CultureInfo.InvariantCulture));
            Part(settings.DefaultTemperature?.ToString("R",CultureInfo.InvariantCulture)); Part(settings.RouteFingerprint); Part(settings.Error);
        }
        Part(frozen.Prompt); Part(frozen.System); Part(frozen.InvalidReason); Part(frozen.HookPhase); Part(state.Error);
        foreach(var p in frozen.Parameters) { Part(p.Name); Part(p.JsonName); Part(p.TypeId); Part(p.Value.GetRawText()); }
        return new(descriptor.Id,descriptor.Revision,descriptor.DefinitionHash,state.Profile,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString()))),frozen.Parameters);
    }

    internal Task<Outcome<TR>> ExecuteComponentAsync(OperationContext operation, Guid executionId, string slotId,
        Guid stepId, CallSite caller, CancellationToken ct) => ExecuteComponentAsync(operation,executionId,slotId,stepId,caller,null,ct);

    internal Task<Outcome<TR>> ExecuteComponentAsync(OperationContext operation, Guid executionId, string slotId,
        Guid stepId, CallSite caller, IExecutionJournal? journal, CancellationToken ct)
    {
        var frozen=FreezeForComposition();
        var conflict=state.Suppressed || state.Context is { } explicitContext &&
            (explicitContext.Reference.RunId!=operation.Reference.RunId
            || explicitContext.Reference.Draft is not null && explicitContext.Reference.Draft!=operation.Reference.Draft
            || explicitContext.Reference.TrialId is not null && explicitContext.Reference.TrialId!=operation.Reference.TrialId
            || explicitContext.WorkId is not null && explicitContext.WorkId!=operation.WorkId);
        // The slot supplies one whole context. Compatible explicit constraints
        // are checked, never merged with a different ambient run or draft.
        var captured=LlmCmdContext.Capture(operation);
        var context=new CommandExecutionContext(executionId,descriptor.Id,descriptor.Revision,descriptor.DefinitionHash,captured,slotId,stepId);
        return runtime.ExecuteAsync(descriptor,parameters,state.Profile,state.Temperature,context,
            state.Error ?? (conflict ? "composition_context_conflict" : null),caller,ct,journal,frozen.prepared);
    }
}

public sealed partial class CommandRuntime
{
    // Freeze the effective configuration too. Keeping only a profile name would
    // silently replay old-model parts after a rollout remapped that name. Hooks
    // run once; their captured values, rather than their code, enter the identity.
    internal PreparedCommandSettings FreezeSettings<TP,TR>(PromptDescriptor<TP,TR> descriptor,string requestedProfile,float? temperature)
    {
        var profile=string.IsNullOrEmpty(requestedProfile) ? defaultProfile ?? "" : requestedProfile;
        if(!profiles.TryGetValue(profile,out var resolved)) return new(profile,null,null,null,"","profile_invalid");
        var defaults=new ChatSettingsPatch();
        try { descriptor.ConfigureDefaults?.Invoke(defaults); }
        catch { return new(profile,resolved.Model,null,null,"","defaults_hook_failed"); }
        if(defaults.Temperature is float value && !float.IsFinite(value))
            return new(profile,resolved.Model,null,defaults.Temperature,"","default_temperature_invalid");
        var route=router?.GetRouteFingerprint(ProfileRouting.ProfileKey.Declare(profile)) ?? "single-provider";
        return new(profile,resolved.Model,temperature ?? resolved.Temperature ?? defaults.Temperature,defaults.Temperature,route);
    }

    internal IHumanFeedbackLedger CompositionLedger => ledger;
    internal IExecutionEventSink? CompositionEvents => events;
    internal Actor ResolveCompositionActor() => actorProvider();
    internal bool IsDisposed => disposed;
}
