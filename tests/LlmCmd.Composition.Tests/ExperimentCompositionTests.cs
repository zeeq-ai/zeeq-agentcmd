using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.Composition.Tests;

/// <summary>Exercises experiment pins through real composition and ledger boundaries.</summary>
public sealed class ExperimentCompositionTests
{
    private sealed class Input(string text)
    {
        public string Text { get; set; } = text;
    }

    private sealed class Binding : IRuntimeBinding<Input>
    {
        public string ContractSignature => "contract";

        public IReadOnlyDictionary<string, string?> Freeze(Input input) =>
            new Dictionary<string, string?> { ["Text"] = input.Text };

        public bool TryApplyOverrides(
            Input input,
            IReadOnlyDictionary<string, JsonElement> overrides,
            out Input effective,
            out string? error
        )
        {
            effective = input;
            error = overrides.Count == 0 ? null : "unsupported";
            return error is null;
        }

        public bool TrySnapshot(Input input, out Input snapshot)
        {
            snapshot = input;
            return false;
        }
    }

    private sealed class RotatingSource(ExperimentSnapshot initial) : IExperimentSnapshotSource
    {
        public ExperimentSnapshot Current = initial;
        public int Reads;
        public bool Unavailable { get; set; }

        public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref Reads);
            return ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                Unavailable
                    ? new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable)
                    : new SourceResult<ExperimentSnapshot>.Found(Current)
            );
        }
    }

    private sealed class CorruptingStore(ICompositionStore inner, bool corrupt = true)
        : ICompositionStore,
            IDisposable
    {
        private bool disposed;

        public void Dispose() => disposed = true;

        public ValueTask<OpenDraftResult> OpenDraftAsync(OpenDraft request, CancellationToken ct) =>
            inner.OpenDraftAsync(request, ct);

        public async ValueTask<DraftRecord?> GetDraftAsync(Guid id, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var record = await inner.GetDraftAsync(id, ct);
            return corrupt && record?.Aggregate is { } aggregate
                ? record with
                {
                    Aggregate = aggregate with
                    {
                        Json = aggregate.Json.Replace(
                            "Invoice 7",
                            "tampered",
                            StringComparison.Ordinal
                        ),
                    },
                }
                : record;
        }

        public ValueTask<ExperimentPinResult> PinExperimentAsync(
            Guid id,
            ExperimentDraftPin pin,
            CancellationToken ct
        ) => inner.PinExperimentAsync(id, pin, ct);

        public ValueTask<ClaimResult> ClaimSlotAsync(ClaimSlot request, CancellationToken ct) =>
            inner.ClaimSlotAsync(request, ct);

        public ValueTask<TransitionResult> AttachSlotAsync(
            AttachSlot request,
            CancellationToken ct
        ) => inner.AttachSlotAsync(request, ct);

        public ValueTask<TransitionResult> ReleaseSlotAsync(
            ReleaseSlot request,
            CancellationToken ct
        ) => inner.ReleaseSlotAsync(request, ct);

        public ValueTask<TransitionResult> AppendEventAsync(
            AppendEvent request,
            CancellationToken ct
        ) => inner.AppendEventAsync(request, ct);

        public ValueTask<SealResult> SealAsync(SealDraft request, CancellationToken ct) =>
            inner.SealAsync(request, ct);
    }

    private sealed class GroupSelector : IExperimentSelector
    {
        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromResult<ExperimentSelection>(
                new ExperimentSelection.Variant(
                    context.View.ControlVariantId,
                    ExperimentRole.Primary,
                    "group-1"
                )
            );
    }

    private sealed class AllowShadow : ICompositionShadowPolicy
    {
        public bool IsAuthorized(
            Actor actor,
            ExperimentSnapshot snapshot,
            IReadOnlyDictionary<string, string> variants
        ) => actor.Tenant == "tenant";
    }

    private sealed class ShadowFactory(CommandRuntime runtime, Action disposed)
        : ICompositionShadowFactory<Doc>,
            IAsyncDisposable
    {
        public ValueTask ConfigureAsync(
            CompositionRoot<Doc> root,
            JsonElement payload,
            Actor actor,
            CancellationToken cancellationToken
        )
        {
            var text = payload.GetProperty("text").GetString()!;
            root.WithPartFrom(
                DocSlots.Header,
                Make(runtime, "header", "HEADER", DocJson.Default.Header).WithParameters(new(text))
            );
            root.WithPartFrom(
                DocSlots.Parties,
                Make(runtime, "parties", "PARTIES", DocJson.Default.Parties)
                    .WithParameters(new(text))
            );
            root.WithPartFrom(
                DocSlots.Lines,
                Make(runtime, "lines", "LINES", DocJson.Default.LineItems).WithParameters(new(text))
            );
            root.WithPartFrom(
                DocSlots.Totals,
                Make(runtime, "totals", "TOTALS", DocJson.Default.Totals).WithParameters(new(text))
            );
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            disposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ContextSelector : IExperimentSelector
    {
        public List<CapturedContext> Captures { get; } = [];

        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        )
        {
            Captures.Add(context.Operation);
            using var other = LlmCmdContext.BeginScope(OperationContext.ForRun(Guid.NewGuid()));
            return ValueTask.FromResult<ExperimentSelection>(
                new ExperimentSelection.Variant(context.View.ControlVariantId)
            );
        }
    }

    private sealed class RacingSource : IExperimentSnapshotSource
    {
        private readonly TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int reads;

        public async ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        )
        {
            var number = Interlocked.Increment(ref reads);
            if (number == 2)
            {
                ready.TrySetResult();
            }
            await ready.Task.WaitAsync(cancellationToken);
            return new SourceResult<ExperimentSnapshot>.Found(View(number == 1 ? "v1" : "v2"));
        }
    }

    private sealed class RejectAggregateLedger(InMemoryFeedbackLedger inner, bool reject = true)
        : IHumanFeedbackLedger,
            IDisposable
    {
        private bool disposed;

        public void Dispose() => disposed = true;

        public Guid CreateRun(Actor actor) => inner.CreateRun(actor);

        public bool CanAccessRun(Guid run, Actor actor)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return inner.CanAccessRun(run, actor);
        }

        public LedgerResult<OutputArtifact> RecordOutput(
            Guid run,
            Guid execution,
            JsonElement value,
            string schema,
            System.Diagnostics.ActivityContext origin,
            Actor actor
        ) => inner.RecordOutput(run, execution, value, schema, origin, actor);

        public LedgerResult<OutputArtifact> RecordProducedOutput(
            Guid run,
            ProducerRef producer,
            JsonElement value,
            string schema,
            System.Diagnostics.ActivityContext origin,
            Actor actor
        ) => inner.RecordProducedOutput(run, producer, value, schema, origin, actor);

        public LedgerResult<OutputArtifact> RecordProducedOutputWithEvidence(
            Guid run,
            ProducerRef producer,
            JsonElement value,
            string schema,
            System.Diagnostics.ActivityContext origin,
            Actor actor,
            ExperimentEvidence evidence
        ) =>
            reject && producer is ProducerRef.Composition
                ? new LedgerResult<OutputArtifact>.Rejected("test_rejected", "test")
                : inner.RecordProducedOutputWithEvidence(
                    run,
                    producer,
                    value,
                    schema,
                    origin,
                    actor,
                    evidence
                );

        public LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return inner.ReadOutput(target, actor);
        }

        public LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor) =>
            inner.RecordGrade(request, actor);

        public LedgerResult<CorrectionReceipt> SubmitCorrection(
            CorrectionRequest request,
            Actor actor
        ) => inner.SubmitCorrection(request, actor);
    }

    private static ExperimentSnapshot View(string revision)
    {
        ExperimentRevision.TryCreate(revision, out var id);
        var commands = new Dictionary<string, ExperimentCatalogView>(StringComparer.Ordinal);
        foreach (
            var (name, prefix) in new[]
            {
                ("header", "HEADER"),
                ("parties", "PARTIES"),
                ("lines", "LINES"),
                ("totals", "TOTALS"),
            }
        )
        {
            var variant = new ExperimentVariant(
                "control",
                1,
                new(
                    new TemplateChange.Inherited(),
                    new TemplateChange.Replaced(prefix + ":" + revision + " {Text}")
                ),
                new Dictionary<string, JsonElement>(),
                null
            );
            commands[name] = new(
                name,
                1,
                "contract",
                id!,
                "exp",
                "control",
                new Dictionary<string, ExperimentVariant> { ["control"] = variant }
            );
        }
        return new(id!, commands);
    }

    private static Cmd<Input, TR> Make<TR>(
        CommandRuntime runtime,
        string id,
        string prefix,
        JsonTypeInfo<TR> result,
        Func<Input, string?>? validate = null
    ) =>
        new(
            runtime,
            new PromptDescriptor<Input, TR>(
                id,
                1,
                "extraction",
                "",
                prefix + ":{Text}",
                new string('a', 64),
                id,
                new Lazy<JsonElement>(() =>
                    JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()
                ),
                result,
                p => prefix + ":" + p.Text,
                _ => "",
                p =>
                    [
                        new ParameterRecord(
                            "Text",
                            "string",
                            JsonSerializer.SerializeToElement(p.Text),
                            null
                        ),
                    ],
                validate ?? (_ => null),
                _ => null,
                RuntimeBinding: new Binding()
            )
        );

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingInputHookStopsExperimentWithoutSendingThatPart(bool deferred)
    {
        static string? Throw(Input _) => throw new InvalidOperationException("private hook detail");

        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient();
        var options = new ExperimentRuntimeOptions(
            new ExperimentResolver(new RotatingSource(View("v1"))),
            DefaultConfigurationFingerprint: "defaults-v1"
        );
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: options
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        var header = Make(
            runtime,
            "header",
            "HEADER",
            DocJson.Default.Header,
            deferred ? null : Throw
        );
        var parties = Make(runtime, "parties", "PARTIES", DocJson.Default.Parties);
        var lines = Make(runtime, "lines", "LINES", DocJson.Default.LineItems);
        var totals = Make(
            runtime,
            "totals",
            "TOTALS",
            DocJson.Default.Totals,
            deferred ? Throw : null
        );
        var root = await roots.BeginAsync<Doc>(new(run, "input", "hook-error"));
        root.WithPartFrom(DocSlots.Header, header.WithParameters(new("header")));
        root.WithPartFrom(DocSlots.Parties, parties.WithParameters(new("parties")));
        root.WithPartFrom(DocSlots.Lines, lines.WithParameters(new("lines")));
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Header)
                .Then<Totals, Input>(_ => totals.WithParameters(new("totals")))
        );

        var outcome = await root.ExecuteAsync();
        if (deferred)
        {
            var incomplete = (CompositeOutcome<Doc>.Incomplete)outcome;
            await Assert
                .That(incomplete.Steps.Single(step => step.SlotId == "totals").Code)
                .IsEqualTo("hook_failure:ValidateInput");
            await Assert.That(client.CallsFor("TOTALS")).IsEqualTo(0);
        }
        else
        {
            var unavailable = (CompositeOutcome<Doc>.Unavailable)outcome;
            await Assert.That(unavailable.Reason).IsEqualTo("hook_failure:ValidateInput");
            await Assert.That(client.Total).IsEqualTo(0);
        }
    }

    [Test]
    public async Task PinsVersionAcrossMutationRotationAndDeferredStepAndStoresAggregateEvidence()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient();
        var source = new RotatingSource(View("v1"));
        var selector = new ContextSelector();
        var options = new ExperimentRuntimeOptions(
            new ExperimentResolver(source, selector: selector),
            DefaultConfigurationFingerprint: "defaults-v1"
        );
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: options
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        var header = Make(runtime, "header", "HEADER", DocJson.Default.Header);
        var parties = Make(runtime, "parties", "PARTIES", DocJson.Default.Parties);
        var lines = Make(runtime, "lines", "LINES", DocJson.Default.LineItems);
        var totals = Make(runtime, "totals", "TOTALS", DocJson.Default.Totals);
        var root = await roots.BeginAsync<Doc>(new(run, "input", "key"));
        var mutable = new Input("original");
        root.WithPartFrom(DocSlots.Header, header.WithParameters(mutable));
        root.WithPartFrom(DocSlots.Parties, parties.WithParameters(new("parties")));
        root.WithPartFrom(DocSlots.Lines, lines.WithParameters(new("lines")));
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Header)
                .Then<Totals, Input>(_ => totals.WithParameters(new("deferred")))
        );
        mutable.Text = "changed";
        client.Before = (key, _) =>
        {
            if (key == "HEADER")
            {
                source.Current = View("v2");
            }
            return Task.CompletedTask;
        };
        var published = Check.Published(await root.ExecuteAsync());
        await Assert.That(client.Prompts["HEADER"]).IsEqualTo("HEADER:v1 original");
        await Assert.That(client.Prompts["TOTALS"]).IsEqualTo("TOTALS:v1 deferred");
        await Assert.That(source.Reads).IsEqualTo(1);
        await Assert.That(selector.Captures.Count).IsEqualTo(1);
        await Assert
            .That(
                selector.Captures.All(context =>
                    context.RunId == run && context.DraftId == root.DraftId
                )
            )
            .IsTrue();
        await Assert
            .That(
                selector.Captures.Select(context => context.Reference!.ContextId).Distinct().Count()
            )
            .IsEqualTo(1);
        var draft = (await store.GetDraftAsync(root.DraftId!.Value, default))!;
        await Assert.That(draft.ExperimentPin!.Snapshot!.Revision.Value).IsEqualTo("v1");
        var artifact = (
            (LedgerResult<OutputArtifact>.Accepted)
                ledger.ReadOutput(published.Aggregate.Output, actor)
        ).Value;
        await Assert.That(artifact.Evidence!.Source).IsEqualTo("composition");
        await Assert.That(artifact.Evidence.Members!.Count).IsEqualTo(4);
        await Assert.That(artifact.Evidence.CompositionMembers!.Count).IsEqualTo(4);
        await Assert
            .That(
                artifact.Evidence.CompositionMembers.All(member =>
                    member.Provenance == MemberProvenance.Executed
                )
            )
            .IsTrue();
        await Assert.That(artifact.Evidence.Members.Contains(published.Aggregate.Output)).IsFalse();
        var grade = ledger.RecordGrade(
            new GradeRequest(
                Guid.NewGuid(),
                published.Aggregate.Output,
                "/header/title",
                JsonSerializer.SerializeToElement("Invoice 7"),
                1
            ),
            actor
        );
        await Assert.That(grade is LedgerResult<GradeReceipt>.Accepted).IsTrue();
    }

    [Test]
    public async Task UnavailableSourcePinsDefaultAndRejectsChangedDefaultOnReplay()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var source = new RotatingSource(View("v1")) { Unavailable = true };
        var client = new FakeClient();
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        CommandRuntime Runtime(string defaultId) =>
            CommandRuntime.Create(
                () => client,
                new Dictionary<string, Profile> { ["extraction"] = new("model") },
                ledger,
                () => actor,
                experiments: new ExperimentRuntimeOptions(
                    new ExperimentResolver(source),
                    DefaultConfigurationFingerprint: defaultId
                )
            );
        void Register(CompositionRoot<Doc> root, CommandRuntime runtime)
        {
            root.WithPartFrom(
                DocSlots.Header,
                Make(runtime, "header", "HEADER", DocJson.Default.Header).WithParameters(new("doc"))
            );
            root.WithPartFrom(
                DocSlots.Parties,
                Make(runtime, "parties", "PARTIES", DocJson.Default.Parties)
                    .WithParameters(new("doc"))
            );
            root.WithPartFrom(
                DocSlots.Lines,
                Make(runtime, "lines", "LINES", DocJson.Default.LineItems)
                    .WithParameters(new("doc"))
            );
            root.WithPartFrom(
                DocSlots.Totals,
                Make(runtime, "totals", "TOTALS", DocJson.Default.Totals).WithParameters(new("doc"))
            );
        }
        using var runtime1 = Runtime("defaults-v1");
        var roots1 = new RootCommands(ledger, () => actor, store, registry, runtime: runtime1);
        var first = await roots1.BeginAsync<Doc>(new(run, "input", "key"));
        Register(first, runtime1);
        var published = Check.Published(await first.ExecuteAsync());
        await Assert.That(client.Prompts["HEADER"]).IsEqualTo("HEADER:doc");
        await Assert
            .That(
                (await store.GetDraftAsync(first.DraftId!.Value, default))!
                    .ExperimentPin!
                    .DefaultReason
            )
            .IsEqualTo(ExperimentFallbackReason.Unavailable);
        source.Unavailable = false;
        var replay = await roots1.BeginAsync<Doc>(new(run, "input", "key"));
        Register(replay, runtime1);
        var replayed = Check.Published(await replay.ExecuteAsync());
        await Assert.That(replayed.Aggregate.Output).IsEqualTo(published.Aggregate.Output);
        await Assert.That(source.Reads).IsEqualTo(1);
        await Assert.That(client.Total).IsEqualTo(4);
        using var runtime2 = Runtime("defaults-v2");
        var roots2 = new RootCommands(ledger, () => actor, store, registry, runtime: runtime2);
        var changed = await roots2.BeginAsync<Doc>(new(run, "input", "key"));
        Register(changed, runtime2);
        var failure = (CompositeOutcome<Doc>.Unavailable)await changed.ExecuteAsync();
        await Assert.That(failure.Reason).IsEqualTo("experiment_default_identity_changed");
        await Assert.That(source.Reads).IsEqualTo(1);
        await Assert.That(client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task ConcurrentRootsAdoptOneSnapshotAndNeverMixVersions()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var source = new RacingSource();
        var client = new FakeClient();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: new ExperimentRuntimeOptions(
                new ExperimentResolver(source),
                DefaultConfigurationFingerprint: "defaults-v1"
            )
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        async Task<CompositionRoot<Doc>> Open()
        {
            var root = await roots.BeginAsync<Doc>(new(run, "input", "key"));
            root.WithPartFrom(
                DocSlots.Header,
                Make(runtime, "header", "HEADER", DocJson.Default.Header).WithParameters(new("doc"))
            );
            root.WithPartFrom(
                DocSlots.Parties,
                Make(runtime, "parties", "PARTIES", DocJson.Default.Parties)
                    .WithParameters(new("doc"))
            );
            root.WithPartFrom(
                DocSlots.Lines,
                Make(runtime, "lines", "LINES", DocJson.Default.LineItems)
                    .WithParameters(new("doc"))
            );
            root.WithPartFrom(
                DocSlots.Totals,
                Make(runtime, "totals", "TOTALS", DocJson.Default.Totals).WithParameters(new("doc"))
            );
            return root;
        }
        var first = await Open();
        var second = await Open();
        var outcomes = await Task.WhenAll(first.ExecuteAsync(), second.ExecuteAsync());
        await Assert.That(outcomes.All(o => o is CompositeOutcome<Doc>.Published)).IsTrue();
        var pin = (await store.GetDraftAsync(first.DraftId!.Value, default))!.ExperimentPin!;
        await Assert
            .That(
                client.Prompts.Values.All(prompt =>
                    prompt.Contains(
                        ":" + pin.Snapshot!.Revision.Value + " ",
                        StringComparison.Ordinal
                    )
                )
            )
            .IsTrue();
        await Assert.That(client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task AggregateEvidenceRejectionPreventsSeal()
    {
        var inner = new InMemoryFeedbackLedger();
        var ledger = new RejectAggregateLedger(inner);
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: new ExperimentRuntimeOptions(
                new ExperimentResolver(new RotatingSource(View("v1"))),
                DefaultConfigurationFingerprint: "defaults-v1"
            )
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        var root = await roots.BeginAsync<Doc>(new(run, "input", "key"));
        root.WithPartFrom(
            DocSlots.Header,
            Make(runtime, "header", "HEADER", DocJson.Default.Header).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Parties,
            Make(runtime, "parties", "PARTIES", DocJson.Default.Parties).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Lines,
            Make(runtime, "lines", "LINES", DocJson.Default.LineItems).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Totals,
            Make(runtime, "totals", "TOTALS", DocJson.Default.Totals).WithParameters(new("doc"))
        );
        var outcome = await root.ExecuteAsync();
        await Assert.That(outcome is CompositeOutcome<Doc>.Unavailable).IsTrue();
        await Assert
            .That((await store.GetDraftAsync(root.DraftId!.Value, default))!.Status)
            .IsEqualTo(DraftStatus.Open);
        await Assert.That(client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task InvalidKnownTreatmentFallsBackForEntireDraftBeforeAnyClaim()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var snapshot = View("v1");
        var header = snapshot.Commands["header"];
        var variants = (Dictionary<string, ExperimentVariant>)header.Variants;
        variants["control"] = variants["control"] with
        {
            Prompt = new(
                new TemplateChange.Inherited(),
                new TemplateChange.Replaced("HEADER:{Unknown}")
            ),
        };
        var client = new FakeClient();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: new ExperimentRuntimeOptions(
                new ExperimentResolver(new RotatingSource(snapshot)),
                DefaultConfigurationFingerprint: "defaults-v1"
            )
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        var root = await roots.BeginAsync<Doc>(new(run, "input", "key"));
        root.WithPartFrom(
            DocSlots.Header,
            Make(runtime, "header", "HEADER", DocJson.Default.Header).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Parties,
            Make(runtime, "parties", "PARTIES", DocJson.Default.Parties).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Lines,
            Make(runtime, "lines", "LINES", DocJson.Default.LineItems).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Totals,
            Make(runtime, "totals", "TOTALS", DocJson.Default.Totals).WithParameters(new("doc"))
        );
        Check.Published(await root.ExecuteAsync());
        var pin = (await store.GetDraftAsync(root.DraftId!.Value, default))!.ExperimentPin!;
        await Assert.That(pin.DefaultReason).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
        await Assert
            .That(
                client.Prompts.Values.All(prompt =>
                    !prompt.Contains(":v1 ", StringComparison.Ordinal)
                )
            )
            .IsTrue();
        await Assert.That(client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task InvalidDeferredTreatmentFailsWithoutRepinningOrSendingThatStep()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var snapshot = View("v1");
        var variants = (Dictionary<string, ExperimentVariant>)snapshot.Commands["totals"].Variants;
        variants["control"] = variants["control"] with
        {
            Prompt = new(
                new TemplateChange.Inherited(),
                new TemplateChange.Replaced("TOTALS:{Unknown}")
            ),
        };
        var client = new FakeClient();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: new ExperimentRuntimeOptions(
                new ExperimentResolver(new RotatingSource(snapshot)),
                DefaultConfigurationFingerprint: "defaults-v1"
            )
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        var root = await roots.BeginAsync<Doc>(new(run, "input", "key"));
        var totals = Make(runtime, "totals", "TOTALS", DocJson.Default.Totals);
        root.WithPartFrom(
            DocSlots.Header,
            Make(runtime, "header", "HEADER", DocJson.Default.Header).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Parties,
            Make(runtime, "parties", "PARTIES", DocJson.Default.Parties).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Lines,
            Make(runtime, "lines", "LINES", DocJson.Default.LineItems).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Totals,
            root.Steps.Of(DocSlots.Header)
                .Then<Totals, Input>(_ => totals.WithParameters(new("doc")))
        );
        var outcome = await root.ExecuteAsync();
        await Assert.That(outcome is CompositeOutcome<Doc>.Incomplete).IsTrue();
        var draft = (await store.GetDraftAsync(root.DraftId!.Value, default))!;
        await Assert.That(draft.ExperimentPin!.Snapshot!.Revision.Value).IsEqualTo("v1");
        await Assert.That(draft.Slots["totals"].Attempts).IsEqualTo(0);
        await Assert.That(client.CallsFor("TOTALS")).IsEqualTo(0);
        await Assert.That(client.Total).IsEqualTo(3);
    }

    [Test]
    public async Task ReusedPartKeepsOriginalExperimentEvidenceAndProvenance()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var source = new RotatingSource(View("v1"));
        var client = new FakeClient();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: new ExperimentRuntimeOptions(
                new ExperimentResolver(source),
                DefaultConfigurationFingerprint: "defaults-v1"
            )
        );
        var header = Make(runtime, "header", "HEADER", DocJson.Default.Header);
        var seed = (Outcome<Header>.Success)
            await header.WithParameters(new("seed")).InRun(run).ExecuteAsync();
        var seedOutput = ((OutputDelivery.Stored)seed.Delivery).Output;
        await Assert.That(seedOutput.Evidence!.Revision).IsEqualTo("v1");
        source.Current = View("v2");
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: runtime);
        var root = await roots.BeginAsync<Doc>(new(run, "input", "key"));
        root.ReusePart(DocSlots.Header, new Pinned<Header>(seed.Value, seedOutput.Reference));
        root.WithPartFrom(
            DocSlots.Parties,
            Make(runtime, "parties", "PARTIES", DocJson.Default.Parties).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Lines,
            Make(runtime, "lines", "LINES", DocJson.Default.LineItems).WithParameters(new("doc"))
        );
        root.WithPartFrom(
            DocSlots.Totals,
            Make(runtime, "totals", "TOTALS", DocJson.Default.Totals).WithParameters(new("doc"))
        );
        var published = Check.Published(await root.ExecuteAsync());
        var whole = (
            (LedgerResult<OutputArtifact>.Accepted)
                ledger.ReadOutput(published.Aggregate.Output, actor)
        ).Value;
        var member = whole.Evidence!.CompositionMembers!.Single(m => m.SlotId == "header");
        await Assert.That(member.Provenance).IsEqualTo(MemberProvenance.Reused);
        await Assert.That(member.Output).IsEqualTo(seedOutput.Reference);
        await Assert
            .That(
                ((LedgerResult<OutputArtifact>.Accepted)ledger.ReadOutput(member.Output, actor))
                    .Value
                    .Evidence!
                    .Revision
            )
            .IsEqualTo("v1");
        await Assert.That(whole.Evidence.Revision).IsEqualTo("v2");
    }

    [Test]
    public async Task ReplayUsesAuthorizedLedgerOutputWhenStoreJsonDiffers()
    {
        using var rig = new Rig();
        var corrupt = new CorruptingStore(rig.RealStore);
        var roots = new RootCommands(rig.Ledger, () => rig.Actor, corrupt, rig.Registry);
        async Task<CompositionRoot<Doc>> Open()
        {
            var root = await roots.BeginAsync<Doc>(new(rig.Run, "input", "key"));
            return rig.AddAll(root);
        }
        var first = Check.Published(await (await Open()).ExecuteAsync());
        var replay = Check.Published(await (await Open()).ExecuteAsync());
        await Assert.That(replay.Aggregate.Output).IsEqualTo(first.Aggregate.Output);
        await Assert.That(replay.Value.Header.Title).IsEqualTo("Invoice 7");
        await Assert.That(rig.Client.Total).IsEqualTo(4);
    }

    [Test]
    public async Task CompositionShadowUsesIndependentScopeAndPinnedSnapshotWithoutBlockingPrimary()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var sourceView = View("v1");
        ((Dictionary<string, ExperimentCatalogView>)sourceView.Commands).Remove("totals");
        var source = new RotatingSource(sourceView);
        var options = new ExperimentRuntimeOptions(
            new ExperimentResolver(source, selector: new GroupSelector()),
            DefaultConfigurationFingerprint: "defaults-v1"
        );
        var primaryClient = new FakeClient();
        var shadowClient = new FakeClient();
        var primaryGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var shadowGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        primaryClient.Before = (key, ct) =>
            key == "HEADER" ? primaryGate.Task.WaitAsync(ct) : Task.CompletedTask;
        shadowClient.Before = (key, ct) =>
            key == "HEADER" ? shadowGate.Task.WaitAsync(ct) : Task.CompletedTask;
        using var primaryRuntime = CommandRuntime.Create(
            () => primaryClient,
            new Dictionary<string, Profile> { ["extraction"] = new("model") },
            ledger,
            () => actor,
            experiments: options
        );
        var store = new InMemoryCompositionStore();
        var registry = new CompositionRegistry([
            new CompositeDescriptor<Doc>(
                "document.extraction",
                1,
                "hash",
                DocJson.Default.Doc,
                DocSlots.All
            ),
        ]);
        var scopeDisposed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var services = new ServiceCollection()
            .AddScoped<ICompositionStore>(_ => new CorruptingStore(store, false))
            .AddScoped<IHumanFeedbackLedger>(_ => new RejectAggregateLedger(ledger, false))
            .AddSingleton(registry)
            .AddScoped(sp =>
                CommandRuntime.Create(
                    () => shadowClient,
                    new Dictionary<string, Profile> { ["extraction"] = new("model") },
                    sp.GetRequiredService<IHumanFeedbackLedger>(),
                    () => actor,
                    experiments: options
                )
            )
            .AddScoped<ICompositionShadowFactory<Doc>>(sp => new ShadowFactory(
                sp.GetRequiredService<CommandRuntime>(),
                () => scopeDisposed.TrySetResult()
            ))
            .BuildServiceProvider();
        await using var scheduler = new InMemoryShadowScheduler(2, 1);
        var shadowResult = new TaskCompletionSource<CompositionShadowResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var disposition = new TaskCompletionSource<ShadowDisposition>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var admissionScope = services.CreateAsyncScope();
        var admissionStore = (CorruptingStore)
            admissionScope.ServiceProvider.GetRequiredService<ICompositionStore>();
        var admissionLedger = (RejectAggregateLedger)
            admissionScope.ServiceProvider.GetRequiredService<IHumanFeedbackLedger>();
        var coordinator = new CompositionShadowCoordinator(
            admissionStore,
            admissionLedger,
            services.GetRequiredService<IServiceScopeFactory>(),
            scheduler,
            new AllowShadow(),
            TimeSpan.FromSeconds(5),
            result => shadowResult.TrySetResult(result),
            result => disposition.TrySetResult(result)
        );
        var roots = new RootCommands(ledger, () => actor, store, registry, runtime: primaryRuntime);
        var primary = await roots.BeginAsync<Doc>(new(run, "input", "primary"));
        primary.WithPartFrom(
            DocSlots.Header,
            Make(primaryRuntime, "header", "HEADER", DocJson.Default.Header)
                .WithParameters(new("doc"))
        );
        primary.WithPartFrom(
            DocSlots.Parties,
            Make(primaryRuntime, "parties", "PARTIES", DocJson.Default.Parties)
                .WithParameters(new("doc"))
        );
        primary.WithPartFrom(
            DocSlots.Lines,
            Make(primaryRuntime, "lines", "LINES", DocJson.Default.LineItems)
                .WithParameters(new("doc"))
        );
        primary.WithPartFrom(
            DocSlots.Totals,
            Make(primaryRuntime, "totals", "TOTALS", DocJson.Default.Totals)
                .WithParameters(new("doc"))
        );
        var primaryTask = primary.ExecuteAsync();
        await Check.WaitUntil(() => primaryClient.CallsFor("HEADER") == 1);
        source.Current = View("v2");
        var variants = new Dictionary<string, string>
        {
            ["header"] = "control",
            ["parties"] = "control",
            ["lines"] = "control",
        };
        var denied = await coordinator.TryScheduleAsync<Doc>(
            primary.DraftId!.Value,
            new Actor("other", "subject"),
            JsonSerializer.SerializeToElement(new { text = "doc" }),
            variants,
            "group-1"
        );
        await Assert.That(denied).IsFalse();
        var invalidVariants = new Dictionary<string, string>(variants) { ["header"] = "missing" };
        var invalid = await coordinator.TryScheduleAsync<Doc>(
            primary.DraftId!.Value,
            actor,
            JsonSerializer.SerializeToElement(new { text = "doc" }),
            invalidVariants,
            "group-1"
        );
        await Assert.That(invalid).IsFalse();
        using var wrongServices = new ServiceCollection()
            .AddSingleton<ICompositionStore>(new InMemoryCompositionStore())
            .AddSingleton<IHumanFeedbackLedger>(ledger)
            .BuildServiceProvider();
        var wrongDisposition = new TaskCompletionSource<ShadowDisposition>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var wrongCoordinator = new CompositionShadowCoordinator(
            store,
            ledger,
            wrongServices.GetRequiredService<IServiceScopeFactory>(),
            scheduler,
            new AllowShadow(),
            TimeSpan.FromSeconds(5),
            observeDisposition: result => wrongDisposition.TrySetResult(result)
        );
        await Assert
            .That(
                await wrongCoordinator.TryScheduleAsync<Doc>(
                    primary.DraftId!.Value,
                    actor,
                    JsonSerializer.SerializeToElement(new { text = "doc" }),
                    variants,
                    "group-1"
                )
            )
            .IsTrue();
        await Assert
            .That(await wrongDisposition.Task.WaitAsync(TimeSpan.FromSeconds(3)))
            .IsEqualTo(ShadowDisposition.Failed);
        await Assert.That(shadowClient.Total).IsEqualTo(0);
        var accepted = await coordinator.TryScheduleAsync<Doc>(
            primary.DraftId!.Value,
            actor,
            JsonSerializer.SerializeToElement(new { text = "doc" }),
            variants,
            "group-1"
        );
        await Assert.That(accepted).IsTrue();
        await Check.WaitUntil(() => shadowClient.CallsFor("HEADER") == 1);
        await admissionScope.DisposeAsync();
        await Assert
            .That(
                Check.Throws<ObjectDisposedException>(() =>
                    admissionStore
                        .GetDraftAsync(primary.DraftId!.Value, default)
                        .GetAwaiter()
                        .GetResult()
                )
            )
            .IsTrue();
        await Assert
            .That(
                Check.Throws<ObjectDisposedException>(() =>
                    admissionLedger.CanAccessRun(run, actor)
                )
            )
            .IsTrue();
        primaryGate.SetResult();
        var primaryWhole = Check.Published(await primaryTask.WaitAsync(TimeSpan.FromSeconds(3)));
        await Assert.That(shadowResult.Task.IsCompleted).IsFalse();
        shadowGate.SetResult();
        var result = await shadowResult.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert
            .That(await disposition.Task.WaitAsync(TimeSpan.FromSeconds(3)))
            .IsEqualTo(ShadowDisposition.Completed);
        await scopeDisposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(result.ShadowDraftId == primaryWhole.DraftId).IsFalse();
        await Assert.That(result.Output == primaryWhole.Aggregate.Output).IsFalse();
        var primaryDraft = (await store.GetDraftAsync(primaryWhole.DraftId, default))!;
        var shadowDraft = (await store.GetDraftAsync(result.ShadowDraftId, default))!;
        await Assert
            .That(
                primaryDraft.Slots["totals"].Fingerprint == shadowDraft.Slots["totals"].Fingerprint
            )
            .IsFalse();
        var primaryArtifact = (
            (LedgerResult<OutputArtifact>.Accepted)
                ledger.ReadOutput(primaryWhole.Aggregate.Output, actor)
        ).Value;
        var shadowArtifact = (
            (LedgerResult<OutputArtifact>.Accepted)ledger.ReadOutput(result.Output, actor)
        ).Value;
        await Assert.That(primaryArtifact.Evidence!.Role).IsEqualTo(ExperimentRole.Primary);
        await Assert.That(shadowArtifact.Evidence!.Role).IsEqualTo(ExperimentRole.Shadow);
        await Assert.That(primaryArtifact.Evidence.ComparisonGroupId).IsEqualTo("group-1");
        await Assert.That(shadowArtifact.Evidence.ComparisonGroupId).IsEqualTo("group-1");
        await Assert.That(shadowArtifact.Evidence.Revision).IsEqualTo("v1");
        await Assert
            .That(
                shadowArtifact.Evidence.Members!.All(reference =>
                    primaryWhole.Members.All(member => member.Output != reference)
                )
            )
            .IsTrue();
        foreach (var member in shadowArtifact.Evidence.CompositionMembers!)
        {
            var part = (
                (LedgerResult<OutputArtifact>.Accepted)ledger.ReadOutput(member.Output, actor)
            ).Value;
            await Assert.That(part.Evidence!.Role).IsEqualTo(ExperimentRole.Shadow);
            await Assert.That(part.Evidence.ComparisonGroupId).IsEqualTo("group-1");
            await Assert
                .That(part.Evidence.Source)
                .IsEqualTo(member.SlotId == "totals" ? "default" : "experiment");
            await Assert
                .That(part.Evidence.Revision)
                .IsEqualTo(member.SlotId == "totals" ? null : "v1");
        }
        await Assert
            .That(
                shadowClient
                    .Prompts.Where(pair => pair.Key != "TOTALS")
                    .All(pair => pair.Value.Contains(":v1 ", StringComparison.Ordinal))
            )
            .IsTrue();
        await Assert.That(source.Reads).IsEqualTo(1);
        var grade = ledger.RecordGrade(
            new GradeRequest(
                Guid.NewGuid(),
                result.Output,
                "/header/title",
                JsonSerializer.SerializeToElement("Invoice 7"),
                1
            ),
            actor
        );
        await Assert.That(grade is LedgerResult<GradeReceipt>.Accepted).IsTrue();
    }
}
