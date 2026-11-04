using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Zeeq.AgentCmd;

/// <summary>
/// Deterministic weighted assignment over a host-supplied stable unit key.
/// </summary>
/// <remarks>
/// The same experiment, weights, and unit key always produce the same variant, in any process, so
/// assignment can be repeated offline and audited. The hash is SHA-256, not <c>string.GetHashCode</c>,
/// which is randomized per process. Trace or request identifiers are never an input, because a per-request
/// value would reshuffle assignment on every call. Changing weights moves units between variants.
/// </remarks>
public sealed class WeightedExperimentSelector : IExperimentSelector
{
    /// <summary>Chooses a variant from the context's unit key and the view's weights.</summary>
    /// <param name="context">Selection context with a host-supplied unit key.</param>
    /// <param name="cancellationToken">Unused; selection is synchronous.</param>
    /// <returns>The weighted variant, the control variant when no unit key is present, or the configured default when every weight is zero.</returns>
    public ValueTask<ExperimentSelection> SelectAsync(
        ExperimentSelectionContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var view = context.View;

        // Without a stable unit key, no unit can be assigned consistently. The control arm is the safe default.
        if (string.IsNullOrEmpty(context.UnitId))
        {
            return ValueTask.FromResult<ExperimentSelection>(
                new ExperimentSelection.Variant(view.ControlVariantId)
            );
        }

        ulong totalWeight = 0;
        foreach (var variant in view.Variants.Values)
        {
            totalWeight += (ulong)variant.Weight;
        }

        if (totalWeight == 0)
        {
            return ValueTask.FromResult<ExperimentSelection>(
                new ExperimentSelection.UseConfiguredDefault()
            );
        }

        var digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(view.ExperimentId + "\n" + context.UnitId)
        );
        var sample = BinaryPrimitives.ReadUInt64BigEndian(digest) % totalWeight;

        // Sorting by identifier makes the cumulative walk independent of dictionary enumeration order.
        ulong cumulative = 0;
        foreach (
            var variant in view.Variants.Values.OrderBy(v => v.VariantId, StringComparer.Ordinal)
        )
        {
            cumulative += (ulong)variant.Weight;
            if (variant.Weight > 0 && sample < cumulative)
            {
                return ValueTask.FromResult<ExperimentSelection>(
                    new ExperimentSelection.Variant(variant.VariantId)
                );
            }
        }

        return ValueTask.FromResult<ExperimentSelection>(
            new ExperimentSelection.UseConfiguredDefault()
        );
    }
}
