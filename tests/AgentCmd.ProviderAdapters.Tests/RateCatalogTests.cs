using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd.Providers;
using Zeeq.AgentCmd.Providers.Pricing;

namespace AgentCmd.ProviderAdapters.Tests;

public class RateCatalogTests
{
    [Test]
    public async Task Cards_can_succeed_each_other_by_date_and_cover_disjoint_context_bands()
    {
        var original = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "gpt-5.6-sol" && c.ValidFrom is not null
        );
        var after = original with
        {
            ValidFrom = new(2026, 12, 1),
            ValidThrough = null,
            Rates = original.Rates with { Input = 5 },
            Provenance = original.Provenance with { Id = "after-promo" },
        };
        var longer = after with
        {
            Scope = after.Scope with { ContextBand = "long" },
            Rates = after.Rates with { Output = 30 },
            Provenance = after.Provenance with { Id = "long-after-promo" },
        };
        var catalog = new TokenPricingCatalog([original, after, longer]);
        var snapshot = Snapshot(original);
        var before = catalog.Estimate(snapshot, original.Scope with { AsOf = new(2026, 11, 30) });
        var current = catalog.Estimate(snapshot, original.Scope with { AsOf = new(2026, 12, 1) });
        await Assert.That(current.Rate!.Id).IsEqualTo("after-promo:short");
        await Assert.That(current.TotalUsd > before.TotalUsd).IsTrue();
        await Assert.That(current.Reason.Contains("Context band is caller-asserted")).IsTrue();
        await Assert
            .That(
                catalog
                    .Estimate(
                        snapshot,
                        original.Scope with
                        {
                            ContextBand = "long",
                            AsOf = new(2026, 12, 1),
                        }
                    )
                    .Rate!.Id
            )
            .IsEqualTo("long-after-promo:long");
        var rejected = false;
        try
        {
            _ = new TokenPricingCatalog([original, after with { ValidFrom = new(2026, 11, 30) }]);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task The_same_host_binding_checks_the_captured_target_and_reported_model()
    {
        var card = TokenPricingCatalog.Default.Cards.Single(c => c.Model.ModelId == "gpt-6-luna");
        var binding = ModelRequestCatalog.Default.Bind(
            "azureopenai",
            ApiKind.ChatCompletions,
            "production",
            "gpt-6-luna"
        );
        var snapshot = Snapshot(card) with { RequestedModelId = "production" };
        var scope = card.Scope with { ContextBand = "short", TargetBinding = binding };
        await Assert
            .That(PricingEstimator.Estimate(snapshot, scope).Status)
            .IsEqualTo(PricingStatus.Complete);
        await Assert
            .That(PricingEstimator.Estimate(snapshot, scope with { TargetBinding = null }).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(snapshot with { RequestedModelId = "different-target" }, scope)
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(PricingEstimator.Estimate(snapshot with { ModelId = "gpt-6-sol" }, scope).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        var json = System.Text.Json.JsonSerializer.Serialize(scope);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<PricingScope>(json)!;
        await Assert
            .That(PricingEstimator.Estimate(snapshot, roundTrip).Status)
            .IsEqualTo(PricingStatus.Complete);
    }

    static UsageMeasurement M(string dimension, long value) =>
        new(dimension, value, Unit.Tokens, Relation.Total, null, MeasurementStatus.Agreed, []);

    static ResponseSnapshot Snapshot(TokenRateCard card, string? model = null, long input = 100) =>
        new(
            "rate-test",
            new(card.Model.ProviderKey, "test", card.Api),
            "test",
            null,
            null,
            "test",
            false,
            AttemptEnd.Completed,
            null,
            model ?? card.Model.ModelId,
            null,
            null,
            new(
                [
                    M(Dim.Input, input),
                    M(Dim.Uncached, input - 30),
                    M(Dim.Output, 40),
                    M(Dim.Cached, 20),
                    M(Dim.CacheWrite, 10),
                    M(Dim.CacheWrite5m, 10),
                    M(Dim.CacheWrite1h, 0),
                ],
                []
            ),
            new(0, 0, 0, 0),
            null,
            []
        );

    [Test]
    public async Task Every_reviewed_card_prices_the_same_disjoint_parts_with_its_own_rates()
    {
        foreach (var card in TokenPricingCatalog.Default.Cards)
        {
            // A time-limited card needs a date inside its own window.
            var scope = card.Scope with
            {
                ContextBand = "short",
                AsOf = card.ValidThrough ?? card.ValidFrom ?? new DateOnly(2026, 11, 2),
            };
            var expected =
                (
                    70 * card.Rates.Input
                    + 20 * card.Rates.CachedInput
                    + 10 * card.Rates.CacheWrite
                    + 40 * card.Rates.Output
                ) / 1_000_000m;
            foreach (var id in card.Model.ResponseIds)
            {
                var estimate = PricingEstimator.Estimate(Snapshot(card, id), scope);
                await Assert.That(estimate.Status).IsEqualTo(PricingStatus.Complete);
                await Assert.That(estimate.TotalUsd).IsEqualTo((decimal?)expected);
            }
        }
    }

    [Test]
    public async Task Sonnet_and_opus_share_protocol_arithmetic_without_haikus_context_surcharge()
    {
        foreach (var model in new[] { "claude-sonnet-5-5", "claude-opus-5-5" })
        {
            var card = TokenPricingCatalog.Default.Cards.Single(c => c.Model.ModelId == model);
            var snapshot = Snapshot(card, input: 900_000);
            var expected =
                (
                    899_970 * card.Rates.Input
                    + 20 * card.Rates.CachedInput
                    + 10 * card.Rates.CacheWrite
                    + 40 * card.Rates.Output
                ) / 1_000_000m;
            await Assert
                .That(PricingEstimator.Estimate(snapshot, card.Scope).TotalUsd)
                .IsEqualTo((decimal?)expected);
        }
    }

    [Test]
    public async Task Promotions_and_unverified_context_bands_do_not_leak_into_other_requests()
    {
        var card = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "gpt-5.6-sol" && c.ValidFrom is not null
        );
        var snapshot = Snapshot(card);
        // 8/31 belongs to the list-price card; the dates around the promotion stay unpriced.
        foreach (var date in new DateOnly?[] { null, new(2026, 12, 1) })
        {
            await Assert
                .That(PricingEstimator.Estimate(snapshot, card.Scope with { AsOf = date }).Status)
                .IsEqualTo(PricingStatus.Unpriced);
        }
        await Assert
            .That(
                PricingEstimator
                    .Estimate(
                        snapshot,
                        card.Scope with
                        {
                            AsOf = new(2026, 11, 2),
                            ContextBand = "long",
                        }
                    )
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
    }

    [Test]
    public async Task A_host_can_add_a_platform_card_without_changing_a_parser_or_subclass()
    {
        var original = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "claude-sonnet-5-5"
        );
        var custom = original with
        {
            Model = new("bedrock-test", "test-model", ["test-model-v1"]),
            Scope = new("reviewed-host-sku", "standard", "global"),
        };
        var cards = new List<TokenRateCard> { custom };
        var catalog = new TokenPricingCatalog(cards);
        cards.Clear();
        var snapshot = Snapshot(custom, "test-model-v1");
        await Assert
            .That(catalog.Estimate(snapshot, custom.Scope).Status)
            .IsEqualTo(PricingStatus.Complete);
        await Assert
            .That(PricingEstimator.Estimate(snapshot, custom.Scope).Status)
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                catalog
                    .Estimate(
                        snapshot,
                        custom.Scope with
                        {
                            TargetBinding = new("target", new("bedrock-test", "other-model")),
                        }
                    )
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        var rejected = false;
        try
        {
            _ = new TokenPricingCatalog([custom, custom]);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task Unsupported_platforms_and_missing_azure_prices_do_not_use_anthropic_or_direct_openai_prices()
    {
        var card = TokenPricingCatalog.Default.Cards.Single(c => c.Model.ModelId == "gpt-6-luna");
        var snapshot = Snapshot(card);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(
                        snapshot with
                        {
                            ModelId = "gpt-6.1-sol",
                        },
                        card.Scope with
                        {
                            ContextBand = "short",
                        }
                    )
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
        await Assert
            .That(
                PricingEstimator
                    .Estimate(
                        snapshot with
                        {
                            Registration = Fx.OpenAi,
                        },
                        card.Scope with
                        {
                            ContextBand = "short",
                        }
                    )
                    .Status
            )
            .IsEqualTo(PricingStatus.Unpriced);
    }

    [Test]
    public async Task The_list_price_applies_before_the_promotion_and_not_after()
    {
        var list = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "gpt-5.6-sol" && c.ValidFrom is null
        );
        var snapshot = Snapshot(list);
        var before = PricingEstimator.Estimate(
            snapshot,
            list.Scope with
            {
                AsOf = new(2026, 8, 31),
            }
        );
        await Assert.That(before.Status).IsEqualTo(PricingStatus.Complete);
        await Assert.That(list.Rates.Input).IsEqualTo(5m);
        await Assert.That(list.Rates.Output).IsEqualTo(30m);
    }

    [Test]
    public async Task Fable_models_have_standard_single_band_rates_and_no_request_profile()
    {
        var fable = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "claude-fable-5-2"
        );
        await Assert.That(fable.Rates.Input).IsEqualTo(8m);
        await Assert.That(fable.Rates.Output).IsEqualTo(40m);
        await Assert.That(fable.Rates.CachedInput).IsEqualTo(.20m);
        await Assert.That(fable.LongContextRates).IsNull();
        await Assert
            .That(PricingEstimator.Estimate(Snapshot(fable), fable.Scope).Status)
            .IsEqualTo(PricingStatus.Complete);
        await Assert
            .That(TokenPricingCatalog.Default.Cards.Any(c => c.Model.ModelId == "claude-fable-5-1"))
            .IsTrue();
    }

    [Test]
    public async Task A_host_override_replaces_only_the_overlapping_builtin_card()
    {
        var builtIn = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "claude-sonnet-5-5"
        );
        var negotiated = builtIn with
        {
            Rates = builtIn.Rates with { Input = 1m, Output = 5m },
            Provenance = builtIn.Provenance with { Id = "contract-2026" },
        };
        var catalog = TokenPricingCatalog.Create(options => options.Override(negotiated));
        var snapshot = Snapshot(builtIn);

        var custom = PricingEstimator.Estimate(snapshot, builtIn.Scope, catalog);
        var standard = PricingEstimator.Estimate(snapshot, builtIn.Scope);

        await Assert.That(custom.Status).IsEqualTo(PricingStatus.Complete);
        await Assert.That(custom.TotalUsd < standard.TotalUsd).IsTrue();
        await Assert.That(catalog.Cards.Count).IsEqualTo(TokenPricingCatalog.Default.Cards.Count);
        await Assert
            .That(
                TokenPricingCatalog
                    .Default.Cards.Single(c => c.Model.ModelId == "claude-sonnet-5-5")
                    .Rates.Input
            )
            .IsEqualTo(2m);
    }

    [Test]
    public async Task Add_rejects_an_overlap_while_remove_and_clear_make_usage_unpriced()
    {
        var builtIn = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "claude-opus-5-5"
        );
        var snapshot = Snapshot(builtIn);

        await Assert
            .That(() => TokenPricingCatalog.Create(options => options.Add(builtIn)))
            .Throws<ArgumentException>();

        var removed = TokenPricingCatalog.Create(options =>
            options.Remove("anthropic", "claude-opus-5-5")
        );
        await Assert
            .That(PricingEstimator.Estimate(snapshot, builtIn.Scope, removed).Status)
            .IsEqualTo(PricingStatus.Unpriced);

        var cleared = TokenPricingCatalog.Create(options => options.Clear());
        await Assert.That(cleared.Cards.Count).IsEqualTo(0);
    }

    [Test]
    public async Task The_di_helper_registers_one_catalog_with_host_overrides()
    {
        var builtIn = TokenPricingCatalog.Default.Cards.Single(c =>
            c.Model.ModelId == "claude-sonnet-5-5"
        );
        var negotiated = builtIn with { Rates = builtIn.Rates with { Input = 1m } };
        var services = new ServiceCollection().AddAgentCmdPricing(options =>
            options.Override(negotiated)
        );
        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<TokenPricingCatalog>();

        await Assert
            .That(catalog.Cards.Single(c => c.Model.ModelId == "claude-sonnet-5-5").Rates.Input)
            .IsEqualTo(1m);
        await Assert.That(() => services.AddAgentCmdPricing()).Throws<InvalidOperationException>();
        await Assert
            .That(() => new ServiceCollection().AddAgentCmdPricing(options => options.Add(builtIn)))
            .Throws<ArgumentException>();
    }
}
