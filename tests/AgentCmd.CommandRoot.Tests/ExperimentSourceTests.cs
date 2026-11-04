using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

/// <summary>
/// Offline coverage for experiment snapshot sources, validation, assignment, the weighted selector, and the cache.
/// No provider or secret is contacted.
/// </summary>
public class ExperimentSourceTests
{
    const string CommandId = "invoice.extract";
    const string Signature = "sig-1";

    [Test]
    public async Task LookupAcceptsOpaqueTenantTextAndRejectsBlankPartitions()
    {
        await Assert
            .That(ExperimentLookup.TryCreate("tenant/with:punct 1", null, out var lookup))
            .IsTrue();
        await Assert.That(lookup!.Partition).IsEqualTo("tenant/with:punct 1");
        await Assert.That(ExperimentLookup.TryCreate("   ", null, out _)).IsFalse();
        await Assert
            .That(
                ExperimentLookup.TryCreate(
                    new string('x', ExperimentLookup.MaxPartitionLength + 1),
                    null,
                    out _
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task ControlVariantIsChosenWhenNoSelectorIsConfigured()
    {
        var resolver = new ExperimentResolver(
            Serving(Publish(("control", 1, "control user"), ("treatment", 1, "treatment user")))
        );

        var resolution = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        await Assert.That(VariantOf(resolution)).IsEqualTo("control");
        await Assert.That(Treatment(resolution).Role).IsEqualTo(ExperimentRole.Primary);
    }

    [Test]
    public async Task SourceTimeoutSelectsConfiguredDefault()
    {
        // The source ignores its token. The resolver must still enforce the deadline.
        var source = new FakeSnapshotSource(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return new SourceResult<ExperimentSnapshot>.NoOverride();
            }
        );
        var resolver = new ExperimentResolver(
            source,
            new() { SourceTimeout = TimeSpan.FromMilliseconds(50) }
        );

        var resolution = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.Timeout);
    }

    [Test]
    public async Task SourceExceptionSelectsDefaultWithoutExposingItsText()
    {
        var source = new FakeSnapshotSource(
            (_, _) => throw new InvalidOperationException("secret-token-xyz")
        );
        var resolver = new ExperimentResolver(source);

        var resolution = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        // The reason is a closed enum, so no source text can reach evidence through the result.
        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.Unavailable);
    }

    [Test]
    public async Task CallerCancellationPropagatesInsteadOfSelectingDefault()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSnapshotSource(
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new SourceResult<ExperimentSnapshot>.NoOverride();
            }
        );
        var resolver = new ExperimentResolver(source);

        var threw = false;
        try
        {
            await resolver.ResolveAsync(
                Lookup(),
                Fallback(),
                Context(),
                null,
                null,
                cancellation.Token
            );
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task SourceAuthorizationDenialSelectsDefaultWithItsOwnReason()
    {
        var source = new FakeSnapshotSource(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Unavailable(
                        SourceFailure.AuthorizationDenied
                    )
                )
        );
        var resolver = new ExperimentResolver(source);

        var resolution = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        await Assert
            .That(ReasonOf(resolution))
            .IsEqualTo(ExperimentFallbackReason.SourceAuthorizationDenied);
    }

    [Test]
    public async Task ExplicitNoOverrideAndMissingCommandSelectDefaultForThatCommandOnly()
    {
        var noOverride = new ExperimentResolver(
            new FakeSnapshotSource(
                (_, _) =>
                    ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                        new SourceResult<ExperimentSnapshot>.NoOverride()
                    )
            )
        );
        var missing = new ExperimentResolver(Serving(Publish(("control", 1, "user"))));
        var otherCommand = Fallback() with { CommandId = "other.command" };

        var explicitNone = await noOverride.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );
        var absentCommand = await missing.ResolveAsync(
            Lookup(),
            otherCommand,
            Context(),
            null,
            null,
            default
        );

        await Assert.That(ReasonOf(explicitNone)).IsEqualTo(ExperimentFallbackReason.NoOverride);
        await Assert.That(ReasonOf(absentCommand)).IsEqualTo(ExperimentFallbackReason.NoOverride);
    }

    [Test]
    public async Task ContractSignatureChangeSelectsDefaultInsteadOfTreatment()
    {
        var resolver = new ExperimentResolver(Serving(Publish(("control", 1, "user"))));

        var resolution = await resolver.ResolveAsync(
            Lookup(),
            Fallback("sig-2"),
            Context(),
            null,
            null,
            default
        );

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task RequestedRevisionMustMatchThePublishedRevision()
    {
        // The published snapshot is revision r1, so a lookup for r2 must not use it.
        var resolver = new ExperimentResolver(Serving(Publish(("control", 1, "user"))));

        var resolution = await resolver.ResolveAsync(
            Lookup(revision: "r2"),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task InvalidSnapshotsAreRejectedWholeWithoutPartialUse()
    {
        // Each snapshot breaks one rule: missing control, negative weight, removed user template, empty text.
        var cases = new Func<ExperimentSnapshot>[]
        {
            () => PublishWith("missing", userRemoved: false, ("treatment", 1, "user")),
            () => Publish(("control", -1, "user")),
            () => PublishWith("control", userRemoved: true, ("control", 1, "user")),
            () => Publish(("control", 1, "")),
        };

        foreach (var build in cases)
        {
            var resolver = new ExperimentResolver(Serving(build()));

            var resolution = await resolver.ResolveAsync(
                Lookup(),
                Fallback(),
                Context(),
                null,
                null,
                default
            );

            await Assert
                .That(ReasonOf(resolution))
                .IsEqualTo(ExperimentFallbackReason.InvalidRelease);
        }
    }

    [Test]
    public async Task SelectorFailuresAndUnknownVariantsSelectDefault()
    {
        var unknown = new ExperimentResolver(
            Serving(Publish(("control", 1, "user"))),
            selector: new ReturningSelector(new ExperimentSelection.Variant("missing"))
        );
        var chooseDefault = new ExperimentResolver(
            Serving(Publish(("control", 1, "user"))),
            selector: new ReturningSelector(new ExperimentSelection.UseConfiguredDefault())
        );
        var throwing = new ExperimentResolver(
            Serving(Publish(("control", 1, "user"))),
            selector: new ThrowingSelector()
        );

        await Assert
            .That(
                ReasonOf(
                    await unknown.ResolveAsync(Lookup(), Fallback(), Context(), null, null, default)
                )
            )
            .IsEqualTo(ExperimentFallbackReason.UnknownVariant);
        await Assert
            .That(
                ReasonOf(
                    await chooseDefault.ResolveAsync(
                        Lookup(),
                        Fallback(),
                        Context(),
                        null,
                        null,
                        default
                    )
                )
            )
            .IsEqualTo(ExperimentFallbackReason.SelectorChoseDefault);
        await Assert
            .That(
                ReasonOf(
                    await throwing.ResolveAsync(
                        Lookup(),
                        Fallback(),
                        Context(),
                        null,
                        null,
                        default
                    )
                )
            )
            .IsEqualTo(ExperimentFallbackReason.SelectorFailed);
    }

    [Test]
    public async Task WeightedSelectorIsDeterministicHonorsWeightsAndUsesControlWithoutUnit()
    {
        var snapshot = Publish(("control", 7, "user"), ("treatment", 3, "user"));
        var resolver = new ExperimentResolver(
            Serving(snapshot),
            selector: new WeightedExperimentSelector()
        );

        var first = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            "unit-1",
            default
        );
        var repeat = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            "unit-1",
            default
        );
        var withoutUnit = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        await Assert.That(VariantOf(first)).IsEqualTo(VariantOf(repeat));
        await Assert.That(VariantOf(withoutUnit)).IsEqualTo("control");

        var treatments = 0;
        for (var unit = 0; unit < 2000; unit++)
        {
            var resolution = await resolver.ResolveAsync(
                Lookup(),
                Fallback(),
                Context(),
                null,
                $"unit-{unit}",
                default
            );
            if (VariantOf(resolution) == "treatment")
            {
                treatments++;
            }
        }

        // Expected 600 of 2000 with a 30% weight. The bounds are wide enough for a fixed hash distribution.
        await Assert.That(treatments).IsGreaterThan(520);
        await Assert.That(treatments).IsLessThan(680);
    }

    [Test]
    public async Task ZeroTotalWeightSelectsConfiguredDefault()
    {
        var resolver = new ExperimentResolver(
            Serving(Publish(("control", 0, "user"))),
            selector: new WeightedExperimentSelector()
        );

        var resolution = await resolver.ResolveAsync(
            Lookup(),
            Fallback(),
            Context(),
            null,
            "unit-1",
            default
        );

        await Assert
            .That(ReasonOf(resolution))
            .IsEqualTo(ExperimentFallbackReason.SelectorChoseDefault);
    }

    [Test]
    public async Task SnapshotIsFrozenAgainstSourceMutationAndDisposedJsonDocuments()
    {
        var variants = new Dictionary<string, ExperimentVariant>
        {
            ["control"] = Variant("control", 1, "user", "fr-CA"),
        };
        var commands = new Dictionary<string, ExperimentCatalogView>
        {
            [CommandId] = new(CommandId, 1, Signature, Rev("r1"), "exp-1", "control", variants),
        };
        var published = new ExperimentSnapshot(Rev("r1"), commands);
        var resolver = new ExperimentResolver(Serving(published));

        var loaded = await resolver.LoadAsync(Lookup(), default);
        // The source keeps mutating its own collections after publication.
        variants.Clear();
        commands.Clear();
        var assignment = await resolver.AssignAsync(
            ((ExperimentSnapshotResult.Loaded)loaded).Snapshot,
            Lookup(),
            Fallback(),
            Context(),
            null,
            null,
            default
        );

        await Assert.That(Treatment(assignment).View.Variants.Count).IsEqualTo(1);
        await Assert
            .That(Treatment(assignment).Variant.ParameterValues["Locale"].GetString())
            .IsEqualTo("fr-CA");
    }

    [Test]
    public async Task ParameterValuesSurviveDisposalOfTheSourceDocument()
    {
        using var document = JsonDocument.Parse("\"fr-CA\"");
        var variants = new Dictionary<string, ExperimentVariant>
        {
            ["control"] = new(
                "control",
                1,
                new(new TemplateChange.Inherited(), new TemplateChange.Replaced("user")),
                new Dictionary<string, JsonElement> { ["Locale"] = document.RootElement },
                null
            ),
        };
        var published = new ExperimentSnapshot(
            Rev("r1"),
            new Dictionary<string, ExperimentCatalogView>
            {
                [CommandId] = new(CommandId, 1, Signature, Rev("r1"), "exp-1", "control", variants),
            }
        );
        var resolver = new ExperimentResolver(Serving(published));
        var loaded = await resolver.LoadAsync(Lookup(), default);

        // The document is disposed here. The cloned value must still read correctly.
        document.Dispose();
        var value = ((ExperimentSnapshotResult.Loaded)loaded)
            .Snapshot
            .Commands[CommandId]
            .Variants["control"]
            .ParameterValues["Locale"];

        await Assert.That(value.GetString()).IsEqualTo("fr-CA");
    }

    [Test]
    public async Task ConcurrentMissesShareOneSourceCall()
    {
        var source = new FakeSnapshotSource(
            async (lookup, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                return new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "user")));
            }
        );
        var cached = Cached(source);

        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => cached.GetAsync(Lookup(), default).AsTask())
        );

        await Assert.That(source.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task LatestEntryExpiresAfterItsAbsoluteLifetime()
    {
        var source = new FakeSnapshotSource(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "user")))
                )
        );
        var cached = Cached(source, latest: TimeSpan.FromMilliseconds(200));

        await cached.GetAsync(Lookup(), default);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await cached.GetAsync(Lookup(), default);

        await Assert.That(source.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task UnavailableAndInvalidAnswersAreNotCached()
    {
        var calls = 0;
        var source = new FakeSnapshotSource(
            (_, _) =>
            {
                calls++;
                var result = calls switch
                {
                    1 => (SourceResult<ExperimentSnapshot>)
                        new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable),
                    2 => new SourceResult<ExperimentSnapshot>.Found(
                        Publish(("control", -5, "user"))
                    ),
                    _ => new SourceResult<ExperimentSnapshot>.Found(
                        Publish(("control", 1, "user"))
                    ),
                };
                return ValueTask.FromResult(result);
            }
        );
        var cached = Cached(source);

        var first = await cached.GetAsync(Lookup(), default);
        var second = await cached.GetAsync(Lookup(), default);
        var third = await cached.GetAsync(Lookup(), default);

        await Assert.That(first is SourceResult<ExperimentSnapshot>.Unavailable).IsTrue();
        await Assert.That(second is SourceResult<ExperimentSnapshot>.Invalid).IsTrue();
        await Assert.That(third is SourceResult<ExperimentSnapshot>.Found).IsTrue();
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task PartitionsAreCachedSeparately()
    {
        var source = new FakeSnapshotSource(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "user")))
                )
        );
        var cached = Cached(source);

        await cached.GetAsync(Lookup("tenant-a"), default);
        await cached.GetAsync(Lookup("tenant-b"), default);
        await cached.GetAsync(Lookup("tenant-a"), default);

        await Assert.That(source.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task TwoSourcesSharingOneCacheDoNotShareEntries()
    {
        var cache = new ExperimentSnapshotCache();
        var first = new FakeSnapshotSource(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "first")))
                )
        );
        var second = new FakeSnapshotSource(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "second")))
                )
        );
        var a = Cached(first, cache: cache, sourceId: "source-a");
        var b = Cached(second, cache: cache, sourceId: "source-b");

        var fromA = await a.GetAsync(Lookup(), default);
        var fromB = await b.GetAsync(Lookup(), default);

        // Same partition and revision, one shared HybridCache: each source must answer from its own namespace.
        await Assert.That(first.Calls).IsEqualTo(1);
        await Assert.That(second.Calls).IsEqualTo(1);
        await Assert
            .That(
                ((SourceResult<ExperimentSnapshot>.Found)fromA)
                    .Release
                    .Commands[CommandId]
                    .Variants["control"]
                    .Prompt
                    .User
                    is TemplateChange.Replaced { Text: "first" }
            )
            .IsTrue();
        await Assert
            .That(
                ((SourceResult<ExperimentSnapshot>.Found)fromB)
                    .Release
                    .Commands[CommandId]
                    .Variants["control"]
                    .Prompt
                    .User
                    is TemplateChange.Replaced { Text: "second" }
            )
            .IsTrue();
    }

    [Test]
    public async Task HostileSourcePayloadsSelectDefaultWithoutThrowing()
    {
        var hostile = new ExperimentSnapshot(Rev("r1"), new HostileCommands());
        var payloads = new Func<SourceResult<ExperimentSnapshot>?>[]
        {
            () => null,
            () => new SourceResult<ExperimentSnapshot>.Found(null!),
            () => new SourceResult<ExperimentSnapshot>.Found(hostile),
        };

        foreach (var payload in payloads)
        {
            var source = new FakeSnapshotSource((_, _) => ValueTask.FromResult(payload()!));
            var resolver = new ExperimentResolver(source);

            var resolution = await resolver.ResolveAsync(
                Lookup(),
                Fallback(),
                Context(),
                null,
                null,
                default
            );

            await Assert
                .That(ReasonOf(resolution))
                .IsEqualTo(ExperimentFallbackReason.InvalidRelease);
        }
    }

    [Test]
    public async Task SelectAsyncChoosesWithoutAConfiguredDefault()
    {
        var resolver = new ExperimentResolver(
            Serving(Publish(("control", 1, "user"), ("treatment", 1, "other"))),
            selector: new ReturningSelector(
                new ExperimentSelection.Variant("treatment", ExperimentRole.Shadow, "group-1")
            )
        );
        var snapshot = (
            (ExperimentSnapshotResult.Loaded)await resolver.LoadAsync(Lookup(), default)
        ).Snapshot;

        var choice = await resolver.SelectAsync(
            Lookup(),
            snapshot.Commands[CommandId],
            Context(),
            null,
            null,
            default
        );

        await Assert
            .That(
                choice
                    is ExperimentChoice.Chosen
                    {
                        Assignment.VariantId: "treatment",
                        Assignment.Role: ExperimentRole.Shadow
                    }
            )
            .IsTrue();
    }

    [Test]
    public async Task PreselectPinsEveryCommandWithOneSelectorCallEach()
    {
        var selector = new CountingSelector();
        var resolver = new ExperimentResolver(
            Serving(Publish(("control", 1, "user"), ("treatment", 1, "other"))),
            selector: selector
        );
        var snapshot = (
            (ExperimentSnapshotResult.Loaded)await resolver.LoadAsync(Lookup(), default)
        ).Snapshot;

        var pin = await resolver.PreselectAsync(
            Lookup(),
            snapshot,
            Context(),
            "actor-1",
            "unit-1",
            default
        );

        await Assert.That(selector.Calls).IsEqualTo(1);
        await Assert.That(pin.TryGetChoice(CommandId, out var choice)).IsTrue();
        await Assert
            .That(choice is ExperimentChoice.Chosen { Assignment.VariantId: "treatment" })
            .IsTrue();
    }

    [Test]
    public async Task SourceNamespacesAreValidatedAndBounded()
    {
        var inner = Serving(Publish(("control", 1, "user")));

        var rejected = new[] { "", "a:b", "has space", new string('x', 65) };
        foreach (var id in rejected)
        {
            var threw = false;
            try
            {
                _ = new CachedExperimentSnapshotSource(id, inner);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            await Assert.That(threw).IsTrue();
        }

        // A store with one slot must keep the live entry and run the overflowing load uncached, never evict.
        var counting = new FakeSnapshotSource(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "user")))
                )
        );
        var tiny = new ExperimentSnapshotCache(new ExperimentCacheOptions { MaxEntries = 1 });
        var first = new CachedExperimentSnapshotSource("source-a", counting, tiny);
        var second = new CachedExperimentSnapshotSource("source-b", counting, tiny);

        await first.GetAsync(Lookup("tenant-a"), default); // stored; one inner call
        await second.GetAsync(Lookup("tenant-a"), default); // store full; uncached; one inner call
        await second.GetAsync(Lookup("tenant-a"), default); // uncached again; one inner call
        await first.GetAsync(Lookup("tenant-a"), default); // still cached; no inner call

        await Assert.That(counting.Calls).IsEqualTo(3);
    }

    [Test]
    public async Task FullStoreCancellationIsObservedOnTheUncachedPath()
    {
        // The in-flight load holds the only slot, so the overflowing load runs uncached and must still honour its caller's token.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new FakeSnapshotSource(
            async (_, _) =>
            {
                await gate.Task;
                return new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "user")));
            }
        );
        var overflow = new FakeSnapshotSource(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return new SourceResult<ExperimentSnapshot>.NoOverride();
            }
        );
        var tiny = new ExperimentSnapshotCache(new ExperimentCacheOptions { MaxEntries = 1 });
        var longDeadline = new ExperimentCatalogOptions
        {
            SourceTimeout = TimeSpan.FromSeconds(30),
        };
        var first = new CachedExperimentSnapshotSource("source-a", holder, tiny, longDeadline);
        var second = new CachedExperimentSnapshotSource("source-b", overflow, tiny, longDeadline);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var held = first.GetAsync(Lookup(), default).AsTask();
        var started = DateTimeOffset.UtcNow;
        var threw = false;
        try
        {
            await second.GetAsync(Lookup(), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        finally
        {
            gate.SetResult();
        }

        var elapsed = DateTimeOffset.UtcNow - started;
        await held;

        // Cancellation must end the wait promptly, not at the 30-second load deadline, and must not become a default answer.
        await Assert.That(threw).IsTrue();
        await Assert.That(elapsed < TimeSpan.FromSeconds(5)).IsTrue();
        await Assert.That(overflow.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ParallelDistinctMissesNeverExceedMaxEntries()
    {
        var source = new FakeSnapshotSource(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                return new SourceResult<ExperimentSnapshot>.Found(Publish(("control", 1, "user")));
            }
        );
        var cached = new CachedExperimentSnapshotSource(
            "source-a",
            source,
            new ExperimentSnapshotCache(new ExperimentCacheOptions { MaxEntries = 5 })
        );

        await Task.WhenAll(
            Enumerable
                .Range(0, 200)
                .Select(i => cached.GetAsync(Lookup($"tenant-{i}"), default).AsTask())
        );
        var afterWave = source.Calls;

        // At most MaxEntries partitions can still be cached, so at least 195 of 200 must reach the source again.
        for (var i = 0; i < 200; i++)
        {
            await cached.GetAsync(Lookup($"tenant-{i}"), default);
        }

        await Assert.That(source.Calls - afterWave >= 195).IsTrue();
    }

    [Test]
    public async Task PreselectChoosesOncePerExperimentAndProjectsTheArmOntoEveryCommand()
    {
        // Three commands share exp-1 and one command is in exp-2. A flipping selector would give the shared commands
        // different arms if it were called once per command. Each experiment must get exactly one call.
        var selector = new FlippingSelector();
        var snapshot = GroupedSnapshot(
            ("invoice.header", "exp-1", "control", new[] { ("control", 1), ("treatment", 1) }),
            ("invoice.lines", "exp-1", "control", new[] { ("control", 1), ("treatment", 1) }),
            ("invoice.totals", "exp-1", "control", new[] { ("control", 1), ("treatment", 1) }),
            ("receipt.print", "exp-2", "control", new[] { ("control", 1), ("treatment", 1) })
        );
        var resolver = new ExperimentResolver(Serving(snapshot), selector: selector);

        var pin = await resolver.PreselectAsync(Lookup(), snapshot, Context(), null, null, default);

        // Groups are selected in experiment-identifier order: exp-1 takes control, then exp-2 takes treatment.
        await Assert.That(selector.Calls).IsEqualTo(2);
        foreach (var commandId in new[] { "invoice.header", "invoice.lines", "invoice.totals" })
        {
            var assignment = AssignmentOf(pin, commandId);
            await Assert.That(assignment.VariantId).IsEqualTo("control");
            await Assert.That(assignment.View.CommandId).IsEqualTo(commandId);
            await Assert.That(assignment.View.ExperimentId).IsEqualTo("exp-1");
        }

        await Assert.That(AssignmentOf(pin, "receipt.print").VariantId).IsEqualTo("treatment");
    }

    [Test]
    public async Task IncompatibleArmsFallBackTheWholeGroupWithoutSelecting()
    {
        var sharedArms = new[] { ("control", 1), ("treatment", 1) };
        var unrelated = ("receipt.print", "exp-2", "control", sharedArms);
        var cases = new[]
        {
            // Same names, different weight.
            new[]
            {
                ("invoice.header", "exp-1", "control", sharedArms),
                ("invoice.lines", "exp-1", "control", new[] { ("control", 1), ("treatment", 2) }),
            },
            // Same weights, different arm name.
            new[]
            {
                ("invoice.header", "exp-1", "control", sharedArms),
                ("invoice.lines", "exp-1", "control", new[] { ("control", 1), ("variant-b", 1) }),
            },
            // Same arms, different control.
            new[]
            {
                ("invoice.header", "exp-1", "control", sharedArms),
                ("invoice.lines", "exp-1", "treatment", sharedArms),
            },
        };

        foreach (var group in cases)
        {
            var selector = new FlippingSelector();
            var snapshot = GroupedSnapshot(group.Append(unrelated).ToArray());
            var resolver = new ExperimentResolver(Serving(snapshot), selector: selector);

            var pin = await resolver.PreselectAsync(
                Lookup(),
                snapshot,
                Context(),
                null,
                null,
                default
            );

            // Both members of the incompatible group get the same explicit reason, and no selector call is made for it.
            await Assert
                .That(ReasonFor(pin, "invoice.header"))
                .IsEqualTo(ExperimentFallbackReason.IncompatibleExperimentGroup);
            await Assert
                .That(ReasonFor(pin, "invoice.lines"))
                .IsEqualTo(ExperimentFallbackReason.IncompatibleExperimentGroup);

            // The separate experiment is unaffected and is selected exactly once.
            await Assert.That(selector.Calls).IsEqualTo(1);
            await Assert.That(AssignmentOf(pin, "receipt.print").VariantId).IsEqualTo("control");
        }
    }

    [Test]
    public async Task ConcurrentPreselectsOfOnePublicationEachPinOneArmPerExperiment()
    {
        var selector = new FlippingSelector();
        var snapshot = GroupedSnapshot(
            ("invoice.header", "exp-1", "control", new[] { ("control", 1), ("treatment", 1) }),
            ("invoice.lines", "exp-1", "control", new[] { ("control", 1), ("treatment", 1) }),
            ("invoice.totals", "exp-1", "control", new[] { ("control", 1), ("treatment", 1) })
        );
        var resolver = new ExperimentResolver(Serving(snapshot), selector: selector);

        var pins = await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(_ =>
                    resolver
                        .PreselectAsync(Lookup(), snapshot, Context(), null, null, default)
                        .AsTask()
                )
        );

        // Every pin is coherent, however the concurrent calls interleave the flipping selector.
        foreach (var pin in pins)
        {
            var arms = new[] { "invoice.header", "invoice.lines", "invoice.totals" }
                .Select(id => AssignmentOf(pin, id).VariantId)
                .Distinct()
                .ToArray();
            await Assert.That(arms.Length).IsEqualTo(1);
        }

        await Assert.That(selector.Calls).IsEqualTo(20);
    }

    static ExperimentSnapshot GroupedSnapshot(
        params (
            string CommandId,
            string ExperimentId,
            string Control,
            (string Id, int Weight)[] Arms
        )[] commands
    )
    {
        var views = commands.ToDictionary(
            command => command.CommandId,
            command => new ExperimentCatalogView(
                command.CommandId,
                1,
                Signature,
                Rev("r1"),
                command.ExperimentId,
                command.Control,
                command.Arms.ToDictionary(
                    arm => arm.Id,
                    arm => Variant(arm.Id, arm.Weight, "text " + arm.Id),
                    StringComparer.Ordinal
                )
            ),
            StringComparer.Ordinal
        );
        return new(Rev("r1"), views);
    }

    static ExperimentAssignment AssignmentOf(ExperimentPin pin, string commandId) =>
        pin.TryGetChoice(commandId, out var choice) && choice is ExperimentChoice.Chosen chosen
            ? chosen.Assignment
            : throw new InvalidOperationException($"Expected a treatment for {commandId}.");

    static ExperimentFallbackReason? ReasonFor(ExperimentPin pin, string commandId) =>
        pin.TryGetChoice(commandId, out var choice)
        && choice is ExperimentChoice.UseDefault useDefault
            ? useDefault.Reason
            : null;

    sealed class FlippingSelector : IExperimentSelector
    {
        public int Calls;

        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        )
        {
            // Odd calls take control and even calls take treatment, so a per-command loop would split the arms.
            var call = Interlocked.Increment(ref Calls);
            var variant = call % 2 == 1 ? "control" : "treatment";
            return ValueTask.FromResult<ExperimentSelection>(
                new ExperimentSelection.Variant(variant)
            );
        }
    }

    sealed class CountingSelector : IExperimentSelector
    {
        public int Calls;

        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult<ExperimentSelection>(
                new ExperimentSelection.Variant("treatment")
            );
        }
    }

    static CachedExperimentSnapshotSource Cached(
        IExperimentSnapshotSource source,
        TimeSpan? latest = null,
        ExperimentSnapshotCache? cache = null,
        string sourceId = "test-source"
    )
    {
        var store =
            cache
            ?? new ExperimentSnapshotCache(
                new ExperimentCacheOptions
                {
                    LatestDefinitionLifetime = latest ?? TimeSpan.FromSeconds(30),
                }
            );
        return new CachedExperimentSnapshotSource(sourceId, source, store);
    }

    static ExperimentRevision Rev(string value) =>
        ExperimentRevision.TryCreate(value, out var revision)
            ? revision!
            : throw new InvalidOperationException(value);

    static ExperimentLookup Lookup(string partition = "tenant-a", string? revision = null) =>
        ExperimentLookup.TryCreate(
            partition,
            revision is null ? null : Rev(revision),
            out var lookup
        )
            ? lookup!
            : throw new InvalidOperationException(partition);

    static CapturedContext Context() => AgentCmdContext.Capture();

    static ConfiguredDefaultView Fallback(string signature = Signature) =>
        new(
            CommandId,
            signature,
            "system",
            "compiled user {Document}",
            new Dictionary<string, JsonElement>(),
            null
        );

    static ExperimentVariant Variant(
        string id,
        int weight,
        string userText,
        string? locale = null,
        bool userRemoved = false
    )
    {
        var values = new Dictionary<string, JsonElement>();
        if (locale is not null)
        {
            values["Locale"] = JsonSerializer.SerializeToElement(locale);
        }

        TemplateChange user = userRemoved
            ? new TemplateChange.Removed()
            : new TemplateChange.Replaced(userText);
        return new(id, weight, new(new TemplateChange.Inherited(), user), values, null);
    }

    /// <summary>Publishes a valid snapshot whose control is the first variant.</summary>
    static ExperimentSnapshot Publish(params (string Id, int Weight, string Text)[] variants) =>
        PublishWith(variants[0].Id, userRemoved: false, variants);

    /// <summary>Publishes a snapshot with an arbitrary control and optional user-template removal, for negative tests.</summary>
    static ExperimentSnapshot PublishWith(
        string control,
        bool userRemoved,
        params (string Id, int Weight, string Text)[] variants
    )
    {
        var views = variants
            .Select(v => new KeyValuePair<string, ExperimentVariant>(
                v.Id,
                Variant(v.Id, v.Weight, v.Text, userRemoved: userRemoved)
            ))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var view = new ExperimentCatalogView(
            CommandId,
            1,
            Signature,
            Rev("r1"),
            "exp-1",
            control,
            views
        );
        return new(Rev("r1"), new Dictionary<string, ExperimentCatalogView> { [CommandId] = view });
    }

    static FakeSnapshotSource Serving(ExperimentSnapshot snapshot) =>
        new(
            (_, _) =>
                ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                    new SourceResult<ExperimentSnapshot>.Found(snapshot)
                )
        );

    static string? VariantOf(ExperimentResolution resolution) =>
        resolution is ExperimentResolution.Treatment treatment
            ? treatment.Assignment.VariantId
            : null;

    static ExperimentFallbackReason? ReasonOf(ExperimentResolution resolution) =>
        resolution is ExperimentResolution.ConfiguredDefault fallback ? fallback.Reason : null;

    static ExperimentAssignment Treatment(ExperimentResolution resolution) =>
        resolution is ExperimentResolution.Treatment treatment
            ? treatment.Assignment
            : throw new InvalidOperationException("Expected a treatment.");

    sealed class FakeSnapshotSource(
        Func<ExperimentLookup, CancellationToken, ValueTask<SourceResult<ExperimentSnapshot>>> read
    ) : IExperimentSnapshotSource
    {
        public int Calls;

        public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref Calls);
            return read(lookup, cancellationToken);
        }
    }

    sealed class ReturningSelector(ExperimentSelection selection) : IExperimentSelector
    {
        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(selection);
    }

    sealed class ThrowingSelector : IExperimentSelector
    {
        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("selector failure");
    }

    /// <summary>A hostile command collection: every member access throws, as a misbehaving source might.</summary>
    sealed class HostileCommands : IReadOnlyDictionary<string, ExperimentCatalogView>
    {
        public int Count => throw new InvalidOperationException("hostile");
        public ExperimentCatalogView this[string key] =>
            throw new InvalidOperationException("hostile");
        public IEnumerable<string> Keys => throw new InvalidOperationException("hostile");
        public IEnumerable<ExperimentCatalogView> Values =>
            throw new InvalidOperationException("hostile");

        public bool ContainsKey(string key) => throw new InvalidOperationException("hostile");

        public bool TryGetValue(string key, out ExperimentCatalogView value) =>
            throw new InvalidOperationException("hostile");

        public IEnumerator<KeyValuePair<string, ExperimentCatalogView>> GetEnumerator() =>
            throw new InvalidOperationException("hostile");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
