# Model policies and pricing

DMTS review selected composition over a class per model. Three concerns change independently: protocol accounting, model/SKU prices, and request capabilities. The shared exact identity table joins them. A new model normally adds reviewed data and contract cases; it does not copy a parser or estimator.

`ProviderModelIdentity` lists exact aliases and versions. `ModelRequestProfile` declares supported effort values, default effort, temperature/top-p constraints, output limits, and sources. A fixed sampling value uses equal bounds. A nullable output ceiling means unchecked; the built-in 128k limits come from the linked Microsoft model listing and Claude model overviews. Hosts can construct another `ModelRequestCatalog` for newer models.

`ModelPolicyChatClient` validates standard MEAI options without mutation. Put it inside retry/fallback and outside provider extraction. A rejected request makes no provider call and creates no extraction attempt when composed in that order. Treat `ModelPolicyViolationException` as nontransient. Raw SDK options and additional properties are deliberately outside this validator.

## Host setup

```csharp
var capabilities = ModelRequestCatalog.Default;
var binding = capabilities.Bind(
    "azureopenai", ApiKind.ChatCompletions, "invoice-production", "gpt-6-astra");

var client = azureClient.AsBuilder()
    .UseModelPolicy(registration, capabilities, [binding])
    .UseProviderExtraction(extractor, registration, sink)
    .Build();

// The host chooses settings. The policy rejects invalid values before transport.
var response = await client.GetResponseAsync(messages, new ChatOptions
{
    ModelId = binding.Target,
    Reasoning = new() { Effort = ReasoningEffort.Low },
    MaxOutputTokens = 256,
}, ct);

// Reuse the same binding and the stored request date for read-side estimates.
var scope = new PricingScope("azure-openai-global-standard", "standard", "global",
    ContextBand: "short", TargetBinding: binding, AsOf: requestDate);
var estimate = PricingEstimator.Estimate(snapshot, scope);
```

The SKU, geography and context band in this example are host assertions. The resource name or screenshot does not prove those billing settings. `RequestedModelId` is the application target; `ModelId` is what the provider actually returned. Pricing checks both through the shared binding. Unknown versions never inherit a price by prefix.

## Parameter constraints

GPT-6 Astra and GPT-6.1 Sol do not support `none`; the live test uses `low`. GPT-5.6 Sol/Terra and GPT-6 Sol/Luna allow `none`. The documented default for these GPT families is `medium`. Sampling is allowed only with `none`; the live matrix omits temperature and top-p altogether. Microsoft's broad support table is less specific, so the profile retains the stricter effort-dependent rule. See [OpenAI guidance](https://developers.openai.com/api/docs/guides/latest-model) and [Microsoft reasoning models](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning).

Claude Haiku 5.5 supports disabled thinking. Sonnet 5.5 requires `between_tools` to turn off up-front thinking; the pinned MEAI bridge maps `None` to the incompatible `disabled`, so its tested path uses adaptive `low`. Opus 5.5 requires thinking. All three profiles omit sampling as directed by their current docs. See [Haiku](https://platform.claude.com/docs/en/models/haiku-5-5/overview), [Sonnet migration](https://platform.claude.com/docs/en/models/sonnet-5-5/migration-guide), and [Opus](https://platform.claude.com/docs/en/models/opus-5-5/overview).

## Coverage and boundaries

Offline cases exercise all six Azure models, Haiku/Sonnet/Opus 5.5, exact versions, deployment binding, fixed temperature, invalid settings, and immutable option forwarding. Direct OpenAI profiles have SDK contract coverage, not live coverage from this matrix. No claim is made about every model, API mode, tool combination or sampling option.

The explicit live matrix sends one generated command to each Azure deployment and to Haiku/Sonnet, then reads typed output from the ledger. It uses no tools, no SDK retries, and at most 64 output tokens with thinking off or 512 when thinking is required. Capture replay verifies the final estimator against those stored snapshots without new model calls.

Rate cards are frozen, support disjoint date/context intervals, and retain provenance. Anthropic Messages and Chat Completions use different input partitions, while all models share the same completeness and overflow checks. Unknown usage remains unknown. Azure 6.1 pricing is unpriced until numeric Azure-specific rates are verified. See [pricing provenance](../research/provider-pricing.md).
