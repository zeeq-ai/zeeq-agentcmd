# Provider accounting

Provider response adapters read MEAI responses and optional native accounting projections into immutable per-attempt snapshots. This assembly has no OpenAI or Anthropic SDK dependency. `ProviderExtractionChatClient` preserves the provider response and isolates observer failures.

Use explicit provider/protocol registration. Cached, reasoning and audio details are subsets, not extra totals. Missing counts remain unknown. Conflicting normalized/native evidence stays visible. Raw content is off by default.

The maintained tests cover protocol mappings and real SDK boundaries. The explicit live matrix exercises the six configured Azure deployments and Claude Haiku/Sonnet 5.5 through generated commands and stored output readback. Claude Opus 5.5 and direct OpenAI profiles have offline contract coverage; this matrix does not establish live support for them. Paid calls require explicit opt-in.

`ProviderBillingContext` retains only known service-tier, inference-geography and speed categories. Missing metadata remains absent. Unrecognized values become `unknown` with a bounded diagnostic; they cannot silently select a cheaper rate.

`PricingEstimator` delegates to `TokenPricingCatalog.Default`. All model cards use one accounting engine, with protocol-specific cache partition semantics. Model changes normally add immutable data rather than a subclass or parser. Hosts can construct their own catalog with reviewed `TokenRateCard` data. Exact model versions, billing scope, context band and applicable dates select a card; overlapping cards are rejected. [Rate provenance and scope](../../docs/research/provider-pricing.md) describe the assumptions. These estimates cover supported token charges, not invoices.

`ModelPolicyChatClient` is optional MEAI middleware. Its separate `ModelRequestCatalog` checks effort, sampling and output limits without changing options. `SamplingConstraint.Fixed(1)` expresses a fixed temperature; unsupported means omit the field, even if the value looks like a default. Unknown models fail locally unless the host explicitly permits them. Raw SDK factories and additional properties remain the host's responsibility.

One `ModelTargetBinding` maps an application target to an underlying model for both validation and pricing. Azure requires an explicit map. `ResponseSnapshot.RequestedModelId` records the requested deployment separately from the reported model. A captured Azure request without its map, or a mapping/version mismatch, remains unpriced. Legacy snapshots without a requested target can price the reported model, with that verification gap stated in the estimate.

Register the policy outside extraction and inside the host's retry/fallback choices. Classify `ModelPolicyViolationException` as nontransient. Each fallback branch needs its own target mapping. Defaults belong in the host's options or command hooks; the policy does not silently remove a temperature or change reasoning effort. See [model policies](../../docs/architecture/provider-model-policies.md) for setup and coverage.

Native streaming extraction, OpenAI Responses accounting and invoice reconciliation remain outside this package's current guarantees.
