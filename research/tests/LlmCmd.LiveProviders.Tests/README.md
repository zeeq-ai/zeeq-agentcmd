# Explicit live provider capture tests

These TUnit tests call the OpenAI Chat Completions API and the Anthropic Messages API through their real SDK-to-MEAI bridges and the existing `ProviderExtractionChatClient`. Aspire, Docker and Python are not required.

## Set up keys on this project

Run from the repository root:

```sh
dotnet user-secrets set "OpenAI:ApiKey" "YOUR_OPENAI_KEY" --project tests/LlmCmd.LiveProviders.Tests
dotnet user-secrets set "Anthropic:ApiKey" "YOUR_ANTHROPIC_KEY" --project tests/LlmCmd.LiveProviders.Tests
```

The project already declares `UserSecretsId=zeeq-llmcmd-live-tests`. Do not run `init` again. This is a separate store from the placeholder-secret spike. That spike's `Parameters:...` values are not read here. Do not put real keys in chat, tracked configuration or test arguments. User-secrets stay outside the repo but are not encrypted at rest.

To avoid putting the key in shell history or a command argument, `dotnet user-secrets set` also accepts a JSON object on standard input. For example, in zsh:

```sh
read -rs 'provider_key?OpenAI API key: '; print
print -rn -- "$provider_key" | jq -Rs '{"OpenAI:ApiKey": .}' | dotnet user-secrets set --project tests/LlmCmd.LiveProviders.Tests
unset provider_key
```

Use `Anthropic:ApiKey` for Claude. The input form requires `jq`. No running host is needed to save or read these secrets.

`LiveSettings` explicitly loads this assembly's user-secrets, then environment variables. `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` override the corresponding saved key for CI. Nested `OpenAI__ApiKey` / `Anthropic__ApiKey` also work through .NET configuration. The keys remain in the client fixture; neither commands nor capture records receive them.

## Run explicitly

Each task selects just one provider and enables the live-test gate:

```sh
mise run test:live:openai
mise run test:live:anthropic
```

Each run makes one short, non-streaming request with a 32-token output limit and no SDK retries. Rerunning a task makes a new paid request. The test classes have `[Explicit]`; `LLMCMD_RUN_LIVE_TESTS=1` is also required. Ordinary discovery or a broad filter alone cannot start paid calls.

The direct TUnit invocation is:

```sh
LLMCMD_RUN_LIVE_TESTS=1 dotnet run --project tests/LlmCmd.LiveProviders.Tests -- --treenode-filter '/*/*/OpenAiCaptureTests/*'
LLMCMD_RUN_LIVE_TESTS=1 dotnet run --project tests/LlmCmd.LiveProviders.Tests -- --treenode-filter '/*/*/AnthropicCaptureTests/*'
```

The default models are `gpt-4.1-mini` and `claude-haiku-4-5-20251001`. Choose models available to your account with `OpenAI:Model` / `Anthropic:Model` in user-secrets, or `OPENAI_MODEL` / `ANTHROPIC_MODEL` in the environment. The defaults exercise the existing Chat Completions and Messages adapters; they are not a model-selection recommendation. Output limits and prompt quality are not the subject of this smoke test.

## What the tests verify

Each response must reach exactly one extraction sink. Assertions check response and model IDs, normalized input/output counts, native SDK accounting evidence and stop reason, no extraction errors, no usage conflicts, and raw response-text capture disabled. A capture artifact preserves optional dimensions and diagnostics without assuming that a tiny request must exercise caching, reasoning or tools. The live tests do not assert fixed token counts.

Accounting-only JSON is written to `artifacts/live-providers/{provider}.capture.json`, including timestamp and requested model. This directory is ignored. Set `LLMCMD_LIVE_EVIDENCE` to choose another directory. Mise writes JUnit to `/tmp/opencode/llmcmd-live-providers/{provider}.xml`. API keys, request headers, prompt/response text, tool arguments and raw SDK objects are not written to these reports. SDK request failures are reported by exception type without a raw response body.

## Offline verification and limits

```sh
mise run test:provider-sdk
```

This runs real OpenAI 2.14.0 and Anthropic 13.3.0 SDKs against a test-local HTTP handler. It checks the SDK bridges, native response projectors, extraction, retry suppression, project secret identity and configuration precedence. Its responses are synthetic and explicitly do not establish live API success. No credentials are read by these offline cases.

The projectors are integration-test code. The core provider adapter project still has no SDK dependency. They project accounting from SDK native representations, rather than preserving full HTTP response bytes. OpenAI Responses API, streaming native accounting, forced cache hits, pricing/invoice reconciliation, generated command/root execution and hosted Langfuse/LangSmith/Braintrust/Logfire read-back are separate tests. These tests do not claim any of those are covered.
