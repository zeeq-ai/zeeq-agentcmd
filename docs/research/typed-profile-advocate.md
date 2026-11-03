# Typed profile references: customer advocate review

Reviewed 2026-11-01. Design review only. All new API syntax below is proposed. No runtime or generator changes, builds, or implementation claims accompany this review.

## Recommendation

Prefer **generated `ProfileKey` values** and one `WithProfile(ProfileKey)` method. Declare a static partial profile property and use a typed `DefaultProfile` member on the command. Both value keys and marker types are sound; I see no correctness blocker to either.

The deciding benefit is ordinary application code: a profile can be a field, dictionary key, method argument, or the result of a conditional choice. The same method accepts a generated reference and a validated externally selected key. A generic marker call is pleasant for a fixed choice but needs another path for a value chosen at runtime.

Keeping the default inside the command attribute is a valid marker-type advantage, but it does not outweigh ordinary value semantics for this user's command-centered, minimal-setup preference. Do not ship parallel marker and value representations for each profile.

## Minimal proposed declaration, setup, and call

Declare the identity once. The generator supplies the getter and stable ID metadata, not environment-specific model settings.

```csharp
public static partial class Profiles
{
    [ModelProfile("extraction")]
    public static partial ProfileKey Extraction { get; }
}
```

An existing command selects its default with a typed member:

```csharp
[PromptCommand("invoice.extract", Revision = 1)]
public sealed partial class ExtractInvoice
    : PromptCommand<InvoiceParameters, Invoice>
{
    static ProfileKey DefaultProfile => Profiles.Extraction;
    // Existing templates and validation members.
}
```

The generator checks this member's symbol/signature and emits a reference to it. Evaluate it once when initializing the descriptor, not during generation or on every execution. Its normal body is a direct profile reference. Do not use this getter to read Redis or perform routing. Runtime configuration supplies those changing values separately. Having both this member and a legacy string default should produce a conflict diagnostic.

Host configuration can retain the current dictionary style, with a typed key. This is a proposed change to the current string-keyed signature:

```csharp
var profiles = new Dictionary<ProfileKey, Profile>
{
    [Profiles.Extraction] = new(Model: "invoice-model", Temperature: 0.2f)
};
using var runtime = CommandRuntime.Create(
    clientFactory, profiles, ledger, actorProvider);
var extract = new ExtractInvoice(runtime);
```

A future DI helper can configure named options under the same stable ID. The host need not type `"extraction"` again. `clientFactory` still configures the transport separately; `"invoice-model"` is an illustrative deployment name.

The common call uses the declared default:

```csharp
Outcome<Invoice> result = await extract
    .WithParameters(new(document))
    .InRun(runId)
    .ExecuteAsync(ct);
```

An intentional override is `.WithProfile(Profiles.Drafting)` when that profile is also declared and configured. A helper can instead accept a `ProfileKey selectedProfile` and call `.WithProfile(selectedProfile)`. Do not repeat `.WithProfile(Profiles.Extraction)` in every example when it is already the default.

## Three developer workflows

**Use an existing profile.** Type `Profiles.` and select a property. The command default and call override use the same expression. XML documentation should show purpose and stable ID. Settings remain discoverable in host configuration, not hidden in an empty type. No generic method argument or marker instance is needed.

**Define/configure one profile.** Add one attributed property, then one host settings entry. These name the identity and supply its environment-specific values; they are not duplicate model declarations. Use logical names such as `Extraction` or `Drafting`, not a provider's current model version. A typo in a property reference is a compiler error. A missing configuration entry is still a runtime/startup concern.

**Contribute from a package/plugin.** Export a public container such as `InvoiceProfiles` with the compiled profile property and optional generated manifest. Consumers use `InvoiceProfiles.Extraction` without recreating its declaration or reading the package's source. The host controls settings and provider binding. Published stable IDs should be namespaced, for example `vendor.invoices.extraction`. Referencing the same exported key is reuse; conflicting declarations/configuration for one ID need an explicit diagnostic. A manifest can support discovery without becoming another object required at call sites.

## Sound alternative: marker-only profiles

The marker design uses `[ModelProfile("extraction")] public sealed partial class Extraction`, a generic default attribute `[PromptCommand<Profiles.Extraction>(...)]`, and `.WithProfile<Profiles.Drafting>()`. A generated `IModelProfile` contract lets generic constraints reject unrelated types. Its strongest UX benefit is keeping the default in the command attribute. It is a reasonable choice when nearly all selection is static.

The value design instead offers ordinary values, no empty marker classes, and one selection API for static and dynamic choices. Its cost is the separate `DefaultProfile` member. A custom `ProfileKey` cannot be an attribute argument under the [C# attribute rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/attributes); do not propose `Profile = Profiles.Extraction` in the existing attribute. [Partial properties](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/partial-member) support the declaration shape, but generator behavior still needs verification.

Both designs reject ordinary wrong argument types. Neither proves that a configured entry exists. A nominal key can be constructed or parsed outside generation; a profile interface can be implemented manually. Neither is a security boundary. A declaration diagnostic and host registration validation solve different problems.

An enum is convenient inside one closed app, but packages cannot extend another assembly's enum. Marker-plus-token duplication adds two names for one identity. Manifest-first authoring can be useful for an external configuration workflow, but should not require every C# user to learn a second declaration format.

## Required behavior and top concerns

**Preserve the command types.** `WithProfile(ProfileKey)` returns the unchanged `Invocation<TParameters, TResult>`. It must preserve the runtime binding, run context, explicit field patches, and caller-capture behavior. Do not add a profile generic argument to the invocation or erase `TResult` through an untyped base. Every selection creates a new invocation; it does not mutate the injected command root.

**Keep stable identity separate from live configuration.** Rename `Profiles.Extraction` without changing its explicit ID. Serialize that ID, not the CLR member/type name. A published member rename can still break source/binary consumers; persisted identity need not change with it. Equality and lookup should use the stable ID consistently. Invalid/default keys must fail validation; nominal typing alone does not make every value valid.

**Resolve and freeze external defaults.** Appsettings/Redis may change values under an existing key. Resolve one version per execution and freeze it for retries and collection. Record the selected ID, configuration revision, and effective settings. A remote-only profile can be selected through an explicit validated `ProfileKey` parse/lookup boundary and the same `WithProfile` method; it has no generated property until the code/manifest changes. Do not restore implicit string conversions at ordinary call sites.

**Separate profile selection from field precedence.** A runtime-configured command default can select a profile when the caller has not done so. An explicit `WithProfile` wins over that default selection. Explicit fields win over the resolved profile/runtime values regardless of chain order: `WithTemperature(0.1f).WithProfile(...)` keeps 0.1. Changing profiles must not carry old profile defaults into the new profile. Repeated explicit profile selection can be last-call-wins on a new immutable invocation. Named options are a configuration mechanism, not an execution snapshot or a precedence policy; see the [.NET options documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/options).

**Do not confuse a profile with transport or capability.** The current `Profile` holds model/temperature and uses one separately configured provider client. Selecting a profile does not create or select another transport. Future routing needs an explicit provider registration reference. A type named `JsonProfile` cannot prove that a remotely changed model still supports JSON or tools. Validate the command's requirements against the resolved configuration/provider and validate the response. Capability generics add little safety for this first surface.

**Give configuration errors a useful identity.** The current runtime's “Run or profile invalid” message is too vague. Prefer: `Profile 'extraction' is not configured for command 'invoice.extract'. Register or supply this profile in the host.` Use a configuration failure, not invalid invoice input. Distinguish unknown ID, duplicate ID, unavailable source, invalid settings, and unsupported resolved capabilities. Validate defaults of active registered commands at startup where possible; validate dynamic selections and changed remote settings at execution. Do not demand configuration for every unused optional profile a package exports.

Both candidate designs require generator/compiler checks, cross-assembly tests, startup diagnostics, and runtime precedence tests before release. This review did not perform them. The preferred customer workload is one property declaration, one host configuration, one typed default per command, and no profile repetition on ordinary calls.
