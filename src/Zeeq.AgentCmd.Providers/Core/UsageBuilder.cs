namespace Zeeq.AgentCmd.Providers;

/// <summary>
/// Request-local accumulator of observations. One dimension holds one measurement; aliases from several origins are merged as evidence.
/// A disagreement is kept as a conflict. Nothing is summed across dimensions unless an adapter asks for an explicit sum check.
/// </summary>
public sealed class UsageBuilder
{
    sealed class Entry(Unit unit, Relation relation, string? parent)
    {
        public Unit Unit = unit;
        public Relation Relation = relation;
        public string? Parent = parent;
        public List<Observation> Observations = [];
    }

    readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    readonly List<string> order = [];
    readonly List<UsageConflict> conflicts = [];

    /// <summary>Whether no usage dimensions have been observed.</summary>
    public bool IsEmpty => entries.Count == 0;

    /// <summary>Adds a non-negative source observation, retaining independent reports as evidence.</summary>
    public void Observe(
        string dimension,
        long value,
        Unit unit,
        Relation relation,
        string? parent,
        Origin origin,
        string path,
        string? note = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (!entries.TryGetValue(dimension, out var entry))
        {
            entries[dimension] = entry = new Entry(unit, relation, parent);
            order.Add(dimension);
        }
        else if (entry.Unit != unit)
        {
            throw new InvalidOperationException(
                $"Dimension '{dimension}' was declared with unit {entry.Unit}, not {unit}"
            );
        }

        if (!entry.Observations.Any(o => o.Origin == origin && o.Path == path))
        {
            entry.Observations.Add(new(origin, path, value, note));
        }
    }

    /// <summary>The first observed value, which is the normalized value when one exists. Null means unknown.</summary>
    public long? Peek(string dimension) =>
        entries.TryGetValue(dimension, out var e) ? e.Observations[0].Value : null;

    /// <summary>Records a conflict unless the parent and every part are known. A missing part is never replaced by a residual.</summary>
    public void CheckSum(
        string parent,
        IReadOnlyList<string> parts,
        ConflictKind kind = ConflictKind.PartitionSumMismatch
    )
    {
        if (Peek(parent) is not { } expected || parts.Any(p => Peek(p) is null))
        {
            return;
        }

        var sum = parts.Sum(p => Peek(p)!.Value);
        if (sum == expected)
        {
            return;
        }

        var obs = entries[parent]
            .Observations.Concat(parts.SelectMany(p => entries[p].Observations))
            .Append(
                new Observation(Origin.Computed, $"computed:sum({string.Join('+', parts)})", sum)
            )
            .ToArray();
        conflicts.Add(
            new(parent, kind, $"{string.Join(" + ", parts)} = {sum} but {parent} = {expected}", obs)
        );
    }

    internal UsageBuilder Clone()
    {
        var copy = new UsageBuilder();
        foreach (var d in order)
        {
            var e = entries[d];
            copy.entries[d] = new Entry(e.Unit, e.Relation, e.Parent)
            {
                Observations = [.. e.Observations],
            };
            copy.order.Add(d);
        }
        copy.conflicts.AddRange(conflicts);
        return copy;
    }

    internal UsageSnapshot Build()
    {
        var all = new List<UsageConflict>(conflicts);
        foreach (var d in order)
        {
            var e = entries[d];
            if (e.Observations.Select(o => o.Value).Distinct().Count() > 1)
            {
                all.Add(
                    new(
                        d,
                        ConflictKind.ValueMismatch,
                        $"Observations of {d} disagree: {string.Join(", ", e.Observations.Select(o => $"{o.Path}={o.Value}"))}",
                        [.. e.Observations]
                    )
                );
            }

            if (
                e.Relation is Relation.SubsetOfParent or Relation.PartOfParent
                && e.Parent is not null
                && Peek(e.Parent) is { } parent
                && e.Observations[0].Value > parent
            )
            {
                all.Add(
                    new(
                        d,
                        ConflictKind.SubsetExceedsParent,
                        $"{d} = {e.Observations[0].Value} exceeds its parent {e.Parent} = {parent}",
                        [.. e.Observations]
                    )
                );
            }
        }
        var conflicted = all.Select(c => c.Dimension).ToHashSet(StringComparer.Ordinal);
        var measurements = order
            .Select(d =>
            {
                var e = entries[d];
                return new UsageMeasurement(
                    d,
                    e.Observations[0].Value,
                    e.Unit,
                    e.Relation,
                    e.Parent,
                    conflicted.Contains(d)
                        ? MeasurementStatus.Conflicted
                        : MeasurementStatus.Agreed,
                    [.. e.Observations]
                );
            })
            .ToArray();
        return new UsageSnapshot(measurements, [.. all]);
    }
}
