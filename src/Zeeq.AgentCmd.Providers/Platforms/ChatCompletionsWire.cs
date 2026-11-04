using System.Text.Json;

namespace Zeeq.AgentCmd.Providers.Platforms;

/// <summary>
/// Shared reader for the OpenAI-shaped Chat Completions <c>usage</c> object. Both the OpenAI and DeepSeek modules use it;
/// neither module references the other. Compatible providers register their own adapter instance explicitly.
/// </summary>
static class ChatCompletionsWire
{
    public static readonly IReadOnlyDictionary<string, AdditionalCountRule> MeaiAdditionalCounts =
        new Dictionary<string, AdditionalCountRule>
        {
            ["InputTokenDetails.AudioTokenCount"] = new(
                Dim.InputAudio,
                Unit.Tokens,
                Relation.SubsetOfParent,
                Dim.Input
            ),
            ["OutputTokenDetails.AudioTokenCount"] = new(
                Dim.OutputAudio,
                Unit.Tokens,
                Relation.SubsetOfParent,
                Dim.Output
            ),
            ["OutputTokenDetails.AcceptedPredictionTokenCount"] = new(
                Dim.PredictionAccepted,
                Unit.Tokens,
                Relation.SubsetOfParent,
                Dim.Output
            ),
            ["OutputTokenDetails.RejectedPredictionTokenCount"] = new(
                Dim.PredictionRejected,
                Unit.Tokens,
                Relation.SubsetOfParent,
                Dim.Output
            ),
        };

    public static void Read(AdapterContext ctx, params string[] extraUsageKeys)
    {
        if (
            ctx.Wire.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].ValueKind == JsonValueKind.Object
            && choices[0].TryGetProperty("finish_reason", out var fr)
            && fr.ValueKind == JsonValueKind.String
        )
        {
            ctx.NativeStopReason = fr.GetString();
        }

        if (ctx.ReadObject(ctx.Wire, "usage", "$") is not { } usage)
        {
            if (!ctx.Wire.TryGetProperty("usage", out var u) || u.ValueKind == JsonValueKind.Null)
            {
                ctx.Report(
                    Severity.Warning,
                    "wire-usage-absent",
                    "$.usage",
                    "The response body carried no usage object; counts stay as reported by the normalized layer"
                );
            }

            return;
        }
        void Obs(
            string dim,
            long? v,
            Relation rel,
            string? parent,
            string path,
            string? note = null
        )
        {
            if (v is { } n)
            {
                ctx.Usage.Observe(
                    dim,
                    n,
                    Unit.Tokens,
                    rel,
                    parent,
                    Origin.Native,
                    "wire:" + path,
                    note
                );
            }
        }
        const string U = "$.usage";
        Obs(
            Dim.Input,
            ctx.ReadCount(usage, "prompt_tokens", U),
            Relation.Total,
            null,
            "usage.prompt_tokens"
        );
        Obs(
            Dim.Output,
            ctx.ReadCount(usage, "completion_tokens", U),
            Relation.Total,
            null,
            "usage.completion_tokens"
        );
        Obs(
            Dim.Total,
            ctx.ReadCount(usage, "total_tokens", U),
            Relation.Total,
            null,
            "usage.total_tokens"
        );
        string[] pd = ["cached_tokens", "audio_tokens", "cache_write_tokens"],
            cd =

            [
                "reasoning_tokens",
                "audio_tokens",
                "accepted_prediction_tokens",
                "rejected_prediction_tokens",
            ];
        if (ctx.ReadObject(usage, "prompt_tokens_details", U) is { } p)
        {
            var P = U + ".prompt_tokens_details";
            Obs(
                Dim.Cached,
                ctx.ReadCount(p, "cached_tokens", P),
                Relation.SubsetOfParent,
                Dim.Input,
                "usage.prompt_tokens_details.cached_tokens"
            );
            Obs(
                Dim.InputAudio,
                ctx.ReadCount(p, "audio_tokens", P),
                Relation.SubsetOfParent,
                Dim.Input,
                "usage.prompt_tokens_details.audio_tokens"
            );
            Obs(
                Dim.CacheWrite,
                ctx.ReadCount(p, "cache_write_tokens", P),
                Relation.SubsetOfParent,
                Dim.Input,
                "usage.prompt_tokens_details.cache_write_tokens",
                "unadjusted write count as reported by the provider"
            );
            ctx.ReportUnknownFields(p, P, pd);
        }
        if (ctx.ReadObject(usage, "completion_tokens_details", U) is { } c)
        {
            var C = U + ".completion_tokens_details";
            Obs(
                Dim.Reasoning,
                ctx.ReadCount(c, "reasoning_tokens", C),
                Relation.SubsetOfParent,
                Dim.Output,
                "usage.completion_tokens_details.reasoning_tokens"
            );
            Obs(
                Dim.OutputAudio,
                ctx.ReadCount(c, "audio_tokens", C),
                Relation.SubsetOfParent,
                Dim.Output,
                "usage.completion_tokens_details.audio_tokens"
            );
            Obs(
                Dim.PredictionAccepted,
                ctx.ReadCount(c, "accepted_prediction_tokens", C),
                Relation.SubsetOfParent,
                Dim.Output,
                "usage.completion_tokens_details.accepted_prediction_tokens"
            );
            Obs(
                Dim.PredictionRejected,
                ctx.ReadCount(c, "rejected_prediction_tokens", C),
                Relation.SubsetOfParent,
                Dim.Output,
                "usage.completion_tokens_details.rejected_prediction_tokens",
                "still counted within billed completion tokens"
            );
            ctx.ReportUnknownFields(c, C, cd);
        }
        ctx.ReportUnknownFields(
            usage,
            U,
            [
                "prompt_tokens",
                "completion_tokens",
                "total_tokens",
                "prompt_tokens_details",
                "completion_tokens_details",
                .. extraUsageKeys,
            ]
        );
    }
}
