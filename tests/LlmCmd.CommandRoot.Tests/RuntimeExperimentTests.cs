using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CommandRoot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.CommandRoot.Tests;

public class RuntimeExperimentTests
{
    [Test]
    public async Task CommandTraceIdentifiesTheSelectedVariantWithoutPromptData()
    {
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Zeeq.LlmCmd",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var source = new Source { Snapshot = Release("trace-v1", "fr") };
        using var provider = Build(source, new Client(), new Client(), (_, _) => true);
        var command = new Command(provider.GetRequiredService<CommandRuntime>());

        var outcome = (Outcome<Result>.Success)
            await command.WithParameters(new("private input", "en")).ExecuteAsync();

        var span = spans.Single(activity =>
            activity.OperationName == "llmcmd.execute"
            && activity.GetTagItem("llmcmd.execution.id")?.ToString()
                == outcome.ExecutionId.ToString()
        );
        await Assert
            .That(span.GetTagItem("llmcmd.experiment.source")?.ToString())
            .IsEqualTo("experiment");
        await Assert
            .That(span.GetTagItem("llmcmd.experiment.revision")?.ToString())
            .IsEqualTo("trace-v1");
        await Assert.That(span.GetTagItem("llmcmd.experiment.id")?.ToString()).IsEqualTo("trial");
        await Assert.That(span.GetTagItem("llmcmd.experiment.variant")?.ToString()).IsEqualTo("v");
        await Assert
            .That(span.GetTagItem("llmcmd.experiment.role")?.ToString())
            .IsEqualTo("primary");
        await Assert
            .That(
                span.TagObjects.Any(tag => tag.Value?.ToString()?.Contains("private input") == true)
            )
            .IsFalse();
    }

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

    sealed class Command(CommandRuntime runtime) : PromptCommand<Input, Result>(runtime, Descriptor)
    {
        public static readonly PromptDescriptor<Input, Result> Descriptor = new(
            "experiment.test",
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
    }

    sealed class CommandV2(CommandRuntime runtime)
        : PromptCommand<Input, Result>(runtime, Command.Descriptor with { Revision = 2 });

    sealed class PromptOnlyCommand(CommandRuntime runtime)
        : PromptCommand<Input, Result>(
            runtime,
            Command.Descriptor with
            {
                UserTemplate = "{Text}",
                Render = input => input.Text,
                ValidateInput = _ => null,
            }
        );

    sealed class Client : IChatClient
    {
        public ConcurrentQueue<(string Prompt, string? Model, float? Temperature)> Calls { get; } =
            new();
        public string Response { get; set; } = "{\"Value\":\"ok\"}";
        public bool Fail { get; set; }
        public bool FailTransient { get; set; }
        public Action? OnCall { get; set; }
        public TaskCompletionSource? Entered { get; set; }
        public TaskCompletionSource? Release { get; set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            OnCall?.Invoke();
            Calls.Enqueue(
                (
                    string.Join("|", messages.Select(m => m.Text)),
                    options?.ModelId,
                    options?.Temperature
                )
            );
            Entered?.TrySetResult();
            if (Release is not null)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            if (FailTransient)
            {
                throw new HttpRequestException(
                    "synthetic",
                    null,
                    System.Net.HttpStatusCode.ServiceUnavailable
                );
            }
            if (Fail)
            {
                throw new InvalidOperationException("synthetic failure");
            }
            return new(new ChatMessage(ChatRole.Assistant, Response));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    sealed class Source : IExperimentSnapshotSource
    {
        public ExperimentSnapshot? Snapshot;
        public int Reads;

        public ValueTask<SourceResult<ExperimentSnapshot>> GetAsync(
            ExperimentLookup lookup,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref Reads);
            return ValueTask.FromResult<SourceResult<ExperimentSnapshot>>(
                Snapshot is null
                    ? new SourceResult<ExperimentSnapshot>.Unavailable(SourceFailure.Unavailable)
                    : new SourceResult<ExperimentSnapshot>.Found(Snapshot)
            );
        }
    }

    sealed class CountingScheduler : IShadowScheduler
    {
        public int Offered;

        public bool TrySchedule(ShadowWork work)
        {
            Interlocked.Increment(ref Offered);
            return false;
        }
    }

    static ExperimentSnapshot Release(
        string version,
        string locale,
        string template = "new {Text} {Locale}",
        string model = "new-model",
        ExperimentCommandContract? contract = null
    )
    {
        ExperimentRevision.TryCreate(version, out var revision);
        var variant = new ExperimentVariant(
            "v",
            1,
            new(new TemplateChange.Inherited(), new TemplateChange.Replaced(template)),
            new Dictionary<string, JsonElement>
            {
                ["Locale"] = J(JsonSerializer.Serialize(locale)),
            },
            new(ProfileKey.Declare("dynamic"), "dynamic", model, Defaults: new(Temperature: .3f))
        );
        var view = ExperimentCatalogView.Freeze(
            contract?.CommandId ?? "experiment.test",
            contract?.CommandRevision ?? 1,
            contract?.ContractSignature ?? "shape1",
            revision!,
            "trial",
            "v",
            [variant]
        );
        return new(
            revision!,
            new Dictionary<string, ExperimentCatalogView> { [view.CommandId] = view }
        );
    }

    static ServiceProvider Build(
        Source source,
        Client baseline,
        Client treatment,
        Func<Actor, ExecutionProfile, bool>? policy = null,
        string? fixedModel = null
    )
    {
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment, fixedModel)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(
                    new ExperimentRuntimeOptions(
                        new ExperimentResolver(source),
                        RoutePolicy: policy
                    )
                )
        );
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task EffectiveUserPromptIsCheckedAfterTreatmentSelection()
    {
        var source = new Source { Snapshot = Release("v1", "fr", "treatment {Locale}") };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new PromptOnlyCommand(provider.GetRequiredService<CommandRuntime>());

        var repaired = await command.WithParameters(new(" ", "en")).ExecuteAsync();
        await Assert.That(repaired is Outcome<Result>.Success).IsTrue();
        await Assert.That(treatment.Calls.Single().Prompt).IsEqualTo("treatment fr");
        await Assert.That(baseline.Calls.Count).IsEqualTo(0);

        source.Snapshot = Release("v2", " \t", "{Locale}");
        var fallback = (Outcome<Result>.Success)
            await command.WithParameters(new(" invoice ", "en")).ExecuteAsync();
        await Assert.That(fallback.Snapshot.Experiment!.Reason).IsEqualTo("InvalidRelease");
        await Assert.That(baseline.Calls.Single().Prompt).IsEqualTo(" invoice ");
        await Assert.That(treatment.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BlankTreatmentAndDefaultNeverOfferShadowsOrSend()
    {
        var source = new Source { Snapshot = Release("v1", " \t", "{Locale}") };
        var scheduler = new CountingScheduler();
        var baseline = new Client();
        var treatment = new Client();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(sp => new ExperimentRuntimeOptions(
                    new ExperimentResolver(source),
                    RoutePolicy: (_, _) => true,
                    Scheduler: scheduler,
                    ShadowScopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
                    SelectShadows: (_, _) => ["v"],
                    ShadowPolicy: (_, _) => true
                ))
        );
        using var provider = services.BuildServiceProvider();
        var result = await new PromptOnlyCommand(provider.GetRequiredService<CommandRuntime>())
            .WithParameters(new("\u2003", "en"))
            .ExecuteAsync();

        await Assert
            .That(result is Outcome<Result>.Failed { Failure: ExecutionFailure.InvalidInput })
            .IsTrue();
        await Assert.That(scheduler.Offered).IsEqualTo(0);
        await Assert.That(treatment.Calls.Count + baseline.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BlankUserReplacementFallsBackButWhitespaceSystemReplacementIsPreserved()
    {
        var source = new Source { Snapshot = Release("v1", "fr", " \t") };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new PromptOnlyCommand(provider.GetRequiredService<CommandRuntime>());

        var fallback = (Outcome<Result>.Success)
            await command.WithParameters(new("invoice", "en")).ExecuteAsync();
        await Assert.That(fallback.Snapshot.Experiment!.Reason).IsEqualTo("InvalidRelease");
        await Assert.That(baseline.Calls.Single().Prompt).IsEqualTo("invoice");
        await Assert.That(treatment.Calls.Count).IsEqualTo(0);

        var release = Release("v2", "fr", "treatment {Text}");
        var view = release.Commands["experiment.test"];
        var variant = view.Variants["v"] with
        {
            Prompt = new(
                new TemplateChange.Replaced(" \t "),
                new TemplateChange.Replaced("treatment {Text}")
            ),
        };
        source.Snapshot = release with
        {
            Commands = new Dictionary<string, ExperimentCatalogView>
            {
                [view.CommandId] = view with
                {
                    Variants = new Dictionary<string, ExperimentVariant> { ["v"] = variant },
                },
            },
        };
        var selected = await command.WithParameters(new("invoice", "en")).ExecuteAsync();
        await Assert.That(selected is Outcome<Result>.Success).IsTrue();
        await Assert.That(treatment.Calls.Single().Prompt).IsEqualTo(" \t |treatment invoice");
    }

    [Test]
    public async Task BlankConfiguredDefaultIsRejectedWithoutSending()
    {
        var source = new Source { Snapshot = Release("v1", "fr") };
        var baseline = new Client();
        var treatment = new Client();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(
                    new ExperimentRuntimeOptions(
                        new ExperimentResolver(source),
                        ConfigureDefault: view => view with { UserTemplate = " \t" },
                        RoutePolicy: (_, _) => true
                    )
                )
        );
        using var provider = services.BuildServiceProvider();
        var result = await new PromptOnlyCommand(provider.GetRequiredService<CommandRuntime>())
            .WithParameters(new("invoice", "en"))
            .ExecuteAsync();

        await Assert
            .That(
                result is Outcome<Result>.Failed { Failure: ExecutionFailure.ConfigurationFailure }
            )
            .IsTrue();
        await Assert.That(baseline.Calls.Count + treatment.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task UpdatesRouteAndTypedOverridesAndStoresEvidence()
    {
        var source = new Source();
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        source.Snapshot = Release("v1", "fr", contract: command.ExperimentContract);
        var first = (Outcome<Result>.Success)
            await command.WithParameters(new("input", "en")).ExecuteAsync();
        await Assert.That(command.ExperimentContract!.ContractSignature).IsEqualTo("shape1");
        source.Snapshot = Release(
            "v2",
            "de",
            "updated {Text} {Locale}",
            "model-v2",
            command.ExperimentContract
        );
        var second = (Outcome<Result>.Success)
            await command.WithParameters(new("input", "en")).ExecuteAsync();
        await Assert
            .That(
                treatment
                    .Calls.Select(x => x.Prompt)
                    .SequenceEqual(["new input fr", "updated input de"])
            )
            .IsTrue();
        await Assert.That(treatment.Calls.Last().Model).IsEqualTo("model-v2");
        await Assert.That(treatment.Calls.Last().Temperature).IsEqualTo(.3f);
        var artifact = ((OutputDelivery.Stored)second.Delivery).Output;
        await Assert.That(second.Snapshot.RawTemplate).IsEqualTo("updated {Text} {Locale}");
        await Assert.That(second.Snapshot.FormattedPrompt).IsEqualTo("updated input de");
        await Assert
            .That(second.Snapshot.Parameters.Single(x => x.Name == "Locale").Value.GetString())
            .IsEqualTo("de");
        var ledger = provider.GetRequiredService<IHumanFeedbackLedger>();
        var stored = (
            (LedgerResult<OutputArtifact>.Accepted)
                ledger.ReadOutput(artifact.Reference, new Actor("tenant", "user"))
        ).Value;
        await Assert.That(stored.Value.GetProperty("Value").GetString()).IsEqualTo("ok");
        await Assert.That(stored.Evidence!.Revision).IsEqualTo("v2");
        await Assert
            .That(stored.Evidence.Attempts!.Single().RequestSettings!.Temperature)
            .IsEqualTo(.3f);
        await Assert.That(artifact.Evidence!.Revision).IsEqualTo("v2");
        await Assert
            .That(artifact.Evidence.Parameters!.Single(x => x.Name == "Locale").Origin)
            .IsEqualTo("override");
        await Assert.That(first.Snapshot.Experiment!.CommandId).IsEqualTo("experiment.test");
    }

    [Test]
    public async Task SourceFailureAndInvalidOrDeniedRouteUseWholeDefault()
    {
        var source = new Source();
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => false);
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var fallback = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        source.Snapshot = Release("v1", "fr");
        var denied = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        await Assert.That(baseline.Calls.All(c => c.Prompt == "base x en")).IsTrue();
        await Assert.That(treatment.Calls.Count).IsEqualTo(0);
        await Assert.That(fallback.Snapshot.Experiment!.Reason).IsEqualTo("Unavailable");
        await Assert.That(denied.Snapshot.Experiment!.Reason).IsEqualTo("InvalidRelease");
    }

    [Test]
    public async Task CancellationAndUnauthorizedRunNeverConsultSource()
    {
        var source = new Source { Snapshot = Release("v1", "fr") };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var outcome = await command.WithParameters(new("x", "en")).ExecuteAsync(cancelled.Token);
        var unauthorized = await command
            .WithParameters(new("x", "en"))
            .InRun(Guid.NewGuid())
            .ExecuteAsync();
        await Assert
            .That(outcome is Outcome<Result>.Failed { Failure: ExecutionFailure.Cancelled })
            .IsTrue();
        await Assert.That(unauthorized is Outcome<Result>.Failed).IsTrue();
        await Assert.That(source.Reads).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrimaryReturnsWhileShadowUsesIndependentScopeAndCapturedInput(bool failShadow)
    {
        var source = new Source();
        ExperimentRevision.TryCreate("v1", out var revision);
        var primary = new ExperimentVariant(
            "primary",
            1,
            new(new TemplateChange.Inherited(), new TemplateChange.Replaced("primary {Text}")),
            new Dictionary<string, JsonElement>(),
            new(ProfileKey.Declare("primary-route"), "dynamic", "primary-model")
        );
        var shadow = new ExperimentVariant(
            "shadow",
            0,
            new(new TemplateChange.Inherited(), new TemplateChange.Replaced("shadow {Text}")),
            new Dictionary<string, JsonElement>(),
            new(ProfileKey.Declare("shadow-route"), "shadow", "shadow-model")
        );
        var view = ExperimentCatalogView.Freeze(
            "experiment.test",
            1,
            "shape1",
            revision!,
            "trial",
            "primary",
            [primary, shadow]
        );
        source.Snapshot = new(
            revision!,
            new Dictionary<string, ExperimentCatalogView> { ["experiment.test"] = view }
        );
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<ShadowDisposition>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var reported = new TaskCompletionSource<ShadowExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        string? primaryGroup = null;
        var ambient = new AsyncLocal<string?> { Value = "request" };
        string? seen = "unset";
        var shadowClient = new Client
        {
            Entered = started,
            Release = release,
            OnCall = () => seen = ambient.Value,
            Fail = failShadow,
        };
        var primaryClient = new Client();
        var baseline = new Client();
        await using var scheduler = new InMemoryShadowScheduler(2, 1);
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => primaryClient)
                .AddChatClient("shadow", _ => shadowClient)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(sp =>
                    new(
                        new ExperimentResolver(source),
                        RoutePolicy: (_, _) => true,
                        Scheduler: scheduler,
                        SelectShadows: (_, _) => ["shadow"],
                        ObserveShadow: disposition => completed.TrySetResult(disposition),
                        ShadowScopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
                        ShadowPolicy: (_, _) => true,
                        ObserveShadowResult: result => reported.TrySetResult(result)
                    )
                )
        );
        using var provider = services.BuildServiceProvider();
        using (var request = provider.CreateScope())
        {
            var command = new Command(request.ServiceProvider.GetRequiredService<CommandRuntime>());
            var input = new Input("original", "en");
            var response = await command
                .WithParameters(input)
                .ExecuteAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            input = input with { Text = "changed" };
            await Assert.That(response is Outcome<Result>.Success).IsTrue();
            primaryGroup = ((Outcome<Result>.Success)response)
                .Snapshot
                .Experiment!
                .ComparisonGroupId;
        }
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(completed.Task.IsCompleted).IsFalse();
        release.TrySetResult();
        await Assert
            .That(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)))
            .IsEqualTo(failShadow ? ShadowDisposition.Failed : ShadowDisposition.Completed);
        await Assert.That(shadowClient.Calls.Single().Prompt).IsEqualTo("shadow original");
        await Assert.That(seen).IsNull();
        await Assert.That(source.Reads).IsEqualTo(1);
        var notification = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(notification.ComparisonGroupId).IsEqualTo(primaryGroup);
        await Assert.That(notification.VariantId).IsEqualTo("shadow");
        if (failShadow)
        {
            await Assert.That(notification.Output).IsNull();
        }
        else
        {
            await Assert.That(notification.Output is not null).IsTrue();
            var stored = (
                (LedgerResult<OutputArtifact>.Accepted)
                    provider
                        .GetRequiredService<IHumanFeedbackLedger>()
                        .ReadOutput(notification.Output!, new Actor("tenant", "user"))
            ).Value;
            await Assert.That(stored.Evidence!.Role).IsEqualTo(ExperimentRole.Shadow);
            await Assert.That(stored.Evidence.ComparisonGroupId).IsEqualTo(primaryGroup);
            await Assert.That(stored.Value.GetProperty("Value").GetString()).IsEqualTo("ok");
        }
    }

    [Test]
    public async Task InvalidTemplateAndFixedModelMismatchFallBackBeforeTargetSend()
    {
        var source = new Source
        {
            Snapshot = Release("v1", "fr", "unknown {NoSuchField}", "bound"),
        };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(
            source,
            baseline,
            treatment,
            (_, _) => true,
            fixedModel: "bound"
        );
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var invalid = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        source.Snapshot = Release("v2", "fr", model: "different");
        var mismatch = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        await Assert.That(invalid.Snapshot.Experiment!.Reason).IsEqualTo("InvalidRelease");
        await Assert.That(mismatch.Snapshot.Experiment!.Reason).IsEqualTo("InvalidRelease");
        await Assert.That(baseline.Calls.All(x => x.Prompt == "base x en")).IsTrue();
        await Assert.That(treatment.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExplicitProfileAndTemperatureTakePrecedence()
    {
        var source = new Source { Snapshot = Release("v1", "fr") };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var result = (Outcome<Result>.Success)
            await command
                .WithParameters(new("x", "en"))
                .WithProfile("base")
                .WithTemperature(.8f)
                .ExecuteAsync();
        await Assert.That(baseline.Calls.Single().Prompt).IsEqualTo("new x fr");
        await Assert.That(baseline.Calls.Single().Temperature).IsEqualTo(.8f);
        await Assert.That(treatment.Calls.Count).IsEqualTo(0);
        await Assert.That(result.Snapshot.Experiment!.Settings!.Temperature).IsEqualTo(.8f);
    }

    [Test]
    public async Task UnauthorizedOverrideAndStaleCommandRevisionFallBackAsWhole()
    {
        var release = Release("v1", "fr");
        var view = release.Commands["experiment.test"];
        var variant = view.Variants["v"] with
        {
            ParameterValues = new Dictionary<string, JsonElement>
            {
                ["Text"] = J("\"not-authorized\""),
            },
        };
        var badView = view with
        {
            Variants = new Dictionary<string, ExperimentVariant> { ["v"] = variant },
        };
        var source = new Source
        {
            Snapshot = release with
            {
                Commands = new Dictionary<string, ExperimentCatalogView>
                {
                    ["experiment.test"] = badView,
                },
            },
        };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var bad = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        source.Snapshot = release with
        {
            Commands = new Dictionary<string, ExperimentCatalogView>
            {
                ["experiment.test"] = view with { CommandRevision = 2 },
            },
        };
        var stale = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        await Assert.That(bad.Snapshot.Experiment!.Reason).IsEqualTo("InvalidRelease");
        await Assert.That(stale.Snapshot.Experiment!.Reason).IsEqualTo("StaleCommandRevision");
        await Assert.That(baseline.Calls.All(x => x.Prompt == "base x en")).IsTrue();
        await Assert.That(treatment.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SourceFailureRunsConfiguredDefaultAsOneUnit()
    {
        var source = new Source();
        var baseline = new Client();
        var alternate = new Client();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => alternate)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(
                    new ExperimentRuntimeOptions(
                        new ExperimentResolver(source),
                        RoutePolicy: (_, _) => true,
                        ConfigureDefault: initial =>
                            initial with
                            {
                                UserTemplate = "default {Text} {Locale}",
                                ParameterValues = new Dictionary<string, JsonElement>
                                {
                                    ["Locale"] = J("\"it\""),
                                },
                                Route = new(
                                    ProfileKey.Declare("local-default"),
                                    "dynamic",
                                    "default-model",
                                    Defaults: new(Temperature: .6f)
                                ),
                            }
                    )
                )
        );
        using var provider = services.BuildServiceProvider();
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var result = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        await Assert.That(alternate.Calls.Single().Prompt).IsEqualTo("default x it");
        await Assert.That(alternate.Calls.Single().Model).IsEqualTo("default-model");
        await Assert.That(alternate.Calls.Single().Temperature).IsEqualTo(.6f);
        await Assert.That(result.Snapshot.Experiment!.Source).IsEqualTo("default");
        await Assert.That(result.Snapshot.Experiment.Reason).IsEqualTo("Unavailable");
        await Assert
            .That(result.Snapshot.Experiment.Parameters!.Single(p => p.Name == "Locale").Origin)
            .IsEqualTo("default");
        await Assert.That(baseline.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RevisionTwoAcceptsMatchingRelease()
    {
        var source = new Source();
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(source, baseline, treatment, (_, _) => true);
        var command = new CommandV2(provider.GetRequiredService<CommandRuntime>());
        source.Snapshot = Release("v2", "fr", contract: command.ExperimentContract);
        var result = (Outcome<Result>.Success)
            await command.WithParameters(new("x", "en")).ExecuteAsync();
        await Assert.That(treatment.Calls.Single().Prompt).IsEqualTo("new x fr");
        await Assert.That(result.Snapshot.Experiment!.CommandRevision).IsEqualTo(2);
        await Assert.That(baseline.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GeneratedCommandUsesPublishedContractAndDynamicTemplate()
    {
        var source = new Source();
        var baseline = new Client();
        var treatment = new Client { Response = "{\"Total\":12,\"Currency\":\"USD\"}" };
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment)
                .AddProfile("extraction", "base", "base-model")
                .UseDefaultProfile("extraction")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(
                    new ExperimentRuntimeOptions(
                        new ExperimentResolver(source),
                        RoutePolicy: (_, _) => true
                    )
                )
        );
        using var provider = services.BuildServiceProvider();
        var command = new ExtractInvoice(provider.GetRequiredService<CommandRuntime>());
        var contract = command.ExperimentContract!;
        ExperimentRevision.TryCreate("v1", out var revision);
        var variant = new ExperimentVariant(
            "v",
            1,
            new(
                new TemplateChange.Inherited(),
                new TemplateChange.Replaced("generated {Document} {Locale}")
            ),
            new Dictionary<string, JsonElement>(),
            new(ProfileKey.Declare("dynamic"), "dynamic", "new-model")
        );
        var view = ExperimentCatalogView.Freeze(
            contract.CommandId,
            contract.CommandRevision,
            contract.ContractSignature,
            revision!,
            "trial",
            "v",
            [variant]
        );
        source.Snapshot = new(
            revision!,
            new Dictionary<string, ExperimentCatalogView> { [contract.CommandId] = view }
        );
        var result = await command
            .WithParameters(new InvoiceParameters("invoice", "en"))
            .ExecuteAsync();
        await Assert.That(result is Outcome<Invoice>.Success).IsTrue();
        await Assert.That(treatment.Calls.Single().Prompt).Contains("generated invoice en");
    }

    [Test]
    public async Task InterfaceValidatorChecksTheEffectiveExperimentOverride()
    {
        var source = new Source();
        var client = new Client();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => client)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(new ExperimentRuntimeOptions(new ExperimentResolver(source)))
        );
        using var provider = services.BuildServiceProvider();
        var command = new ExperimentValidatedCommand(provider.GetRequiredService<CommandRuntime>());
        var contract = command.ExperimentContract!;
        ExperimentRevision.TryCreate("v1", out var revision);
        var variant = new ExperimentVariant(
            "v",
            1,
            new(
                new TemplateChange.Inherited(),
                new TemplateChange.Replaced("TREATMENT {Document} {Locale}")
            ),
            new Dictionary<string, JsonElement> { ["Locale"] = J("\"fr\"") },
            null
        );
        var view = ExperimentCatalogView.Freeze(
            contract.CommandId,
            contract.CommandRevision,
            contract.ContractSignature,
            revision!,
            "trial",
            "v",
            [variant]
        );
        source.Snapshot = new(
            revision!,
            new Dictionary<string, ExperimentCatalogView> { [contract.CommandId] = view }
        );

        var result = await command
            .WithParameters(new ExperimentValidatedInput("invoice", "en"))
            .ExecuteAsync();

        await Assert.That(result is Outcome<ExperimentValidatedResult>.Success).IsTrue();
        await Assert.That(client.Calls.Single().Prompt).IsEqualTo("CONTROL invoice en");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingInterfaceValidatorDoesNotFallBackOrSend(bool throwsAfterOverride)
    {
        var source = new Source();
        var client = new Client();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => client)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(new ExperimentRuntimeOptions(new ExperimentResolver(source)))
        );
        using var provider = services.BuildServiceProvider();
        var command = new ExperimentValidatedCommand(provider.GetRequiredService<CommandRuntime>());
        var contract = command.ExperimentContract!;
        ExperimentRevision.TryCreate("v1", out var revision);
        var variant = new ExperimentVariant(
            "v",
            1,
            new(new TemplateChange.Inherited(), new TemplateChange.Inherited()),
            new Dictionary<string, JsonElement> { ["Locale"] = J("\"boom\"") },
            null
        );
        var view = ExperimentCatalogView.Freeze(
            contract.CommandId,
            contract.CommandRevision,
            contract.ContractSignature,
            revision!,
            "trial",
            "v",
            [variant]
        );
        source.Snapshot = new(
            revision!,
            new Dictionary<string, ExperimentCatalogView> { [contract.CommandId] = view }
        );

        var result = await command
            .WithParameters(
                new ExperimentValidatedInput("invoice", throwsAfterOverride ? "en" : "boom")
            )
            .ExecuteAsync();

        await Assert
            .That(
                result
                    is Outcome<ExperimentValidatedResult>.Failed
                    {
                        Failure: ExecutionFailure.HookFailure { Phase: "ValidateInput" },
                    }
            )
            .IsTrue();
        await Assert.That(client.Calls.Count).IsEqualTo(0);
        await Assert.That(source.Reads).IsEqualTo(throwsAfterOverride ? 1 : 0);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledResult = await command
            .WithParameters(new ExperimentValidatedInput("invoice", "boom"))
            .ExecuteAsync(cancelled.Token);
        await Assert
            .That(
                cancelledResult
                    is Outcome<ExperimentValidatedResult>.Failed
                    {
                        Failure: ExecutionFailure.Cancelled,
                    }
            )
            .IsTrue();
        await Assert.That(client.Calls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PolicySeesEffectiveTemperaturePerRouteAndFallback(bool explicitTemperature)
    {
        var release = Release("v1", "fr");
        var view = release.Commands["experiment.test"];
        var variant = view.Variants["v"] with
        {
            Route = view.Variants["v"].Route! with { Fallback = ProfileKey.Declare("base") },
        };
        var source = new Source
        {
            Snapshot = release with
            {
                Commands = new Dictionary<string, ExperimentCatalogView>
                {
                    [view.CommandId] = view with
                    {
                        Variants = new Dictionary<string, ExperimentVariant> { ["v"] = variant },
                    },
                },
            },
        };
        var baseline = new Client();
        var treatment = new Client { FailTransient = true };
        var seen = new ConcurrentQueue<(string Provider, float? Temperature)>();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment)
                .AddProfile("base", "base", "base-model", new(Temperature: .8f))
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(
                    new ExperimentRuntimeOptions(
                        new ExperimentResolver(source),
                        RoutePolicy: (_, route) =>
                        {
                            seen.Enqueue((route.Provider, route.Temperature));
                            return true;
                        }
                    )
                )
        );
        using var provider = services.BuildServiceProvider();
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var invocation = command.WithParameters(new("x", "en"));
        if (explicitTemperature)
        {
            invocation = invocation.WithTemperature(.4f);
        }
        var result = await invocation.ExecuteAsync();
        await Assert.That(result is Outcome<Result>.Success).IsTrue();
        await Assert
            .That(seen.First(x => x.Provider == "dynamic").Temperature)
            .IsEqualTo(explicitTemperature ? .4f : .3f);
        await Assert
            .That(seen.First(x => x.Provider == "base").Temperature)
            .IsEqualTo(explicitTemperature ? .4f : .8f);
        await Assert
            .That(treatment.Calls.Single().Temperature)
            .IsEqualTo(explicitTemperature ? .4f : .3f);
        await Assert
            .That(baseline.Calls.Single().Temperature)
            .IsEqualTo(explicitTemperature ? .4f : .8f);
    }

    [Test]
    public async Task PolicyRejectsUnsupportedExplicitTemperatureBeforeAnySend()
    {
        var source = new Source { Snapshot = Release("v1", "fr") };
        var baseline = new Client();
        var treatment = new Client();
        using var provider = Build(
            source,
            baseline,
            treatment,
            (_, route) => route.Temperature is null or 1f
        );
        var command = new Command(provider.GetRequiredService<CommandRuntime>());
        var result = await command
            .WithParameters(new("x", "en"))
            .WithTemperature(.2f)
            .ExecuteAsync();
        await Assert.That(result is Outcome<Result>.Failed).IsTrue();
        await Assert.That(treatment.Calls.Count + baseline.Calls.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationDuringShadowSelectionOrPolicyDoesNotOfferOrSend(
        bool cancelInPolicy
    )
    {
        var release = Release("v1", "fr");
        var view = release.Commands["experiment.test"];
        var shadow = view.Variants["v"] with { VariantId = "shadow" };
        var source = new Source
        {
            Snapshot = release with
            {
                Commands = new Dictionary<string, ExperimentCatalogView>
                {
                    [view.CommandId] = view with
                    {
                        Variants = new Dictionary<string, ExperimentVariant>
                        {
                            ["v"] = view.Variants["v"],
                            ["shadow"] = shadow,
                        },
                    },
                },
            },
        };
        var scheduler = new CountingScheduler();
        var baseline = new Client();
        var treatment = new Client();
        using var cancellation = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddLlmCmd(b =>
            b.AddChatClient("base", _ => baseline)
                .AddChatClient("dynamic", _ => treatment)
                .AddProfile("base", "base", "base-model")
                .UseDefaultProfile("base")
                .UseActor(_ => new Actor("tenant", "user"))
                .UseExperiments(sp => new ExperimentRuntimeOptions(
                    new ExperimentResolver(source),
                    RoutePolicy: (_, _) => true,
                    Scheduler: scheduler,
                    ShadowScopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
                    SelectShadows: (_, _) =>
                    {
                        if (!cancelInPolicy)
                        {
                            cancellation.Cancel();
                        }
                        return ["shadow"];
                    },
                    ShadowPolicy: (_, _) =>
                    {
                        if (cancelInPolicy)
                        {
                            cancellation.Cancel();
                        }
                        return true;
                    }
                ))
        );
        using var provider = services.BuildServiceProvider();
        var result = await new Command(provider.GetRequiredService<CommandRuntime>())
            .WithParameters(new("x", "en"))
            .ExecuteAsync(cancellation.Token);
        await Assert
            .That(result is Outcome<Result>.Failed { Failure: ExecutionFailure.Cancelled })
            .IsTrue();
        await Assert.That(scheduler.Offered).IsEqualTo(0);
        await Assert.That(treatment.Calls.Count + baseline.Calls.Count).IsEqualTo(0);
    }
}
