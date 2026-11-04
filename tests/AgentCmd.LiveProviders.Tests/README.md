# Explicit live provider capture tests

These TUnit tests call Azure OpenAI or direct OpenAI Chat Completions and Anthropic Messages through real SDK-to-MEAI bridges and `ProviderExtractionChatClient`. The generated-runtime cases also exercise the actual command library. Aspire, Docker and Python are not required for these API tests.

## Set up keys on this project

Run from the repository root:

```sh
dotnet user-secrets set "AzureOpenAI:ApiKey" "YOUR_AZURE_KEY" --project tests/AgentCmd.LiveProviders.Tests
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://YOUR-RESOURCE.openai.azure.com/" --project tests/AgentCmd.LiveProviders.Tests
dotnet user-secrets set "Anthropic:ApiKey" "YOUR_ANTHROPIC_KEY" --project tests/AgentCmd.LiveProviders.Tests
```

The project already declares `UserSecretsId=zeeq-llmcmd-live-tests`. Do not run `init` again. This is a separate store from the placeholder-secret spike. That spike's `Parameters:...` values are not read here. Do not put real keys in chat, tracked configuration or test arguments. User-secrets stay outside the repo but are not encrypted at rest.

To avoid putting the key in shell history or a command argument, `dotnet user-secrets set` also accepts a JSON object on standard input. For example, in zsh:

```sh
read -rs 'provider_key?Azure OpenAI API key: '; print
print -rn -- "$provider_key" | jq -Rs '{"AzureOpenAI:ApiKey": .}' | dotnet user-secrets set --project tests/AgentCmd.LiveProviders.Tests
unset provider_key
```

Use `Anthropic:ApiKey` for Claude. The input form requires `jq`. No running host is needed to save or use these secrets. A direct OpenAI account can instead use `OpenAI:ApiKey`; an Azure credential is not a direct OpenAI credential.

`LiveSettings` loads this assembly's user-secrets, then environment variables. `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_ENDPOINT`, `OPENAI_API_KEY` and `ANTHROPIC_API_KEY` override the corresponding saved settings for CI. Nested environment settings such as `AzureOpenAI__ApiKey` also work. Only SDK construction uses credentials; commands, telemetry and capture records do not receive them. Do not list secret values while diagnosing configuration.

## Run explicitly

Each task selects just one provider and enables the live-test gate:

```sh
mise run test:live:azure
mise run test:live:anthropic
```

The default Luna/Haiku smoke runs make one non-streaming request with a 32-token output limit, reasoning disabled and no SDK retries. A model that requires thinking instead uses its reviewed minimum effort and a 512-token budget. Rerunning a task makes a new paid request. The test classes have `[Explicit]`; `AGENTCMD_RUN_LIVE_TESTS=1` is also required. Ordinary discovery or a broad filter alone cannot start paid calls. `test:live:openai` remains available for a separate direct OpenAI key.

The direct TUnit invocation is:

```sh
AGENTCMD_RUN_LIVE_TESTS=1 dotnet run --project tests/AgentCmd.LiveProviders.Tests -- --treenode-filter '/*/*/AzureOpenAiCaptureTests/*'
AGENTCMD_RUN_LIVE_TESTS=1 dotnet run --project tests/AgentCmd.LiveProviders.Tests -- --treenode-filter '/*/*/AnthropicCaptureTests/*'
```

The defaults are `gpt-6-luna` and `claude-haiku-5-5`. For Azure, `AzureOpenAI:Model` or `AZURE_OPENAI_MODEL` is the deployment target. If that name differs from the underlying model, also set `AzureOpenAI:ModelId` to its reviewed canonical model ID. Direct OpenAI and Claude accept `OpenAI:Model` / `Anthropic:Model` or `OPENAI_MODEL` / `ANTHROPIC_MODEL`. Unknown models fail locally; tests never silently select another model.

## Verify generated command execution

```sh
mise run test:live:runtime:azure
mise run test:live:runtime:anthropic
```

Each task makes one additional request. Luna/Haiku use at most 64 output tokens with thinking disabled; models that require reasoning use `low` and at most 512 tokens. `RuntimeProbe` is a source-generated `PromptCommand<RuntimeProbeInput, RuntimeProbeResult>`. The test checks typed JSON, stored output and ledger readback, matching attempt IDs, accounting evidence and command event context. Offline equivalents check that an event listener fault cannot change a successful outcome. Runtime receipts use `{provider}.runtime.capture.json`.

## Model matrix

```sh
mise run test:live:matrix:azure       # Six configured deployments; at most six paid calls.
mise run test:live:matrix:anthropic   # Haiku and Sonnet 5.5; at most two paid calls.
mise run test:replay:matrix           # Local metadata receipts only; no credentials or calls.
```

The Azure matrix uses the screenshot's exact deployment-to-model mappings: `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-6-astra`, `gpt-6-luna`, `gpt-6-sol`, and `gpt-6.1-sol`. These are explicit test inputs, not a rule for inferring arbitrary deployment names. The matrices are sequential within each platform and disable retries. Astra, GPT-6.1 Sol and Sonnet 5.5 use adaptive/low thinking with a 512-token budget. No sampling values are sent.

Every case runs a generated command, verifies typed JSON and ledger readback, and checks the reported version against the exact model identity. Receipts use `{provider}.{model}.matrix.capture.json`. Replaying them uses their original timestamps and explicit mappings. Billing scope in the test is an assertion, not evidence of the resource's SKU. Missing prices do not fail a successful provider capture; Azure 6.1 remains unpriced. See [model policies](../../docs/architecture/provider-model-policies.md).

## What the tests verify

Each response must reach exactly one extraction sink. Assertions check response and model IDs, normalized input/output counts, native SDK accounting evidence and stop reason, no extraction errors, no usage conflicts, and raw response-text capture disabled. A capture artifact preserves optional dimensions and diagnostics without assuming that a tiny request must exercise caching, reasoning or tools. The live tests do not assert fixed token counts.

Accounting-only JSON is written to the repository's `artifacts/live-providers/{provider}.capture.json`, including timestamps, requested model, bounded billing metadata and request count. This directory is ignored. Set `AGENTCMD_LIVE_EVIDENCE` to choose another directory. Mise writes JUnit to `/tmp/opencode/agentcmd-live-providers`. API keys, request headers, prompt/response text, tool arguments and raw SDK objects are not written to these reports. SDK request failures use exception type, HTTP status and request count without raw response bodies.

## Offline verification and limits

```sh
mise run test:provider-sdk
```

This runs real OpenAI 2.14.0 and Anthropic 13.3.0 SDKs against a test-local HTTP handler. It checks the SDK bridges, native response projectors, extraction, retry suppression, project secret identity and configuration precedence. Its responses are synthetic and explicitly do not establish live API success. No credentials are read by these offline cases.

The projectors are integration-test code. The provider adapter package has no SDK dependency. Projectors select accounting and stop metadata from SDK representations, rather than preserving full HTTP response bytes. Native streaming accounting, OpenAI Responses API, forced cache hits, aggregate composition through live models, invoice reconciliation and hosted collector readback remain separate tests.

[Provider-specific pricing](../../docs/research/provider-pricing.md) uses first-party rate cards and explicit billing scope. Missing counters or incompatible observed billing metadata are not priced as a complete total. The [local Langfuse proof](../AgentCmd.LangfuseReadback/README.md) replays these sanitized receipts, then verifies model, tokens, cost estimate and absence of content through Langfuse's observations API. Replaying a receipt makes no model request.

## Azure OpenAI v1

Set `AzureOpenAI:ApiKey` and `AzureOpenAI:Endpoint` in this test project's user-secrets, or use `AZURE_OPENAI_API_KEY` and `AZURE_OPENAI_ENDPOINT`. The endpoint must be an HTTPS resource origin or its `/openai/v1/` URL. Set `AzureOpenAI:Model` or `AZURE_OPENAI_MODEL` to the Azure deployment name if it differs from `gpt-6-luna`. Flat environment variables take precedence. Azure availability and deployment names depend on the resource.

Run the explicit Azure class with both gates enabled:

```sh
mise run test:live:azure
```

The Azure test uses the OpenAI SDK's v1 Chat Completions endpoint and the same SDK-free wire parser as direct OpenAI.

To check whether settings are configured without showing their values or making a request:

```sh
mise exec -- dotnet run --project tests/AgentCmd.LiveProviders.Tests -- --treenode-filter '/*/*/SettingsPresenceTests/*' --output Detailed
```

The probe reports only booleans. A missing deployment or Anthropic model uses the defaults; an Azure endpoint is required. The model request catalog selects the tested reasoning profile. Sampling fields remain absent. A fixed temperature policy is available for models whose published contract requires one; the validator never silently changes caller settings.
