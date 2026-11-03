using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd.Providers;
using Zeeq.LlmCmd.Providers.Platforms;

namespace LlmCmd.ProviderAdapters.Tests;

public class OpenAiChatTests
{
    [Test]
    public async Task Subsets_are_not_added_to_totals()
    {
        var r = Fx.Run("openai-chat-basic", Fx.OpenAi);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Extracted");
        await Assert.That(u.Input).IsEqualTo(100L);
        await Assert.That(u.Output).IsEqualTo(40L);
        await Assert.That(u.Total).IsEqualTo(140L);
        await Assert.That(u.ValueOf(Dim.Cached)).IsEqualTo(60L);
        await Assert.That(u.ValueOf(Dim.Reasoning)).IsEqualTo(10L);
        await Assert.That(u.Find(Dim.Cached)!.Relation).IsEqualTo(Relation.SubsetOfParent);
        await Assert.That(u.Find(Dim.Cached)!.Parent).IsEqualTo(Dim.Input);
        await Assert.That(u.Conflicts.Count).IsEqualTo(0);
        await Assert.That(r.Snapshot.NativeStopReason).IsEqualTo("stop");
        await Assert.That(r.Snapshot.AdapterId).IsEqualTo("openai.chat-completions:openai");
    }

    [Test]
    public async Task Agreeing_aliases_collapse_to_one_measurement_with_both_origins()
    {
        var u = Fx.Run("openai-chat-basic", Fx.OpenAi).Snapshot.Usage;
        await Assert.That(u.Measurements.Count(m => m.Dimension == Dim.Input)).IsEqualTo(1);
        await Assert
            .That(Fx.Origins(u.Find(Dim.Input)))
            .IsEquivalentTo(new[] { Origin.Normalized, Origin.Native });
        await Assert.That(u.Find(Dim.Input)!.Status).IsEqualTo(MeasurementStatus.Agreed);
        await Assert
            .That(u.Find(Dim.Input)!.Observations[1].Path)
            .IsEqualTo("wire:usage.prompt_tokens");
    }

    [Test]
    public async Task Audio_and_prediction_stay_separate_dimensions()
    {
        var r = Fx.Run("openai-chat-audio-prediction", Fx.OpenAi);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Extracted");
        await Assert.That(u.ValueOf(Dim.InputAudio)).IsEqualTo(20L);
        await Assert.That(u.ValueOf(Dim.OutputAudio)).IsEqualTo(5L);
        await Assert.That(u.ValueOf(Dim.PredictionAccepted)).IsEqualTo(6L);
        await Assert.That(u.ValueOf(Dim.PredictionRejected)).IsEqualTo(4L);
        await Assert.That(u.ValueOf(Dim.Cached)).IsEqualTo(30L);
        await Assert.That(u.ValueOf(Dim.Reasoning)).IsNull();
        await Assert
            .That(Fx.Origins(u.Find(Dim.PredictionRejected)))
            .IsEquivalentTo(new[] { Origin.NormalizedAdditional, Origin.Native });
        await Assert
            .That(u.Find(Dim.PredictionRejected)!.Observations[1].Note!.Contains("billed"))
            .IsTrue();
        await Assert.That(r.Snapshot.NativeStopReason).IsEqualTo("length");
        await Assert.That(u.Total).IsEqualTo(140L);
    }

    [Test]
    public async Task Extension_fields_are_retained_and_unknown_keys_are_diagnosed_without_losing_baseline()
    {
        var r = Fx.Run("openai-chat-extension-fields", Fx.OpenAi);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(u.ValueOf(Dim.CacheWrite)).IsEqualTo(25L);
        await Assert
            .That(u.Find(Dim.CacheWrite)!.Observations[0].Note!.Contains("unadjusted"))
            .IsTrue();
        await Assert.That(u.Input).IsEqualTo(100L);
        await Assert.That(u.Total).IsEqualTo(140L);
        var d = r.Snapshot.Diagnostics.Single(x => x.Code == "unknown-field");
        await Assert
            .That(d.Path)
            .IsEqualTo("$.usage.prompt_tokens_details.synthetic_future_detail");
        await Assert.That(d.Message.Contains("Number")).IsTrue();
    }

    [Test]
    public async Task Conflicting_native_value_keeps_normalized_value_and_reports_both()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
        {
            Usage = new()
            {
                InputTokenCount = 100,
                OutputTokenCount = 40,
                TotalTokenCount = 140,
                CachedInputTokenCount = 60,
            },
        };
        var wire = Fx.Json(
            """{"usage":{"prompt_tokens":100,"completion_tokens":40,"total_tokens":140,"prompt_tokens_details":{"cached_tokens":50}}}"""
        );
        var r = Fx.Extractor().Extract(Fx.OpenAi, response, "a", wire);
        var cached = r.Snapshot.Usage.Find(Dim.Cached)!;
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(cached.Value).IsEqualTo(60L);
        await Assert.That(cached.Status).IsEqualTo(MeasurementStatus.Conflicted);
        await Assert
            .That(cached.Observations.Select(o => o.Value).ToArray())
            .IsEquivalentTo(new long[] { 60, 50 });
        await Assert
            .That(r.Snapshot.Usage.Conflicts.Single().Kind)
            .IsEqualTo(ConflictKind.ValueMismatch);
    }

    [Test]
    public async Task Total_that_disagrees_with_input_plus_output_is_a_conflict_not_a_correction()
    {
        var wire = Fx.Json(
            """{"usage":{"prompt_tokens":100,"completion_tokens":40,"total_tokens":150}}"""
        );
        var r = Fx.Extractor()
            .Extract(
                Fx.OpenAi,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")),
                "a",
                wire
            );
        await Assert.That(r.Snapshot.Usage.Total).IsEqualTo(150L);
        await Assert
            .That(r.Snapshot.Usage.Conflicts.Single().Kind)
            .IsEqualTo(ConflictKind.PartitionSumMismatch);
        await Assert.That(r.Snapshot.Usage.Conflicts.Single().Dimension).IsEqualTo(Dim.Total);
    }

    [Test]
    public async Task Wire_zero_is_a_native_observation_but_a_normalized_zero_alone_does_not_claim_wire_presence()
    {
        var withWire = Fx.Extractor()
            .Extract(
                Fx.OpenAi,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
                {
                    Usage = new() { InputTokenCount = 0 },
                },
                "a",
                Fx.Json("""{"usage":{"prompt_tokens":0}}""")
            );
        var noWire = Fx.Extractor()
            .Extract(
                Fx.OpenAi,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
                {
                    Usage = new() { InputTokenCount = 0 },
                },
                "a",
                Fx.Json("""{"usage":{}}""")
            );
        await Assert
            .That(Fx.Origins(withWire.Snapshot.Usage.Find(Dim.Input)))
            .IsEquivalentTo(new[] { Origin.Normalized, Origin.Native });
        await Assert
            .That(Fx.Origins(noWire.Snapshot.Usage.Find(Dim.Input)))
            .IsEquivalentTo(new[] { Origin.Normalized });
        await Assert.That(noWire.Snapshot.Usage.Input).IsEqualTo(0L);
        await Assert.That(noWire.Snapshot.Usage.Output).IsNull();
    }
}

public class AnthropicMessagesTests
{
    [Test]
    public async Task Inclusive_input_is_native_uncached_plus_read_plus_write_and_is_not_incremented_again()
    {
        var r = Fx.Run("anthropic-cache-ttl", Fx.Anthropic);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Extracted");
        await Assert.That(u.Input).IsEqualTo(110L);
        await Assert.That(u.Total).IsEqualTo(150L);
        await Assert.That(u.Output).IsEqualTo(40L);
        await Assert.That(u.ValueOf(Dim.Uncached)).IsEqualTo(10L);
        await Assert.That(u.ValueOf(Dim.Cached)).IsEqualTo(80L);
        await Assert
            .That(Fx.Origins(u.Find(Dim.Input)))
            .IsEquivalentTo(new[] { Origin.Normalized, Origin.Computed });
        await Assert.That(u.Find(Dim.Input)!.Observations[1].Path.Contains("computed:")).IsTrue();
        await Assert.That(r.Snapshot.NativeStopReason).IsEqualTo("end_turn");
        await Assert.That(u.Conflicts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ttl_buckets_partition_cache_write_and_are_not_added_to_it()
    {
        var u = Fx.Run("anthropic-cache-ttl", Fx.Anthropic).Snapshot.Usage;
        await Assert.That(u.ValueOf(Dim.CacheWrite)).IsEqualTo(20L);
        await Assert.That(u.ValueOf(Dim.CacheWrite5m)).IsEqualTo(12L);
        await Assert.That(u.ValueOf(Dim.CacheWrite1h)).IsEqualTo(8L);
        await Assert.That(u.Find(Dim.CacheWrite5m)!.Relation).IsEqualTo(Relation.PartOfParent);
        await Assert.That(u.Find(Dim.CacheWrite5m)!.Parent).IsEqualTo(Dim.CacheWrite);
        await Assert
            .That(Fx.Origins(u.Find(Dim.CacheWrite)))
            .IsEquivalentTo(new[] { Origin.NormalizedAdditional, Origin.Native });
        await Assert.That(u.Input).IsEqualTo(110L);
    }

    [Test]
    public async Task Thinking_is_within_output_with_provenance_and_server_tool_counters_are_requests()
    {
        var u = Fx.Run("anthropic-cache-ttl", Fx.Anthropic).Snapshot.Usage;
        await Assert.That(u.Output).IsEqualTo(40L);
        var thinking = u.Find(Dim.Reasoning)!;
        await Assert.That(thinking.Value).IsEqualTo(15L);
        await Assert.That(thinking.Parent).IsEqualTo(Dim.Output);
        await Assert
            .That(thinking.Observations[0].Path)
            .IsEqualTo("wire:usage.output_tokens_details.thinking_tokens");
        await Assert.That(thinking.Observations[0].Note!.Contains("re-tokenization")).IsTrue();
        var search = u.Find(Dim.WebSearchRequests)!;
        await Assert.That(search.Unit).IsEqualTo(Unit.Requests);
        await Assert.That(search.Value).IsEqualTo(2L);
        await Assert.That(search.Relation).IsEqualTo(Relation.Independent);
        await Assert.That(u.Find(Dim.WebFetchRequests)!.Value).IsEqualTo(0L);
        await Assert.That(u.Total).IsEqualTo(150L);
    }

    [Test]
    public async Task Ttl_mismatch_is_a_conflict_and_no_residual_is_invented()
    {
        var r = Fx.Run("anthropic-ttl-mismatch", Fx.Anthropic);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        var c = u.Conflicts.Single();
        await Assert.That(c.Dimension).IsEqualTo(Dim.CacheWrite);
        await Assert.That(c.Kind).IsEqualTo(ConflictKind.PartitionSumMismatch);
        await Assert.That(u.ValueOf(Dim.CacheWrite)).IsEqualTo(20L);
        await Assert.That(u.Find(Dim.CacheWrite)!.Status).IsEqualTo(MeasurementStatus.Conflicted);
        await Assert.That(u.ValueOf(Dim.CacheWrite1h)).IsEqualTo(9L);
        await Assert.That(u.Input).IsEqualTo(110L);
    }

    [Test]
    public async Task Incomplete_ttl_split_is_partial_without_a_fabricated_bucket()
    {
        var r = Fx.Run("anthropic-ttl-incomplete", Fx.Anthropic);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(r).Contains("ttl-split-incomplete")).IsTrue();
        await Assert.That(u.ValueOf(Dim.CacheWrite5m)).IsEqualTo(12L);
        await Assert.That(u.ValueOf(Dim.CacheWrite1h)).IsNull();
        await Assert.That(u.ValueOf(Dim.CacheWrite)).IsEqualTo(20L);
        await Assert.That(u.Conflicts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_non_normalizing_client_that_passes_uncached_input_is_reported_as_a_conflict_not_silently_corrected()
    {
        var r = Fx.Run("anthropic-unnormalized-bridge", Fx.Anthropic);
        var input = r.Snapshot.Usage.Find(Dim.Input)!;
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(input.Value).IsEqualTo(10L);
        await Assert.That(input.Status).IsEqualTo(MeasurementStatus.Conflicted);
        await Assert
            .That(input.Observations.Select(o => o.Value).ToArray())
            .IsEquivalentTo(new long[] { 10, 110 });
        await Assert
            .That(
                r.Snapshot.Usage.Conflicts.Any(c =>
                    c.Dimension == Dim.Input && c.Kind == ConflictKind.ValueMismatch
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Native_only_response_derives_inclusive_input_and_total_with_computed_provenance()
    {
        var r = Fx.Run("anthropic-cache-ttl", Fx.Anthropic, withUsage: false);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Extracted");
        await Assert.That(u.Input).IsEqualTo(110L);
        await Assert.That(u.Total).IsEqualTo(150L);
        await Assert.That(Fx.Origins(u.Find(Dim.Input))).IsEquivalentTo(new[] { Origin.Computed });
        await Assert.That(Fx.Origins(u.Find(Dim.Total))).IsEquivalentTo(new[] { Origin.Computed });
        await Assert.That(u.ValueOf(Dim.CacheWrite)).IsEqualTo(20L);
    }

    [Test]
    public async Task Missing_cache_term_leaves_inclusive_input_unknown()
    {
        var wire = Fx.Json(
            """{"stop_reason":"end_turn","usage":{"input_tokens":10,"cache_creation_input_tokens":20,"output_tokens":40}}"""
        );
        var r = Fx.Extractor()
            .Extract(
                Fx.Anthropic,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")),
                "a",
                wire
            );
        var u = r.Snapshot.Usage;
        await Assert.That(u.Input).IsNull();
        await Assert.That(u.Total).IsNull();
        await Assert.That(u.ValueOf(Dim.Uncached)).IsEqualTo(10L);
        await Assert.That(Fx.Codes(r).Contains("input-total-not-derivable")).IsTrue();
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
    }
}

public class DeepSeekChatTests
{
    [Test]
    public async Task Hit_miss_and_nested_cached_collapse_without_becoming_cache_creation()
    {
        var r = Fx.Run("deepseek-chat-cache", Fx.DeepSeek);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Extracted");
        await Assert.That(u.Input).IsEqualTo(100L);
        await Assert.That(u.Total).IsEqualTo(140L);
        await Assert.That(u.ValueOf(Dim.Cached)).IsEqualTo(60L);
        await Assert
            .That(Fx.Origins(u.Find(Dim.Cached)))
            .IsEquivalentTo(new[] { Origin.Normalized, Origin.Native, Origin.Native });
        await Assert
            .That(u.Find(Dim.Cached)!.Observations.Select(o => o.Path).ToArray())
            .IsEquivalentTo(
                new[]
                {
                    "meai:Usage.CachedInputTokenCount",
                    "wire:usage.prompt_tokens_details.cached_tokens",
                    "wire:usage.prompt_cache_hit_tokens",
                }
            );
        await Assert.That(u.ValueOf(Dim.DeepSeekCacheMiss)).IsEqualTo(40L);
        await Assert
            .That(u.Find(Dim.DeepSeekCacheMiss)!.Relation)
            .IsEqualTo(Relation.SubsetOfParent);
        await Assert.That(u.Find(Dim.CacheWrite)).IsNull();
        await Assert.That(u.ValueOf(Dim.Reasoning)).IsEqualTo(12L);
        await Assert.That(r.Snapshot.AdapterId).IsEqualTo("deepseek.chat-completions");
    }

    [Test]
    public async Task Hit_plus_miss_mismatch_and_alias_mismatch_report_conflicts_and_keep_baseline()
    {
        var r = Fx.Run("deepseek-chat-mismatch", Fx.DeepSeek);
        var u = r.Snapshot.Usage;
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(u.Input).IsEqualTo(100L);
        await Assert.That(u.ValueOf(Dim.Cached)).IsEqualTo(55L);
        await Assert
            .That(
                u.Conflicts.Any(c =>
                    c.Dimension == Dim.Input && c.Kind == ConflictKind.PartitionSumMismatch
                )
            )
            .IsTrue();
        await Assert
            .That(
                u.Conflicts.Any(c =>
                    c.Dimension == Dim.Cached && c.Kind == ConflictKind.ValueMismatch
                )
            )
            .IsTrue();
        await Assert.That(u.Find(Dim.Cached)!.Status).IsEqualTo(MeasurementStatus.Conflicted);
    }
}

public class SafetyTests
{
    [Test]
    public async Task Normalized_only_client_keeps_usage_preserves_unknown_counts_and_does_not_guess_a_provider()
    {
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry([new AnthropicMessagesAdapter(), new DeepSeekChatAdapter()])
        );
        var reg = new ProviderIdentity("acme-gateway", "acme-1", ApiKind.ChatCompletions);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
        {
            ModelId = "deepseek-chat",
            Usage = new()
            {
                InputTokenCount = 5,
                OutputTokenCount = 7,
                TotalTokenCount = 12,
                AdditionalCounts = new() { ["Vendor.Thing"] = 3 },
            },
        };
        var r = extractor.Extract(reg, response, "a");
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(r.Snapshot.AdapterId).IsEqualTo("normalized-only");
        await Assert.That(r.Snapshot.Registration.ProviderKey).IsEqualTo("acme-gateway");
        await Assert.That(r.Snapshot.ModelId).IsEqualTo("deepseek-chat");
        await Assert.That(Fx.Codes(r).Contains("no-adapter")).IsTrue();
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(5L);
        await Assert.That(r.Snapshot.Usage.Total).IsEqualTo(12L);
        var unknown = r.Snapshot.Usage.Find("additional:Vendor.Thing")!;
        await Assert.That(unknown.Value).IsEqualTo(3L);
        await Assert.That(unknown.Unit).IsEqualTo(Unit.Unknown);
        await Assert.That(unknown.Relation).IsEqualTo(Relation.Unknown);
    }

    [Test]
    public async Task Absent_usage_and_null_fields_stay_unknown_not_zero()
    {
        var none = Fx.Extractor()
            .Extract(Fx.OpenAi, new ChatResponse(new ChatMessage(ChatRole.Assistant, "x")), "a");
        await Assert.That(Fx.Status(none)).IsEqualTo("Partial");
        await Assert.That(none.Snapshot.Usage.Measurements.Count).IsEqualTo(0);
        await Assert.That(none.Snapshot.Usage.Input).IsNull();
        await Assert.That(Fx.Codes(none).Contains("usage-absent")).IsTrue();
        var partial = Fx.Extractor()
            .Extract(
                Fx.OpenAi,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
                {
                    Usage = new() { OutputTokenCount = 7 },
                },
                "a",
                Fx.Json("{}")
            );
        await Assert.That(partial.Snapshot.Usage.Input).IsNull();
        await Assert.That(partial.Snapshot.Usage.Output).IsEqualTo(7L);
        await Assert.That(Fx.Codes(partial).Contains("wire-usage-absent")).IsTrue();
    }

    [Test]
    public async Task Negative_overflow_fractional_and_wrongly_typed_counts_are_invalid_and_keep_the_baseline()
    {
        foreach (var bad in new[] { "-5", "\"abc\"", "9223372036854775808", "1.5", "true", "{}" })
        {
            var wire = Fx.Json(
                $$$"""{"usage":{"prompt_tokens":{{{bad}}},"completion_tokens":40}}"""
            );
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
            {
                Usage = new() { InputTokenCount = 100, OutputTokenCount = 40 },
            };
            var r = Fx.Extractor().Extract(Fx.OpenAi, response, "a", wire);
            await Assert.That(Fx.Status(r)).IsEqualTo("Invalid");
            await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(100L);
            await Assert.That(r.Snapshot.Usage.Find(Dim.Input)!.Observations.Count).IsEqualTo(1);
            await Assert
                .That(
                    r.Snapshot.Diagnostics.Any(d =>
                        d.Severity == Severity.Error && d.Path == "$.usage.prompt_tokens"
                    )
                )
                .IsTrue();
        }
    }

    [Test]
    public async Task Negative_normalized_counts_are_invalid_and_unknown()
    {
        var r = Fx.Extractor()
            .Extract(
                Fx.OpenAi,
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
                {
                    Usage = new()
                    {
                        InputTokenCount = -1,
                        OutputTokenCount = 4,
                        AdditionalCounts = new() { ["InputTokenDetails.AudioTokenCount"] = -2 },
                    },
                },
                "a",
                Fx.Json("{}")
            );
        await Assert.That(Fx.Status(r)).IsEqualTo("Invalid");
        await Assert.That(r.Snapshot.Usage.Input).IsNull();
        await Assert.That(r.Snapshot.Usage.Output).IsEqualTo(4L);
        await Assert.That(r.Snapshot.Usage.Find(Dim.InputAudio)).IsNull();
        await Assert
            .That(r.Snapshot.Diagnostics.Count(d => d.Code == "negative-count"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task Unsupported_native_object_is_partial_with_usage_intact()
    {
        var (response, _, _) = Fx.Load("openai-chat-basic", withRaw: false);
        response.RawRepresentation = new object();
        var r = Fx.Extractor().Extract(Fx.OpenAi, response, "a");
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(r).Contains("unsupported-native")).IsTrue();
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(100L);
        await Assert.That(r.Snapshot.Usage.ValueOf(Dim.Cached)).IsEqualTo(60L);
        await Assert
            .That(Fx.Origins(r.Snapshot.Usage.Find(Dim.Input)))
            .IsEquivalentTo(new[] { Origin.Normalized });
    }

    [Test]
    public async Task Missing_raw_representation_after_serialization_reports_unavailable_enrichment()
    {
        var r = Fx.Run("anthropic-cache-ttl", Fx.Anthropic, withRaw: false);
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(r).Contains("native-unavailable")).IsTrue();
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(110L);
        await Assert.That(r.Snapshot.Usage.ValueOf(Dim.CacheWrite)).IsEqualTo(20L);
        await Assert.That(r.Snapshot.Usage.Find(Dim.CacheWrite5m)).IsNull();
    }

    [Test]
    public async Task Json_document_json_node_and_explicit_raw_json_are_all_accepted_and_explicit_wins()
    {
        var (response, wire, _) = Fx.Load("openai-chat-basic", withRaw: false);
        var text = wire.GetRawText();
        foreach (
            object raw in new object[] { JsonDocument.Parse(text), JsonNode.Parse(text)!, wire }
        )
        {
            response.RawRepresentation = raw;
            await Assert
                .That(Fx.Status(Fx.Extractor().Extract(Fx.OpenAi, response, "a")))
                .IsEqualTo("Extracted");
        }
        response.RawRepresentation = new object();
        var explicitResult = Fx.Extractor().Extract(Fx.OpenAi, response, "a", wire);
        await Assert.That(Fx.Status(explicitResult)).IsEqualTo("Extracted");
        await Assert.That(Fx.Codes(explicitResult).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Optional_native_projector_turns_a_sdk_like_object_into_wire_json()
    {
        var native = new FakeSdkCompletion(100, 40, 60);
        var (response, _, _) = Fx.Load("openai-chat-basic", withRaw: false);
        response.RawRepresentation = native;
        var r = Fx.Extractor(null, new FakeSdkProjector()).Extract(Fx.OpenAi, response, "a");
        await Assert.That(Fx.Status(r)).IsEqualTo("Extracted");
        await Assert
            .That(Fx.Origins(r.Snapshot.Usage.Find(Dim.Cached)))
            .IsEquivalentTo(new[] { Origin.Normalized, Origin.Native });
        // A projector for another API kind is not consulted.
        var other = Fx.Extractor(null, new FakeSdkProjector(ApiKind.Messages))
            .Extract(Fx.OpenAi, response, "a");
        await Assert.That(Fx.Codes(other).Contains("unsupported-native")).IsTrue();
        // A throwing projector is isolated.
        var failures = new List<string>();
        var failing = Fx.Extractor(
                new ProviderExtractionOptions { OnFailure = (stage, _) => failures.Add(stage) },
                new FakeSdkProjector(throws: true)
            )
            .Extract(Fx.OpenAi, response, "a");
        await Assert.That(Fx.Status(failing)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(failing).Contains("native-projection-failed")).IsTrue();
        await Assert.That(failures.ToArray()).IsEquivalentTo(new[] { "projector" });
        await Assert.That(failing.Snapshot.Usage.Input).IsEqualTo(100L);
    }

    [Test]
    public async Task Adapter_defect_drops_partial_writes_keeps_baseline_and_is_reported_once()
    {
        var failures = new List<string>();
        var extractor = new ProviderExtractor(
            new ProviderAdapterRegistry([new ThrowingAdapter()]),
            new ProviderExtractionOptions
            {
                OnFailure = (stage, e) => failures.Add(stage + ":" + e.GetType().Name),
            }
        );
        var (response, _, _) = Fx.Load("openai-chat-basic");
        var r = extractor.Extract(Fx.OpenAi, response, "a");
        await Assert.That(Fx.Status(r)).IsEqualTo("Partial");
        await Assert.That(Fx.Codes(r).Contains("adapter-failed")).IsTrue();
        await Assert.That(r.Snapshot.Usage.Input).IsEqualTo(100L);
        await Assert.That(r.Snapshot.Usage.Find("defect.partial_write")).IsNull();
        await Assert
            .That(failures.ToArray())
            .IsEquivalentTo(new[] { "adapter:InvalidOperationException" });
    }
}

sealed record FakeSdkCompletion(int Prompt, int Completion, int Cached);

/// <summary>Test-only stand-in for an optional native-SDK package. It is not production SDK support.</summary>
sealed class FakeSdkProjector(ApiKind api = ApiKind.ChatCompletions, bool throws = false)
    : INativeResponseProjector
{
    public string Id => "test.fake-sdk";
    public ApiKind Api => api;

    public bool CanProject(object native) => native is FakeSdkCompletion;

    public JsonElement Project(object native)
    {
        if (throws)
        {
            throw new InvalidOperationException("projection failed");
        }

        var c = (FakeSdkCompletion)native;
        return Fx.Json(
            "{\"choices\":[{\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":"
                + c.Prompt
                + ",\"completion_tokens\":"
                + c.Completion
                + ",\"total_tokens\":"
                + (c.Prompt + c.Completion)
                + ",\"prompt_tokens_details\":{\"cached_tokens\":"
                + c.Cached
                + "},\"completion_tokens_details\":{\"reasoning_tokens\":10}}}"
        );
    }
}

sealed class ThrowingAdapter : IProviderResponseAdapter
{
    public string Id => "test.throwing";
    public string Version => "0";
    public string RulesRevision => "test";
    public string? ProviderKey => "openai";
    public ApiKind Api => ApiKind.ChatCompletions;
    public StreamUsageMode StreamUsage => StreamUsageMode.Undeclared;
    public IReadOnlyDictionary<string, AdditionalCountRule> AdditionalCounts { get; } =
        new Dictionary<string, AdditionalCountRule>();

    public void Enrich(AdapterContext context)
    {
        context.Usage.Observe(
            "defect.partial_write",
            1,
            Unit.Tokens,
            Relation.Unknown,
            null,
            Origin.Native,
            "wire:x"
        );
        throw new InvalidOperationException("adapter defect");
    }
}
