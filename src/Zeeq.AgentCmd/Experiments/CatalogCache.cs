using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Zeeq.AgentCmd;

/// <summary>
/// Freshness and size limits for the process-local snapshot cache. These are starting values, not an
/// update-delivery guarantee across nodes.
/// </summary>
/// <remarks>
/// The latest snapshot uses an absolute lifetime measured from the moment it was loaded, so a stale value
/// cannot extend itself. Pinned revisions are immutable, so they may live longer without changing meaning.
/// </remarks>
public sealed record ExperimentCacheOptions
{
    /// <summary>Absolute lifetime of the latest snapshot for a partition. Defaults to 30 seconds.</summary>
    public TimeSpan LatestDefinitionLifetime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Lifetime of an explicitly requested immutable revision. Defaults to 30 minutes.</summary>
    public TimeSpan PinnedRevisionLifetime { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Upper bound on cached partitions and revisions. When full, expired entries are purged, and any load that
    /// still cannot be stored runs uncached rather than evicting a live entry.
    /// </summary>
    public int MaxEntries { get; init; } = 1024;

    internal void Validate()
    {
        if (
            LatestDefinitionLifetime <= TimeSpan.Zero
            || LatestDefinitionLifetime > TimeSpan.FromHours(1)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(LatestDefinitionLifetime),
                "Latest lifetime must be positive and at most one hour."
            );
        }

        if (
            PinnedRevisionLifetime <= TimeSpan.Zero
            || PinnedRevisionLifetime > TimeSpan.FromDays(1)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(PinnedRevisionLifetime),
                "Pinned lifetime must be positive and at most one day."
            );
        }

        if (MaxEntries is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxEntries),
                "Entry limit must be between 1 and 100,000."
            );
        }
    }
}

/// <summary>
/// Process-local store of validated snapshots. It coalesces concurrent misses and expires entries on an absolute clock.
/// </summary>
/// <remarks>
/// <para>
/// This is a custom, process-local store. It is not <c>IMemoryCache</c> or <c>HybridCache</c>. HybridCache's
/// coalesced-miss path deserializes stored values with its default reflection-based System.Text.Json serializer,
/// even for memory-only entries, and that path throws for the closed <see cref="SourceResult{TRelease}"/> hierarchy.
/// <c>IMemoryCache</c> needs no serializer, but it has no per-key single-flight, and using it would add a package to
/// this dependency-light core. The decorator, <see cref="CachedExperimentSnapshotSource"/>, is the pluggable seam:
/// any <see cref="IExperimentSnapshotSource"/> can be wrapped, or a host can skip the cache entirely.
/// </para>
/// <para>
/// <see cref="ExperimentCacheOptions.MaxEntries"/> is a hard bound. A slot is reserved before insertion, so parallel
/// misses for distinct keys cannot exceed it.
/// </para>
/// <para>
/// Only <see cref="SourceResult{TRelease}.Found"/> and <see cref="SourceResult{TRelease}.NoOverride"/> answers are
/// kept. A failure or an invalid value is removed as soon as it completes, so a transient outage cannot pin an error.
/// Caller cancellation affects only that caller. The shared load continues under its own deadline, so one cancelled
/// caller cannot cancel the load for others.
/// </para>
/// </remarks>
public sealed class ExperimentSnapshotCache
{
    readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly ExperimentCacheOptions _options;

    // Reserved slots, including loads still in flight. It bounds MaxEntries exactly, unlike a racy Count check.
    int _entryCount;

    /// <summary>Creates an empty store.</summary>
    /// <param name="options">Lifetimes and bounds; null uses defaults.</param>
    public ExperimentSnapshotCache(ExperimentCacheOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }

    /// <summary>Returns a cached answer, or loads one and caches it when it is cacheable.</summary>
    /// <param name="sourceId">Namespace of the source. Two sources sharing one store must use different identifiers.</param>
    /// <param name="lookup">Authorized lookup. Its partition is hashed in the key.</param>
    /// <param name="loadTimeout">Deadline for the shared load, independent of any caller's token.</param>
    /// <param name="load">Loads the answer. Its result must already be validated.</param>
    /// <param name="cancellationToken">Caller cancellation. It stops this caller's wait, not the shared load.</param>
    /// <returns>The source answer.</returns>
    public async ValueTask<SourceResult<ExperimentSnapshot>> GetOrLoadAsync(
        string sourceId,
        ExperimentLookup lookup,
        TimeSpan loadTimeout,
        Func<CancellationToken, ValueTask<SourceResult<ExperimentSnapshot>>> load,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(load);
        var lifetime = lookup.RequestedRevision is null
            ? _options.LatestDefinitionLifetime
            : _options.PinnedRevisionLifetime;
        var key = KeyFor(sourceId, lookup);

        var entry = Acquire(key, Stopwatch.GetTimestamp());
        if (entry is null)
        {
            // The store is full of live entries. Load without storing so no live entry is evicted. The caller still
            // waits on its own token, so cancellation is observed here as it is on the shared path.
            var uncached = RunAsync(null, lifetime, loadTimeout, load);
            return await uncached.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var shared = entry.Start(() => RunAsync(entry, lifetime, loadTimeout, load));
        return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    static string KeyFor(string sourceId, ExperimentLookup lookup)
    {
        // The source identifier excludes colons, and the partition is hashed, so the segments cannot be ambiguous.
        // The fixed "rev:" prefix keeps a revision named "latest" from sharing the latest publication's entry.
        var partition = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(lookup.Partition))
        );
        var revision = lookup.RequestedRevision is { } requested
            ? "rev:" + requested.Value
            : "latest";
        return $"agentcmd:experiments:{sourceId}:snapshot:{partition}:{revision}";
    }

    Entry? Acquire(string key, long now)
    {
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.IsLive(now))
                {
                    return existing;
                }

                Remove(new KeyValuePair<string, Entry>(key, existing));
                continue;
            }

            // Reserve the slot before inserting. Checking Count and then adding would let parallel misses overshoot.
            if (!TryReserve(now))
            {
                return null;
            }

            var created = new Entry();
            if (_entries.TryAdd(key, created))
            {
                return created;
            }

            // Another caller added this key first. Give the reserved slot back and read the winner.
            Interlocked.Decrement(ref _entryCount);
        }
    }

    bool TryReserve(long now)
    {
        if (Interlocked.Increment(ref _entryCount) <= _options.MaxEntries)
        {
            return true;
        }

        Interlocked.Decrement(ref _entryCount);
        PurgeExpired(now);
        if (Interlocked.Increment(ref _entryCount) <= _options.MaxEntries)
        {
            return true;
        }

        Interlocked.Decrement(ref _entryCount);
        return false;
    }

    void PurgeExpired(long now)
    {
        foreach (var pair in _entries)
        {
            if (!pair.Value.IsLive(now))
            {
                Remove(pair);
            }
        }
    }

    void Remove(KeyValuePair<string, Entry> pair)
    {
        // Only the caller whose removal succeeds releases the slot, so the count never drifts.
        if (_entries.TryRemove(pair))
        {
            Interlocked.Decrement(ref _entryCount);
        }
    }

    static async Task<SourceResult<ExperimentSnapshot>> RunAsync(
        Entry? entry,
        TimeSpan lifetime,
        TimeSpan loadTimeout,
        Func<CancellationToken, ValueTask<SourceResult<ExperimentSnapshot>>> load
    )
    {
        try
        {
            using var deadline = new CancellationTokenSource(loadTimeout);
            var result = await load(deadline.Token)
                .AsTask()
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
            entry?.Complete(IsCacheable(result), lifetime);
            return result;
        }
        catch (Exception)
        {
            entry?.Complete(false, lifetime);
            throw;
        }
    }

    static bool IsCacheable(SourceResult<ExperimentSnapshot> result) =>
        result
            is SourceResult<ExperimentSnapshot>.Found
                or SourceResult<ExperimentSnapshot>.NoOverride;

    /// <summary>One key's shared load and its expiry. Expiry is written once when the load completes.</summary>
    sealed class Entry
    {
        readonly object _gate = new();
        Task<SourceResult<ExperimentSnapshot>>? _task;
        long _expiresAt = long.MaxValue;

        /// <summary>Starts the shared load once and returns the same task to every concurrent caller.</summary>
        public Task<SourceResult<ExperimentSnapshot>> Start(
            Func<Task<SourceResult<ExperimentSnapshot>>> start
        )
        {
            lock (_gate)
            {
                return _task ??= start();
            }
        }

        /// <summary>Records the expiry for a cacheable result. A non-cacheable result expires immediately.</summary>
        public void Complete(bool cacheable, TimeSpan lifetime)
        {
            var expiresAt = cacheable
                ? Stopwatch.GetTimestamp() + (long)(lifetime.TotalSeconds * Stopwatch.Frequency)
                : 0;
            Volatile.Write(ref _expiresAt, expiresAt);
        }

        /// <summary>True while the load is running or its cacheable answer has not yet expired.</summary>
        public bool IsLive(long now)
        {
            Task<SourceResult<ExperimentSnapshot>>? task;
            lock (_gate)
            {
                task = _task;
            }

            return task is null || !task.IsCompleted || now < Volatile.Read(ref _expiresAt);
        }
    }
}

/// <summary>
/// Caches validated snapshots from an inner source, keyed by a required source namespace, partition, and revision.
/// </summary>
/// <remarks>
/// The cache stores the whole snapshot, never one prompt or route. Caching separate prompt and route reads could
/// return a torn publication. Invalid values are validated before they reach the store, so they are never stored
/// as successful releases.
/// </remarks>
public sealed class CachedExperimentSnapshotSource : IExperimentSnapshotSource
{
    readonly string _sourceId;
    readonly IExperimentSnapshotSource _inner;
    readonly ExperimentSnapshotCache _cache;
    readonly ExperimentCatalogOptions _catalogOptions;

    /// <summary>Wraps a snapshot source with a process-local cache.</summary>
    /// <param name="sourceId">
    /// Stable namespace for this source, 1 to 64 letters, digits, periods, underscores, or hyphens. Two sources that
    /// share one <paramref name="cache"/> must use different identifiers.
    /// </param>
    /// <param name="inner">The uncached source, such as a host store or a file-backed source.</param>
    /// <param name="cache">Shared store; null creates a store private to this source.</param>
    /// <param name="catalogOptions">Validation limits and the load deadline; null uses defaults.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceId"/> is outside the allowed grammar.</exception>
    public CachedExperimentSnapshotSource(
        string sourceId,
        IExperimentSnapshotSource inner,
        ExperimentSnapshotCache? cache = null,
        ExperimentCatalogOptions? catalogOptions = null
    )
    {
        if (
            string.IsNullOrEmpty(sourceId)
            || sourceId.Length > 64
            || !sourceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
        )
        {
            throw new ArgumentException(
                "Source identifier must be 1 to 64 letters, digits, periods, underscores, or hyphens.",
                nameof(sourceId)
            );
        }

        ArgumentNullException.ThrowIfNull(inner);
        _sourceId = sourceId;
        _inner = inner;
        _cache = cache ?? new ExperimentSnapshotCache();
        _catalogOptions = catalogOptions ?? new();
        _catalogOptions.Validate();
    }

    /// <inheritdoc/>
    public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
        ExperimentLookup lookup,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(lookup);
        return _cache.GetOrLoadAsync(
            _sourceId,
            lookup,
            _catalogOptions.SourceTimeout,
            async token =>
            {
                var fetched = await _inner.GetAsync(lookup, token).ConfigureAwait(false);
                return ExperimentSnapshotValidator.Normalize(
                    lookup,
                    fetched,
                    _catalogOptions.MaxTemplateCharacters
                );
            },
            cancellationToken
        );
    }
}
