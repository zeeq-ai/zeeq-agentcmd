using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.ControlPlane;

namespace AgentCmd.ControlPlane.Tests;

public class JobTypedWorkerTests
{
    private sealed record Input(int Value);

    private sealed record Result(string Value);

    private static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private sealed class Binding : IRuntimeBinding<Input>
    {
        public string ContractSignature => "sig";

        public IReadOnlyDictionary<string, string?> Freeze(Input input) =>
            new Dictionary<string, string?>
            {
                ["Value"] = input.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

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
            error = overrides.Count == 0 ? null : "override_not_allowed";
            return error is null;
        }
    }

    private static readonly PromptDescriptor<Input, Result> Descriptor = new(
        "c",
        1,
        "base",
        "",
        "base {Value}",
        "definition",
        "result",
        new(() => J("{\"type\":\"object\"}")),
        (JsonTypeInfo<Result>)JsonSerializerOptions.Default.GetTypeInfo(typeof(Result)),
        p => $"base {p.Value}",
        _ => "",
        _ => [],
        p => p.Value > 0 ? null : "invalid",
        _ => null,
        RuntimeBinding: new Binding()
    );

    private sealed class Command(CommandRuntime runtime)
        : PromptCommand<Input, Result>(runtime, Descriptor);

    private sealed class Client : IChatClient
    {
        public int Calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
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

    private sealed class Source(ExperimentSnapshot snapshot) : IExperimentSnapshotSource
    {
        public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                new SourceResult<ExperimentSnapshot>.Found(snapshot)
            );
    }

    private sealed class Preparation(ExperimentRevision revision, Guid runId)
        : ITypedTreatmentPreparation<Input>
    {
        public Task<PreparedWorkerTreatment> PrepareAsync(
            WorkspaceScope scope,
            Input input,
            JsonElement request,
            CancellationToken cancellationToken
        )
        {
            var pin = request.GetProperty("pins")[0];
            return Task.FromResult(
                new PreparedWorkerTreatment(
                    revision,
                    "trial",
                    "v",
                    pin.GetProperty("publicationHash").GetString()!,
                    pin.GetProperty("buildHash").GetString()!,
                    runId
                )
            );
        }
    }

    [Test]
    public async Task ExactTypedPreflightAndExecutionUseStrictCandidateAndJournal()
    {
        var (store, _, job) = await JobTransitionsTests.CreateAsync();
        var scope = new WorkspaceScope("app", "test", "one");
        var worker = new ManagementActor("worker");
        var claim = (
            await store.ClaimWorkAsync(
                scope,
                worker,
                "build",
                worker.Subject,
                TimeSpan.FromMinutes(1)
            )
        )!;
        ExperimentRevision.TryCreate("r1", out var revision);
        var variant = new ExperimentVariant(
            "v",
            1,
            new(new TemplateChange.Inherited(), new TemplateChange.Replaced("treatment {Value}")),
            new Dictionary<string, JsonElement>(),
            null
        );
        var view = ExperimentCatalogView.Freeze("c", 1, "sig", revision!, "trial", "v", [variant]);
        var snapshot = new ExperimentSnapshot(
            revision!,
            new Dictionary<string, ExperimentCatalogView> { ["c"] = view }
        );
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "app");
        var runId = ledger.CreateRun(actor);
        var client = new Client();
        var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { ["base"] = new("model", 0.2f) },
            ledger,
            () => actor,
            experiments: new ExperimentRuntimeOptions(new ExperimentResolver(new Source(snapshot)))
        );
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var build = (await store.ReadAsync(scope, "builds", "build"))!;
        var incorrect = new TypedWorkerAdapter<Input, Result>(
            new Command(runtime),
            (JsonTypeInfo<Input>)options.GetTypeInfo(typeof(Input)),
            "build",
            "wrong-digest",
            new Preparation(revision!, runId)
        );
        await Assert.That((await incorrect.CheckAsync(scope, claim, default)).Passed).IsFalse();
        await Assert.That(client.Calls).IsEqualTo(0);
        ExperimentRevision.TryCreate("wrong-revision", out var wrongRevision);
        var stale = new TypedWorkerAdapter<Input, Result>(
            new Command(runtime),
            (JsonTypeInfo<Input>)options.GetTypeInfo(typeof(Input)),
            "build",
            build.ContentHash,
            new Preparation(wrongRevision!, runId)
        );
        await Assert.That((await stale.CheckAsync(scope, claim, default)).Passed).IsFalse();
        await Assert.That(client.Calls).IsEqualTo(0);
        var adapter = new TypedWorkerAdapter<Input, Result>(
            new Command(runtime),
            (JsonTypeInfo<Input>)options.GetTypeInfo(typeof(Input)),
            "build",
            build.ContentHash,
            new Preparation(revision!, runId)
        );
        var check = await adapter.CheckAsync(scope, claim, default);
        await Assert.That(check.Passed).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(0);
        await store.RecordPreflightAsync(scope, worker, claim, check);
        var outcome = await adapter.ExecuteAndCompleteAsync(scope, worker, claim, store);
        await Assert.That(outcome is Outcome<Result>.Success).IsTrue();
        await Assert.That(client.Calls).IsEqualTo(1);
        var progress = await store.GetJobAsync(scope, job.Id);
        var work = progress.Work.Single();
        await Assert.That(progress.Job.State).IsEqualTo("Finished");
        await Assert.That(work.Status).IsEqualTo("Finished");
        await Assert
            .That(work.Result!.Value.GetProperty("executionId").GetString())
            .IsEqualTo(outcome.ExecutionId.ToString());
        await Assert
            .That(work.Result.Value.GetProperty("storageStatus").GetString())
            .IsEqualTo("Stored");
        await Assert
            .That(work.Result.Value.GetProperty("comparisonEligible").GetBoolean())
            .IsTrue();
        await Assert
            .That(work.Result.Value.GetProperty("executionElapsedSeconds").GetDouble())
            .IsGreaterThanOrEqualTo(0);
        await Assert
            .That(work.Result.Value.GetProperty("elapsedSource").GetString())
            .IsEqualTo("CapturedInvocationElapsed");
        var publicationId = claim
            .Request.GetProperty("candidate")
            .GetProperty("publicationId")
            .GetString()!;
        var secondJob = await store.CreateBacktestAsync(
            scope,
            worker,
            "second",
            new CreateJobRequest(
                "Backtest",
                job.Body.GetProperty("datasetId").GetString()!,
                job.Body.GetProperty("benchmarkId").GetString()!,
                [new JobCandidate("candidate", "Candidate", "build", publicationId, "c", "v")],
                true
            )
        );
        var dispatcher = InProcessEvaluationWorker.ForCommand(store, worker, adapter);
        await Assert
            .That(
                (
                    await dispatcher.DispatchOnceAsync(
                        scope,
                        "other-build",
                        TimeSpan.FromMinutes(1)
                    )
                ).Status
            )
            .IsEqualTo("NoWork");
        await Assert
            .That(
                (await dispatcher.DispatchOnceAsync(scope, "build", TimeSpan.FromMinutes(1))).Status
            )
            .IsEqualTo("Completed");
        await Assert
            .That((await store.GetJobAsync(scope, secondJob.Id)).Job.State)
            .IsEqualTo("Finished");
        await Assert.That(client.Calls).IsEqualTo(2);
    }
}
