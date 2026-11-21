using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommandRoot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

/// <summary>
/// Gates for worker execution safety: a standalone invocation can carry its own journal, and a strict
/// candidate either prepares exactly the planned configuration or fails before any provider send.
/// </summary>
public partial class StrictCandidateJournalTests
{
    sealed record Input(string Text, string Locale);

    sealed record Result(string Value);

    static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    sealed class Binding : IRuntimeBinding<Input>
    {
        public string ContractSignature => "shape1";

        public IReadOnlyDictionary<string, string?> Freeze(Input input) =>
            new Dictionary<string, string?> { ["Text"] = input.Text, ["Locale"] = input.Locale };

        public bool TrySnapshot(Input input, out Input snapshot)
        {
            snapshot = input;
            return true;
        }

        public bool TryApplyOverrides(
            Input input,
            IReadOnlyDictionary<string, JsonElement> overrides,
            out Input effective,
            out string? error
        )
        {
            effective = input;
            error = null;
            if (overrides.Any(x => x.Key != "Locale" || x.Value.ValueKind != JsonValueKind.String))
            {
                error = "invalid";
                return false;
            }

            if (overrides.TryGetValue("Locale", out var value))
            {
                effective = input with { Locale = value.GetString()! };
            }

            return true;
        }
    }

    static readonly PromptDescriptor<Input, Result> Descriptor = new(
        "strict.test",
        1,
        "base",
        "",
        "base {Text} {Locale}",
        "hash",
        "result",
        new(() => J("{\"type\":\"object\"}")),
        (JsonTypeInfo<Result>)JsonSerializerOptions.Default.GetTypeInfo(typeof(Result)),
        p => $"base {p.Text} {p.Locale}",
        _ => "",
        p =>
            [
                new("Text", "string", J(JsonSerializer.Serialize(p.Text))),
                new("Locale", "string", J(JsonSerializer.Serialize(p.Locale))),
            ],
        p => string.IsNullOrEmpty(p.Text) ? "missing" : null,
        _ => null,
        RuntimeBinding: new Binding()
    );

    sealed class Command(CommandRuntime runtime)
        : PromptCommand<Input, Result>(runtime, Descriptor);

    sealed class Client : IChatClient
    {
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "{\"Value\":\"ok\"}"))
            );
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>Returns a fixed snapshot and records which revision each lookup requested.</summary>
    sealed class Source : IExperimentSnapshotSource
    {
        public ExperimentSnapshot? Snapshot;
        public ConcurrentQueue<string?> Requested { get; } = new();

        public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        )
        {
            Requested.Enqueue(lookup.RequestedRevision?.Value);
            return ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                Snapshot is null
                    ? new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable)
                    : new SourceResult<ExperimentSnapshot>.Found(Snapshot)
            );
        }
    }

    sealed class Selector(Func<ExperimentSelection> select) : IExperimentSelector
    {
        public ValueTask<ExperimentSelection> SelectAsync(
            ExperimentSelectionContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(select());
    }

    /// <summary>Records every journal event and can reject either lifecycle edge.</summary>
    sealed class Journal : IExecutionJournal
    {
        public ConcurrentQueue<ExecutionEvent> Events { get; } = new();
        public bool RejectStart;
        public bool RejectFinish;

        public ValueTask AppendAsync(ExecutionEvent record, CancellationToken cancellationToken)
        {
            if (
                RejectStart && record.Kind == "attempt.started"
                || RejectFinish && record.Kind == "attempt.finished"
            )
            {
                throw new IOException("journal offline");
            }

            Events.Enqueue(record);
            return ValueTask.CompletedTask;
        }
    }

    static ExperimentRevision Revision(string value)
    {
        ExperimentRevision.TryCreate(value, out var revision);
        return revision!;
    }

    static ExperimentVariant Variant(
        string id,
        string locale = "fr",
        string template = "new {Text} {Locale}",
        bool routed = true
    ) =>
        new(
            id,
            1,
            new(new TemplateChange.Inherited(), new TemplateChange.Replaced(template)),
            new Dictionary<string, JsonElement>
            {
                ["Locale"] = J(JsonSerializer.Serialize(locale)),
            },
            routed
                ? new(
                    ProfileKey.Declare("dynamic"),
                    "dynamic",
                    "new-model",
                    Defaults: new(Temperature: .3f)
                )
                : null
        );

    static ExperimentSnapshot Release(
        string revision,
        ExperimentVariant[]? variants = null,
        int commandRevision = 1,
        string signature = "shape1",
        string experiment = "trial"
    )
    {
        variants ??= [Variant("v"), Variant("w", "de")];
        var view = ExperimentCatalogView.Freeze(
            Descriptor.Id,
            commandRevision,
            signature,
            Revision(revision),
            experiment,
            variants[0].VariantId,
            variants
        );
        return new(
            Revision(revision),
            new Dictionary<string, ExperimentCatalogView> { [view.CommandId] = view }
        );
    }

    static readonly Actor User = new("tenant", "user");

    sealed record Harness(
        ServiceProvider Provider,
        Client Baseline,
        Client Treatment,
        Source Source
    ) : IDisposable
    {
        public Command Command => new(Provider.GetRequiredService<CommandRuntime>());

        public Guid Run() => Provider.GetRequiredService<IHumanFeedbackLedger>().CreateRun(User);

        public int Sends => Baseline.Calls + Treatment.Calls;

        public void Dispose() => Provider.Dispose();
    }

    static Harness Routed(
        ExperimentSnapshot? snapshot,
        Func<ExperimentSelection>? select = null,
        Func<Actor, ExecutionProfile, bool>? policy = null,
        string? fingerprint = "default-v1"
    )
    {
        var source = new Source { Snapshot = snapshot };
        var baseline = new Client();
        var treatment = new Client();
        var services = new ServiceCollection();
        services.AddAgentCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => User)
                .UseExperiments(
                    new ExperimentRuntimeOptions(
                        new ExperimentResolver(
                            source,
                            selector: select is null ? null : new Selector(select)
                        ),
                        RoutePolicy: policy ?? ((_, _) => true),
                        DefaultConfigurationFingerprint: fingerprint
                    )
                )
        );
        return new(services.BuildServiceProvider(), baseline, treatment, source);
    }

    static (CommandRuntime Runtime, Client Client, Guid Run) Direct(
        ExperimentRuntimeOptions? experiments = null
    )
    {
        var ledger = new InMemoryFeedbackLedger();
        var client = new Client();
        var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["base"] = new("base-model", .2f) },
            ledger,
            () => User,
            experiments: experiments
        );
        return (runtime, client, ledger.CreateRun(User));
    }

    static readonly ExpectedExperimentCandidate.Treatment Planned = new(
        Revision("r1"),
        "trial",
        "v"
    );

    static string? Reason<T>(Outcome<T> outcome) =>
        (outcome as Outcome<T>.Failed)?.Failure is ExecutionFailure.ConfigurationFailure failure
            ? failure.Reason
            : null;

    [Test]
    public async Task Strict_treatment_reads_its_exact_revision_and_records_matching_evidence()
    {
        using var h = Routed(Release("r1"));

        var outcome = await h
            .Command.WithParameters(new("invoice", "en"))
            .RequireExperimentCandidate(Planned)
            .ExecuteAsync();

        var success = (Outcome<Result>.Success)outcome;
        await Assert.That(h.Treatment.Calls).IsEqualTo(1);
        await Assert.That(h.Baseline.Calls).IsEqualTo(0);
        await Assert.That(h.Source.Requested.Single()).IsEqualTo("r1");
        await Assert.That(success.Snapshot.Experiment!.Revision).IsEqualTo("r1");
        await Assert.That(success.Snapshot.Experiment.ExperimentId).IsEqualTo("trial");
        await Assert.That(success.Snapshot.Experiment.VariantId).IsEqualTo("v");
        await Assert.That(success.Snapshot.Experiment.Reason).IsNull();
    }

    [Test]
    public async Task Strict_rejections_never_send(CancellationToken ct)
    {
        var cases = new (
            string Name,
            Func<Harness> Create,
            Input Input,
            ExpectedExperimentCandidate Candidate,
            bool ExplicitProfile,
            string Expected
        )[]
        {
            (
                "source returned another revision",
                () => Routed(Release("r2")),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.InvalidRelease
            ),
            (
                "requested revision unavailable",
                () => Routed(null),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.ResolvedDefault
            ),
            (
                "stale command revision",
                () => Routed(Release("r1", commandRevision: 2)),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.StaleCommandRevision
            ),
            (
                "contract signature mismatch",
                () => Routed(Release("r1", signature: "shape2")),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.InvalidRelease
            ),
            (
                "selector chose another variant",
                () => Routed(Release("r1"), () => new ExperimentSelection.Variant("w")),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.VariantMismatch
            ),
            (
                "selector chose the default",
                () => Routed(Release("r1"), () => new ExperimentSelection.UseConfiguredDefault()),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.ResolvedDefault
            ),
            (
                "selector chose a shadow role",
                () =>
                    Routed(Release("r1"), () =>
                        new ExperimentSelection.Variant("v", ExperimentRole.Shadow)
                    ),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.RoleMismatch
            ),
            (
                "another experiment",
                () => Routed(Release("r1", experiment: "other")),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.ExperimentMismatch
            ),
            (
                "bad override",
                () =>
                    Routed(
                        Release(
                            "r1",
                            [
                                Variant("v") with
                                {
                                    ParameterValues = new Dictionary<string, JsonElement>
                                    {
                                        ["Locale"] = J("7"),
                                    },
                                },
                            ]
                        )
                    ),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.PreparationFailed
            ),
            (
                "failed input validation",
                () => Routed(Release("r1")),
                new("", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.PreparationFailed
            ),
            (
                "blank render",
                () => Routed(Release("r1", [Variant("v", " ", "{Locale}")])),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.PreparationFailed
            ),
            (
                "route policy denied",
                () => Routed(Release("r1"), policy: (_, _) => false),
                new("invoice", "en"),
                Planned,
                false,
                ExperimentCandidateFailureReasons.PreparationFailed
            ),
            (
                "explicit profile replaces the candidate route",
                () => Routed(Release("r1")),
                new("invoice", "en"),
                Planned,
                true,
                ExperimentCandidateFailureReasons.RouteOverridden
            ),
            (
                "default expected but treatment resolved",
                () => Routed(Release("r1")),
                new("invoice", "en"),
                new ExpectedExperimentCandidate.ConfiguredDefault("default-v1"),
                false,
                ExperimentCandidateFailureReasons.ResolvedTreatment
            ),
            (
                "default fingerprint mismatch",
                () => Routed(null),
                new("invoice", "en"),
                new ExpectedExperimentCandidate.ConfiguredDefault("default-v2"),
                false,
                ExperimentCandidateFailureReasons.DefaultFingerprintMismatch
            ),
            (
                "default fingerprint missing",
                () => Routed(null, fingerprint: null),
                new("invoice", "en"),
                new ExpectedExperimentCandidate.ConfiguredDefault("default-v1"),
                false,
                ExperimentCandidateFailureReasons.DefaultFingerprintMismatch
            ),
        };

        var failures = new List<string>();
        foreach (var item in cases)
        {
            using var h = item.Create();
            var invocation = h
                .Command.WithParameters(item.Input)
                .RequireExperimentCandidate(item.Candidate);
            if (item.ExplicitProfile)
            {
                invocation = invocation.WithProfile("base");
            }

            var outcome = await invocation.ExecuteAsync(ct);

            if (Reason(outcome) != item.Expected || h.Sends != 0)
            {
                failures.Add(
                    $"{item.Name}: {Reason(outcome) ?? outcome.GetType().Name}, sends {h.Sends}"
                );
            }
        }

        await Assert.That(string.Join("; ", failures)).IsEqualTo("");
    }

    [Test]
    public async Task Strict_configured_default_runs_only_with_its_expected_fingerprint()
    {
        using var h = Routed(null);

        var outcome = await h
            .Command.WithParameters(new("invoice", "en"))
            .RequireExperimentCandidate(
                new ExpectedExperimentCandidate.ConfiguredDefault("default-v1")
            )
            .ExecuteAsync();

        var success = (Outcome<Result>.Success)outcome;
        await Assert.That(h.Baseline.Calls).IsEqualTo(1);
        await Assert.That(h.Treatment.Calls).IsEqualTo(0);
        await Assert.That(success.Snapshot.Experiment!.Source).IsEqualTo("default");
    }

    [Test]
    public async Task Strict_candidate_requires_experiment_options()
    {
        var (runtime, client, run) = Direct();
        using var _ = runtime;

        var outcome = await new Command(runtime)
            .WithParameters(new("invoice", "en"))
            .InRun(run)
            .RequireExperimentCandidate(Planned)
            .ExecuteAsync();

        await Assert
            .That(Reason(outcome))
            .IsEqualTo(ExperimentCandidateFailureReasons.ExperimentsDisabled);
        await Assert.That(client.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Non_strict_calls_keep_the_ordinary_configured_default_fallback()
    {
        using var h = Routed(Release("r1", commandRevision: 2));

        var outcome = await h.Command.WithParameters(new("invoice", "en")).ExecuteAsync();

        var success = (Outcome<Result>.Success)outcome;
        await Assert.That(success.Snapshot.Experiment!.Reason).IsEqualTo("StaleCommandRevision");
        await Assert.That(h.Baseline.Calls).IsEqualTo(1);
        await Assert.That(h.Treatment.Calls).IsEqualTo(0);
        await Assert.That(h.Source.Requested.Single()).IsNull();
    }

    [Test]
    public async Task Invalid_candidates_are_reported_at_execution_without_sending()
    {
        using var h = Routed(Release("r1"));
        var invocation = h.Command.WithParameters(new("invoice", "en"));

        var missing = await invocation.RequireExperimentCandidate(null!).ExecuteAsync();
        var blank = await invocation
            .RequireExperimentCandidate(
                new ExpectedExperimentCandidate.Treatment(Revision("r1"), " ", "v")
            )
            .ExecuteAsync();
        var blankDefault = await invocation
            .RequireExperimentCandidate(new ExpectedExperimentCandidate.ConfiguredDefault(""))
            .ExecuteAsync();

        await Assert.That(Reason(missing)).IsEqualTo(ExperimentCandidateFailureReasons.Null);
        await Assert.That(Reason(blank)).IsEqualTo(ExperimentCandidateFailureReasons.Invalid);
        await Assert
            .That(Reason(blankDefault))
            .IsEqualTo(ExperimentCandidateFailureReasons.Invalid);
        await Assert.That(h.Sends).IsEqualTo(0);
    }

    [Test]
    public async Task Direct_send_journals_start_and_finish_with_the_explicit_run()
    {
        var (runtime, client, run) = Direct();
        using var _ = runtime;
        var journal = new Journal();

        var outcome = await new Command(runtime)
            .WithParameters(new("invoice", "en"))
            .InRun(run)
            .WithJournal(journal)
            .ExecuteAsync();

        await Assert.That(outcome is Outcome<Result>.Success).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(1);
        await AssertStartAndFinish(journal, outcome.ExecutionId, run);
    }

    [Test]
    public async Task Routed_send_journals_start_and_finish_with_the_explicit_run()
    {
        using var h = Routed(Release("r1"));
        var run = h.Run();
        var journal = new Journal();

        var outcome = await h
            .Command.WithParameters(new("invoice", "en"))
            .InRun(run)
            .WithJournal(journal)
            .RequireExperimentCandidate(Planned)
            .ExecuteAsync();

        await Assert.That(outcome is Outcome<Result>.Success).IsTrue();
        await Assert.That(h.Treatment.Calls).IsEqualTo(1);
        await AssertStartAndFinish(journal, outcome.ExecutionId, run);
        await Assert.That(journal.Events.First().Provider).IsEqualTo("dynamic");
    }

    static async Task AssertStartAndFinish(Journal journal, Guid executionId, Guid run)
    {
        var events = journal.Events.ToArray();
        await Assert
            .That(string.Join(",", events.Select(e => e.Kind)))
            .IsEqualTo("attempt.started,attempt.finished");
        await Assert.That(events.All(e => e.Context.ExecutionId == executionId)).IsTrue();
        await Assert.That(events.All(e => e.Context.Operation.RunId == run)).IsTrue();
        await Assert.That(events[0].AttemptId).IsEqualTo(events[1].AttemptId);
        await Assert.That(events[1].Outcome).IsEqualTo("success");
    }

    [Test]
    public async Task Journal_start_rejection_blocks_direct_and_routed_sends()
    {
        var (runtime, client, run) = Direct();
        using var _ = runtime;
        var direct = await new Command(runtime)
            .WithParameters(new("invoice", "en"))
            .InRun(run)
            .WithJournal(new Journal { RejectStart = true })
            .ExecuteAsync();

        using var h = Routed(Release("r1"));
        var routed = await h
            .Command.WithParameters(new("invoice", "en"))
            .InRun(h.Run())
            .WithJournal(new Journal { RejectStart = true })
            .ExecuteAsync();

        await Assert.That(Reason(direct)).IsEqualTo("Execution journal unavailable");
        await Assert.That(Reason(routed)).IsEqualTo("Execution journal unavailable");
        await Assert.That(client.Calls).IsEqualTo(0);
        await Assert.That(h.Sends).IsEqualTo(0);
    }

    [Test]
    public async Task Journal_finish_failure_is_not_fenced_and_keeps_the_response()
    {
        var (runtime, client, run) = Direct();
        using var _ = runtime;
        var journal = new Journal { RejectFinish = true };

        var outcome = await new Command(runtime)
            .WithParameters(new("invoice", "en"))
            .InRun(run)
            .WithJournal(journal)
            .ExecuteAsync();

        await Assert.That(outcome is Outcome<Result>.Success).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(1);
        await Assert.That(journal.Events.Single().Kind).IsEqualTo("attempt.started");
    }

    [Test]
    public async Task Journal_requires_an_explicit_context()
    {
        var (runtime, client, run) = Direct();
        using var _ = runtime;
        var journal = new Journal();
        var command = new Command(runtime).WithParameters(new("invoice", "en"));

        using (AgentCmdContext.BeginScope(OperationContext.ForRun(run)))
        {
            var ambient = await command.WithJournal(journal).ExecuteAsync();
            var suppressed = await command.WithoutContext().WithJournal(journal).ExecuteAsync();

            await Assert.That(Reason(ambient)).IsEqualTo("journal_requires_explicit_context");
            await Assert.That(Reason(suppressed)).IsEqualTo("journal_requires_explicit_context");
        }

        var missing = await command.InRun(run).WithJournal(null!).ExecuteAsync();

        await Assert.That(Reason(missing)).IsEqualTo("journal_null");
        await Assert.That(client.Calls).IsEqualTo(0);
        await Assert.That(journal.Events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Bindings_branch_immutably()
    {
        using var h = Routed(Release("r1"), () => new ExperimentSelection.Variant("w"));
        var run = h.Run();
        var journal = new Journal();
        var common = h.Command.WithParameters(new("invoice", "en")).InRun(run);
        var journaled = common.WithJournal(journal);
        var strict = common.RequireExperimentCandidate(Planned);

        var plain = await common.ExecuteAsync();
        await Assert.That(plain is Outcome<Result>.Success).IsTrue();
        await Assert.That(journal.Events.Count).IsEqualTo(0);

        var rejected = await strict.ExecuteAsync();
        await Assert
            .That(Reason(rejected))
            .IsEqualTo(ExperimentCandidateFailureReasons.VariantMismatch);
        await Assert.That(journal.Events.Count).IsEqualTo(0);

        var recorded = await journaled.ExecuteAsync();
        await Assert.That(recorded is Outcome<Result>.Success).IsTrue();
        await Assert.That(journal.Events.Count).IsEqualTo(2);

        // Later configuration on a branch keeps its bindings; the shared start stays plain.
        var retuned = await journaled.WithTemperature(.1f).ExecuteAsync();
        await Assert.That(retuned is Outcome<Result>.Success).IsTrue();
        await Assert.That(journal.Events.Count).IsEqualTo(4);
        await Assert.That(h.Sends).IsEqualTo(3);
    }

    [Test]
    public async Task Composition_refuses_standalone_bindings_instead_of_bypassing_its_journal()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "worker");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient("""{"Value":"part"}""");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["extract"] = new("model-a", .2f) },
            ledger,
            () => actor
        );
        var header = PartSlot<SettingsWhole, SettingsPart>.Declare("header", "Header");
        var roots = new RootCommands(
            ledger,
            () => actor,
            new InMemoryCompositionStore(),
            new CompositionRegistry([
                new CompositeDescriptor<SettingsWhole>(
                    "settings.whole",
                    1,
                    "whole-v1",
                    SettingsJson.Default.SettingsWhole,
                    [header.Descriptor]
                ),
            ])
        );
        var root = await roots.BeginAsync<SettingsWhole>(new(run, "input-v1", "bound"));
        root.WithPartFrom(
            header,
            new SettingsCommand(runtime).WithParameters(new("document")).WithJournal(new Journal())
        );

        var outcome = await root.ExecuteAsync();

        var steps = outcome switch
        {
            CompositeOutcome<SettingsWhole>.Incomplete incomplete => incomplete.Steps,
            CompositeOutcome<SettingsWhole>.Conflict conflict => conflict.Steps,
            CompositeOutcome<SettingsWhole>.Unavailable unavailable => unavailable.Steps,
            _ => [],
        };
        var reasons = steps
            .Select(step => (step.Failure as ExecutionFailure.ConfigurationFailure)?.Reason)
            .ToArray();
        await Assert.That(reasons).Contains("composition_binding_conflict");
        await Assert.That(client.Calls).IsEqualTo(0);
    }
}
