# Client registration analysis

Keep the existing string-based setup. The core package includes a Roslyn analyzer
in its generator assembly, so consumers need no extra package, generic builder or
registration attributes. Diagnostics are errors by default.

| Diagnostic | Meaning | Location |
| --- | --- | --- |
| `AGENTCLIENT001` | A named profile refers to an unregistered client. The message lists the registered IDs. | Profile client argument |
| `AGENTCLIENT002` | A client ID is registered twice in one setup. | Repeated registration name |
| `AGENTCLIENT003` | A constant profile model differs from its client's constant `fixedModel`. | Profile model argument |

IDs are case-sensitive, matching runtime routing. Literals, `const` fields,
constant concatenation and `nameof` use Roslyn's evaluated constant value.
Argument names and overload symbols determine meaning; source argument order
does not. The single-client `AddProfile(name, model, temperature)` overload is
not a named-client reference.

## Scope

Each direct `AddAgentCmd` lambda is independent. The supported shape is an
expression-bodied chain or a block containing only straight-line builder calls.
Every chain must start at that lambda's builder parameter. All registrations are
collected before checking routes, so a profile can precede its client.

Branches, loops, local declarations, early returns, builder aliases, captures,
and helpers that receive the builder open the setup. An unknown registration
name can satisfy any reference, so it also opens the setup. These cases produce
no speculative registration diagnostics. Runtime validation remains active.

A dynamic profile client skips only that route. A dynamic model or fixed-model
binding skips only the model comparison; known client-name checks still work.
Argument helpers that do not receive or capture the builder do not introduce
registrations. This analysis describes the declared topology assuming argument
evaluation completes; it does not prove that arbitrary configuration getters,
callbacks or factories return successfully. Unsupported expression shapes may
cause conservative skipping.

The analyzer does not resolve DI services, construct clients, contact providers,
read configuration or inspect secrets. Runtime validation checks dynamic
registrations, defaults, fallback chains and model bindings. Provider middleware
still owns model-specific request constraints. A declared fixed-model string
does not establish the model behind an Azure deployment.

## Verification and maintenance

```sh
mise run test:analyzers  # Offline semantic tests, including actual library metadata.
mise run test:package   # NuGet-only consumer: valid setup plus all three errors.
mise run verify         # Full offline gate; no paid APIs or container startup.
```

Resolve library symbols once per compilation, then pre-filter invocation names
before semantic analysis. Keep state local to one callback and skip generated
code. Extend the supported signatures deliberately when adding builder APIs;
runtime validation remains the final authority.
