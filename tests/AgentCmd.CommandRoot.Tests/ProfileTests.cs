using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using CommandRoot;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

[NotInParallel]
public class ProfileTests
{
    static ChatResponse Answer(string model, decimal total = 12) =>
        new(new ChatMessage(ChatRole.Assistant, $"{{\"Total\":{total},\"Currency\":\"USD\"}}"))
        {
            ModelId = model,
            ResponseId = "synthetic-" + model,
            Usage = new() { InputTokenCount = 7, OutputTokenCount = 3 },
        };

    static Task<ChatResponse> Unavailable() =>
        Task.FromException<ChatResponse>(
            new HttpRequestException(
                "synthetic unavailable",
                null,
                HttpStatusCode.ServiceUnavailable
            )
        );

    sealed class Client(Func<ChatOptions?, CancellationToken, Task<ChatResponse>> run) : IChatClient
    {
        public int Calls;
        public int Disposals;
        public ConcurrentQueue<(string Prompt, string? Model, float? Temperature)> Inputs = new();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Interlocked.Increment(ref Calls);
            Inputs.Enqueue(
                (
                    string.Join("|", messages.Select(m => m.Text)),
                    options?.ModelId,
                    options?.Temperature
                )
            );
            return run(options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type type, object? key = null) => null;

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    sealed class Fixture : IDisposable
    {
        public Client OpenAI { get; }
        public Client Anthropic { get; }
        public ProfileRouter Router { get; }
        public CommandRuntime Runtime { get; }
        public Guid Run { get; }
        public ConcurrentQueue<ExecutionOutcomeRecord> Records { get; } = new();

        public Fixture(
            Client openAI,
            Client anthropic,
            bool fallback = true,
            int retries = 2,
            ExecutionBudget? budget = null,
            RetryPolicy? policy = null
        )
        {
            OpenAI = openAI;
            Anthropic = anthropic;
            var catalog = Profiles.Instance;
            var built = ProfileRouter.TryCreate(
                new[]
                {
                    new ExecutionProfile(
                        catalog.OpenAI,
                        "openai",
                        "openai-model",
                        .2f,
                        policy ?? new(retries, TimeSpan.Zero, false),
                        fallback ? catalog.Anthropic : null
                    ),
                    new ExecutionProfile(catalog.Anthropic, "anthropic", "anthropic-model", .8f),
                },
                new Dictionary<string, ProviderRegistration>
                {
                    ["openai"] = new(() => openAI),
                    ["anthropic"] = new(() => anthropic),
                },
                budget
            );
            Router = ((RouterBuildResult.Ready)built).Router;
            var ledger = new InMemoryFeedbackLedger();
            var actor = new Actor("profile-test", "reviewer");
            Run = ledger.CreateRun(actor);
            Runtime = CommandRuntime.Create(Router, ledger, () => actor, Records.Enqueue);
        }

        public Invocation<InvoiceParameters, Invoice> Invocation =>
            new ExtractInvoice(Runtime).WithParameters(new("same synthetic invoice")).InRun(Run);

        public void Dispose()
        {
            Runtime.Dispose();
            Router.Dispose();
        }
    }

    [Test]
    public async Task Same_generated_command_executes_on_two_platforms_with_inferred_output()
    {
        using var f = new Fixture(
            new((_, _) => Task.FromResult(Answer("openai-response", 10))),
            new((_, _) => Task.FromResult(Answer("anthropic-response", 20))),
            fallback: false,
            retries: 0
        );
        var invocation = f.Invocation;
        Invocation<InvoiceParameters, Invoice> open = invocation.WithProfile<Profiles>(p =>
            p.OpenAI
        );
        Outcome<Invoice> a = await open.ExecuteAsync();
        Outcome<Invoice> b = await invocation
            .WithProfile<Profiles>(p => p.Anthropic)
            .ExecuteAsync();
        var first = (Outcome<Invoice>.Success)a;
        var second = (Outcome<Invoice>.Success)b;
        await Assert.That(first.Value.Total).IsEqualTo(10m);
        await Assert.That(second.Value.Total).IsEqualTo(20m);
        await Assert.That(first.Snapshot.DefinitionHash).IsEqualTo(second.Snapshot.DefinitionHash);
        await Assert
            .That(first.Snapshot.FormattedPrompt)
            .IsEqualTo(second.Snapshot.FormattedPrompt);
        await Assert
            .That(first.Snapshot.Routing!.ServedProfile)
            .IsEqualTo(Profiles.Instance.OpenAI.Id);
        await Assert
            .That(second.Snapshot.Routing!.ServedProfile)
            .IsEqualTo(Profiles.Instance.Anthropic.Id);
        await Assert.That(first.RunId).IsEqualTo(second.RunId);
        await Assert.That(first.ExecutionId == second.ExecutionId).IsFalse();
        await Assert.That(f.OpenAI.Inputs.Single().Model).IsEqualTo("openai-model");
        await Assert.That(f.Anthropic.Inputs.Single().Model).IsEqualTo("anthropic-model");
        await Assert.That(f.Records.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Retry_and_fallback_emit_one_transition_and_separate_attempt_spans_and_metrics()
    {
        var spans = new ConcurrentQueue<Activity>();
        var points = new ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Zeeq.AgentCmd" or ProfileTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, l) =>
        {
            if (
                instrument.Meter.Name == ProfileTelemetry.SourceName
                && instrument.Name == "agentcmd.profile.fallbacks"
            )
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<long>(
            (_, value, tags, _) =>
                points.Enqueue((value, tags.ToArray().ToDictionary(x => x.Key, x => x.Value)))
        );
        meter.Start();
        using var f = new Fixture(
            new((_, _) => Unavailable()),
            new((_, _) => Task.FromResult(Answer("anthropic-response")))
        );
        var result = (Outcome<Invoice>.Success)
            await f
                .Invocation.WithProfile<Profiles>(p => p.OpenAI)
                .WithTemperature(.1f)
                .ExecuteAsync();
        var capture = result.Snapshot.Routing!;
        await Assert.That(capture.Attempts.Count).IsEqualTo(4);
        await Assert
            .That(string.Join(",", capture.Attempts.Select(a => a.Kind)))
            .IsEqualTo("initial,retry,retry,fallback");
        await Assert.That(capture.Attempts.All(a => a.Temperature == .1f)).IsTrue();
        await Assert
            .That(capture.Attempts.Take(3).All(a => a.InputTokens is null && a.Outcome == "failed"))
            .IsTrue();
        await Assert.That(capture.Attempts[3].InputTokens).IsEqualTo(7L);
        var parent = spans.Single(s => s.OperationName == "agentcmd.execute");
        var children = spans.Where(s => s.OperationName == "agentcmd.provider.attempt").ToArray();
        await Assert.That(children.Length).IsEqualTo(4);
        await Assert
            .That(children.All(s => s.ParentSpanId == parent.SpanId && s.TraceId == parent.TraceId))
            .IsTrue();
        await Assert.That(children.Count(s => s.Status == ActivityStatusCode.Error)).IsEqualTo(3);
        await Assert.That(parent.Status == ActivityStatusCode.Error).IsFalse();
        var transition = parent.Events.Single(e => e.Name == "agentcmd.profile.fallback");
        await Assert
            .That(transition.Tags.Single(t => t.Key == "agentcmd.profile.to").Value?.ToString())
            .IsEqualTo(Profiles.Instance.Anthropic.Id);
        await Assert.That(points.Count).IsEqualTo(1);
        await Assert.That(points.Single().Value).IsEqualTo(1L);
        await Assert
            .That(points.Single().Tags["agentcmd.fallback.reason"]?.ToString())
            .IsEqualTo("Transient");
        await Assert
            .That(
                points
                    .Single()
                    .Tags.Keys.Any(k =>
                        k.Contains("run")
                        || k.Contains("execution")
                        || k.Contains("trace")
                        || k.Contains("prompt")
                    )
            )
            .IsFalse();
        await Assert.That(f.Records.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_retry_can_recover_without_fallback()
    {
        int calls = 0;
        using var f = new Fixture(
            new(
                (_, _) =>
                    Interlocked.Increment(ref calls) == 1
                        ? Unavailable()
                        : Task.FromResult(Answer("openai-response"))
            ),
            new((_, _) => Task.FromResult(Answer("anthropic-response")))
        );
        var result = (Outcome<Invoice>.Success)
            await f.Invocation.WithProfile<Profiles>(p => p.OpenAI).ExecuteAsync();
        await Assert.That(result.Snapshot.Routing!.Attempts.Count).IsEqualTo(2);
        await Assert
            .That(result.Snapshot.Routing.ServedProfile)
            .IsEqualTo(Profiles.Instance.OpenAI.Id);
        await Assert.That(f.Anthropic.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Permanent_failure_does_not_retry_or_fallback_and_retains_capture()
    {
        using var f = new Fixture(
            new(
                (_, _) =>
                    Task.FromException<ChatResponse>(
                        new HttpRequestException("unauthorized", null, HttpStatusCode.Unauthorized)
                    )
            ),
            new((_, _) => Task.FromResult(Answer("anthropic-response")))
        );
        var result = (ExecutionFailure.ProviderFailure)
            (
                (Outcome<Invoice>.Failed)
                    await f.Invocation.WithProfile<Profiles>(p => p.OpenAI).ExecuteAsync()
            ).Failure;
        await Assert.That(f.OpenAI.Calls).IsEqualTo(1);
        await Assert.That(f.Anthropic.Calls).IsEqualTo(0);
        await Assert
            .That(result.Snapshot!.Routing!.Attempts.Single().Failure)
            .IsEqualTo(FailureKind.Permanent);
        await Assert.That(f.Records.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Caller_cancellation_stops_without_fallback()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var f = new Fixture(
            new((_, _) => Unavailable()),
            new((_, _) => Task.FromResult(Answer("anthropic-response")))
        );
        var result = await f
            .Invocation.WithProfile<Profiles>(p => p.OpenAI)
            .ExecuteAsync(source.Token);
        await Assert
            .That(result is Outcome<Invoice>.Failed { Failure: ExecutionFailure.Cancelled })
            .IsTrue();
        await Assert.That(f.OpenAI.Calls).IsEqualTo(0);
        await Assert.That(f.Anthropic.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Unrequested_provider_cancellation_is_timeout_and_can_fallback()
    {
        using var f = new Fixture(
            new((_, _) => Task.FromException<ChatResponse>(new OperationCanceledException())),
            new((_, _) => Task.FromResult(Answer("anthropic-response"))),
            retries: 0
        );
        var result = (Outcome<Invoice>.Success)
            await f.Invocation.WithProfile<Profiles>(p => p.OpenAI).ExecuteAsync();
        await Assert
            .That(result.Snapshot.Routing!.Attempts[0].Failure)
            .IsEqualTo(FailureKind.Timeout);
        await Assert.That(result.Snapshot.Routing.Attempts.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Caller_cancelled_during_synchronous_completion_stops_but_keeps_observed_usage()
    {
        using var source = new CancellationTokenSource();
        using var f = new Fixture(
            new(
                (_, _) =>
                {
                    source.Cancel();
                    return Task.FromResult(Answer("late-openai-response"));
                }
            ),
            new((_, _) => Task.FromResult(Answer("anthropic-response")))
        );
        var result = (ExecutionFailure.Cancelled)
            (
                (Outcome<Invoice>.Failed)
                    await f
                        .Invocation.WithProfile<Profiles>(p => p.OpenAI)
                        .ExecuteAsync(source.Token)
            ).Failure;
        await Assert.That(f.Anthropic.Calls).IsEqualTo(0);
        await Assert
            .That(result.Snapshot!.Routing!.Attempts.Single().Outcome)
            .IsEqualTo("cancelled");
        await Assert.That(result.Snapshot.Routing.Attempts.Single().InputTokens).IsEqualTo(7L);
    }

    [Test]
    public async Task Exhausted_attempt_budget_does_not_wait_for_a_retry_delay()
    {
        using var f = new Fixture(
            new((_, _) => Unavailable()),
            new((_, _) => Task.FromResult(Answer("anthropic-response"))),
            budget: new(MaxAttempts: 1),
            policy: new(MaxRetries: 2, InitialDelay: TimeSpan.FromSeconds(20), UseJitter: false)
        );
        var timer = Stopwatch.StartNew();
        var result = (ExecutionFailure.ProviderFailure)
            (
                (Outcome<Invoice>.Failed)
                    await f.Invocation.WithProfile<Profiles>(p => p.OpenAI).ExecuteAsync()
            ).Failure;
        await Assert.That(timer.Elapsed < TimeSpan.FromSeconds(5)).IsTrue();
        await Assert.That(result.Snapshot!.Routing!.Attempts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Total_attempt_budget_does_not_restart_on_fallback()
    {
        using var f = new Fixture(
            new((_, _) => Unavailable()),
            new((_, _) => Task.FromResult(Answer("anthropic-response"))),
            budget: new(MaxAttempts: 2)
        );
        var result = (ExecutionFailure.ProviderFailure)
            (
                (Outcome<Invoice>.Failed)
                    await f.Invocation.WithProfile<Profiles>(p => p.OpenAI).ExecuteAsync()
            ).Failure;
        await Assert.That(result.Snapshot!.Routing!.Attempts.Count).IsEqualTo(2);
        await Assert.That(f.Anthropic.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Execution_deadline_bounds_noncooperative_async_provider_and_is_not_caller_cancel()
    {
        var pending = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var f = new Fixture(
            new((_, _) => pending.Task),
            new((_, _) => Task.FromResult(Answer("anthropic-response"))),
            budget: new(Timeout: TimeSpan.FromMilliseconds(30))
        );
        var timer = Stopwatch.StartNew();
        var result = (ExecutionFailure.ProviderFailure)
            (
                (Outcome<Invoice>.Failed)
                    await f.Invocation.WithProfile<Profiles>(p => p.OpenAI).ExecuteAsync()
            ).Failure;
        await Assert.That(timer.Elapsed < TimeSpan.FromSeconds(5)).IsTrue();
        await Assert
            .That(result.Snapshot!.Routing!.Attempts[0].Failure)
            .IsEqualTo(FailureKind.Timeout);
        await Assert.That(f.Anthropic.Calls).IsEqualTo(0);
        pending.SetException(new InvalidOperationException("late synthetic fault"));
    }

    [Test]
    public async Task Concurrent_selections_keep_independent_capture_and_settings()
    {
        using var f = new Fixture(
            new(
                async (_, _) =>
                {
                    await Task.Yield();
                    return Answer("openai-response");
                }
            ),
            new(
                async (_, _) =>
                {
                    await Task.Yield();
                    return Answer("anthropic-response");
                }
            ),
            fallback: false
        );
        var input = f.Invocation;
        var results = await Task.WhenAll(
            input.WithProfile<Profiles>(p => p.OpenAI).WithTemperature(.1f).ExecuteAsync(),
            input.WithProfile<Profiles>(p => p.Anthropic).WithTemperature(.9f).ExecuteAsync()
        );
        await Assert
            .That(
                ((Outcome<Invoice>.Success)results[0]).Snapshot.Routing!.Attempts.Single().Provider
            )
            .IsEqualTo("openai");
        await Assert
            .That(
                ((Outcome<Invoice>.Success)results[1]).Snapshot.Routing!.Attempts.Single().Provider
            )
            .IsEqualTo("anthropic");
        await Assert.That(f.OpenAI.Inputs.Single().Temperature).IsEqualTo(.1f);
        await Assert.That(f.Anthropic.Inputs.Single().Temperature).IsEqualTo(.9f);
    }

    [Test]
    public async Task Invalid_graph_is_rejected_before_creating_provider_clients()
    {
        int factories = 0;
        var c = Profiles.Instance;
        var registrations = new Dictionary<string, ProviderRegistration>
        {
            ["openai"] = new(() =>
            {
                factories++;
                return new Client((_, _) => Unavailable());
            }),
        };
        var cycle = ProfileRouter.TryCreate(
            new[]
            {
                new ExecutionProfile(c.OpenAI, "openai", "a", Fallback: c.Anthropic),
                new ExecutionProfile(c.Anthropic, "openai", "b", Fallback: c.OpenAI),
            },
            registrations
        );
        var missing = ProfileRouter.TryCreate(
            new[] { new ExecutionProfile(c.OpenAI, "missing", "a", Fallback: c.Anthropic) },
            registrations
        );
        await Assert.That(cycle is RouterBuildResult.Invalid).IsTrue();
        await Assert.That(missing is RouterBuildResult.Invalid).IsTrue();
        await Assert.That(factories).IsEqualTo(0);
    }

    [Test]
    public async Task Profile_selector_runs_once_and_only_retains_selected_key()
    {
        using var f = new Fixture(
            new((_, _) => Task.FromResult(Answer("openai-response"))),
            new((_, _) => Task.FromResult(Answer("anthropic-response"))),
            fallback: false
        );
        int selected = 0;
        var invocation = f.Invocation.WithProfile<Profiles>(p =>
        {
            selected++;
            return p.OpenAI;
        });
        await Assert.That(selected).IsEqualTo(1);
        await invocation.ExecuteAsync();
        await invocation.ExecuteAsync();
        await Assert.That(selected).IsEqualTo(1);
        await Assert.That(f.OpenAI.Calls).IsEqualTo(2);
    }
}
