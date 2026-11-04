using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;
using Zeeq.AgentCmd.Generated;

namespace AgentCmd.LiveProviders.Tests;

public sealed record MultiHeader(string Currency);

public sealed record MultiLines(int Count);

public sealed record MultiTotal(int Count, string Currency);

public sealed record MultiInput(string Text);

public sealed record MultiTotalInput(string Text, int Count);

[JsonSerializable(typeof(MultiWhole))]
public partial class MultiWholeJson : JsonSerializerContext;

[CompositeOutput("live.multi.whole", Revision = 1, JsonContext = typeof(MultiWholeJson))]
public sealed partial record MultiWhole(MultiHeader Header, MultiLines Lines, MultiTotal Total);

public sealed partial class MultiHeaderCommand : PromptCommand<MultiInput, MultiHeader>
{
    public const string Id = "live.multi.header";
    public const string UserTemplate = "For {Text}, set Currency to USD.";
}

public sealed partial class MultiLinesCommand : PromptCommand<MultiInput, MultiLines>
{
    public const string Id = "live.multi.lines";
    public const string UserTemplate = "For {Text}, set Count to 2.";
}

public sealed partial class MultiTotalCommand : PromptCommand<MultiTotalInput, MultiTotal>
{
    public const string Id = "live.multi.total";
    public const string UserTemplate = "For {Text}, set Currency to USD and Count to {Count}.";
}

public class MultiPromptEvaluationTests
{
    [Test]
    public async Task Sdk_composition_is_read_back_and_only_the_whole_is_graded()
    {
        using var transport = new CompositionTransport();
        var settings = new LiveSettings(
            "azureopenai",
            "offline-placeholder",
            "synthetic-model",
            new Uri("https://synthetic.example/openai/v1/"),
            modelId: "gpt-6-luna"
        );
        var result = await MultiPromptProof.RunAsync(settings, transport);
        await Assert.That(result.Requests).IsEqualTo(3);
        await Assert.That(transport.SchemaChecks).IsEqualTo(3);
        await Assert.That(transport.DependencySeen).IsTrue();
        await Assert.That(result.GradeCount).IsEqualTo(1);
        await Assert.That(result.Members).IsEqualTo(3);
        await Assert.That(result.DependencyCallbacks).IsEqualTo(1);
    }

    private sealed class CompositionTransport : HttpMessageHandler
    {
        internal int Calls,
            SchemaChecks;
        internal bool DependencySeen;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var schema = body["response_format"]?["json_schema"]?["schema"]?["properties"];
            Calls++;
            var expected = Calls switch
            {
                1 => "Currency",
                2 => "Count",
                _ => "Count",
            };
            if (
                schema?[expected] is not null
                && body["max_completion_tokens"]?.GetValue<int>() is > 0 and <= 64
            )
            {
                SchemaChecks++;
            }

            if (Calls == 3)
            {
                DependencySeen = body["messages"]!
                    .ToJsonString()
                    .Contains("Count to 2", StringComparison.Ordinal);
            }

            var text = Calls switch
            {
                1 => "{\"Currency\":\"USD\"}",
                2 => "{\"Count\":2}",
                _ => "{\"Count\":2,\"Currency\":\"USD\"}",
            };
            var response = JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-synthetic",
                    @object = "chat.completion",
                    created = 1700000000,
                    model = "synthetic-model",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new { role = "assistant", content = text },
                            finish_reason = "stop",
                        },
                    },
                    usage = new
                    {
                        prompt_tokens = 10,
                        completion_tokens = 4,
                        total_tokens = 14,
                    },
                }
            );
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}

[Explicit, Category("Live")]
public class AzureMultiPromptEvaluationTests
{
    [Test]
    public async Task Composes_and_grades_only_the_read_back_aggregate()
    {
        var settings = LiveSettings.Load("azureopenai");
        var destination = EvidencePaths.BeginRun("azureopenai.composition.evaluation.json");
        using var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        try
        {
            var result = await MultiPromptProof.RunAsync(settings, transport);
            Directory.CreateDirectory(EvidencePaths.Directory);
            await File.WriteAllTextAsync(
                destination,
                JsonSerializer.Serialize(
                    new
                    {
                        VerifiedAt = DateTimeOffset.UtcNow,
                        EvaluationTarget = "exact stored aggregate",
                        result.Requests,
                        result.GradeCount,
                        result.Members,
                        result.DependencyCallbacks,
                    }
                )
            );
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Azure composition proof failed ({error.GetType().Name})."
            );
        }
    }
}

internal static class MultiPromptProof
{
    internal sealed record Result(
        int Requests,
        int GradeCount,
        int Members,
        int DependencyCallbacks
    );

    internal static async Task<Result> RunAsync(LiveSettings settings, HttpMessageHandler transport)
    {
        using var counter = new CountingHandler(transport);
        using var http = new HttpClient(counter) { Timeout = TimeSpan.FromSeconds(45) };
        var actor = new Actor("composition-proof", "reviewer");
        var ledger = new GradeAuditLedger();
        var services = new ServiceCollection()
            .AddSingleton<IHumanFeedbackLedger>(ledger)
            .AddAgentCmd(o =>
                o.UseChatClient(_ => LiveProviderFactory.Create(settings, http))
                    .AddProfile("proof", settings.Model)
                    .UseActor(_ => actor)
            )
            .AddGeneratedCommands();
        using var serviceProvider = services.BuildServiceProvider();
        var roots = serviceProvider.GetRequiredService<IRootCommands>();
        var run = ledger.CreateRun(actor);
        var root = await roots.BeginAsync<MultiWhole>(new CompositionRequest(run, "v1", "proof"));
        var input = new MultiInput("the sample");
        // Register every slot and its dependency before execution, so preflight
        // can reject an invalid plan before any paid request is made.
        root.WithHeaderFrom(
                serviceProvider.GetRequiredService<MultiHeaderCommand>().WithParameters(input)
            )
            .WithLinesFrom(
                serviceProvider.GetRequiredService<MultiLinesCommand>().WithParameters(input)
            );
        var callbacks = 0;
        // The dependent pass receives the pinned, stored Lines output exactly once.
        root.WithTotalFrom(
            root.Steps.Lines.Then(pinned =>
            {
                callbacks++;
                return serviceProvider
                    .GetRequiredService<MultiTotalCommand>()
                    .WithParameters(new(input.Text, pinned.Value.Count));
            })
        );
        if (counter.Calls != 0)
        {
            throw new InvalidOperationException("Deferred composition sent a request early.");
        }

        var composition = await root.ExecuteAsync();
        if (composition is not CompositeOutcome<MultiWhole>.Published published)
        {
            // Preserve bounded preflight and step codes so an offline failure
            // identifies its contract boundary without logging prompt content.
            var reason = composition switch
            {
                CompositeOutcome<MultiWhole>.ConfigurationFailure failure => string.Join(
                    ", ",
                    failure.Problems
                ),
                CompositeOutcome<MultiWhole>.Incomplete incomplete => string.Join(
                    ", ",
                    incomplete.Steps.Select(step =>
                        step.SlotId + ":" + step.Status + ":" + step.Code
                    )
                ),
                CompositeOutcome<MultiWhole>.Conflict conflict => conflict.Code,
                CompositeOutcome<MultiWhole>.Unavailable unavailable => unavailable.Reason,
                CompositeOutcome<MultiWhole>.Cancelled => "cancelled",
                CompositeOutcome<MultiWhole>.Published => "published",
            };
            throw new InvalidOperationException("Composition failed: " + reason);
        }

        var reference = published.Aggregate.Output;
        // A composition has its own producer; do not invent a command execution ID.
        if (
            reference.Producer is not ProducerRef.Composition producer
            || producer.Id != published.DraftId
            || reference.ExecutionId is not null
            || published.Members.Count != 3
            || published.Members.Select(m => m.SlotId).Distinct().Count() != 3
            || published.Members.Any(m =>
                m.Output.Producer is not ProducerRef.CommandExecution || m.Output.RunId != run
            )
        )
        {
            throw new InvalidOperationException("Aggregate lineage invalid.");
        }

        var requestsAfterComposition = counter.Calls;
        // The evaluator consumes the exact ledger version, never Published.Value or the parts.
        var read = ReadAndEvaluate(ledger, actor, reference);
        // One root-pointer grade targets the entire exact output version, never a leaf.
        var grade = new GradeRequest(
            Guid.NewGuid(),
            reference,
            "",
            read.Value,
            1,
            "Cross-part invariants passed"
        );
        if (
            ledger.RecordGrade(grade, actor)
                is not LedgerResult<GradeReceipt>.Accepted { Replayed: false } accepted
            || accepted.Value.Event.Target != reference
            || ledger.RecordGrade(grade, actor)
                is not LedgerResult<GradeReceipt>.Accepted { Replayed: true }
            || ledger.Grades.Count != 2
            || ledger.Grades.Any(g => g.Target != reference || g.Pointer != "")
            || counter.Calls != requestsAfterComposition
            || requestsAfterComposition != 3
            || callbacks != 1
        )
        {
            throw new InvalidOperationException("Whole grading or replay failed.");
        }

        return new(
            counter.Calls,
            ledger.Grades.Select(g => g.EventId).Distinct().Count(),
            published.Members.Count,
            callbacks
        );
    }

    // The reader depends only on the ledger and immutable aggregate reference.
    private static OutputArtifact ReadAndEvaluate(
        IHumanFeedbackLedger ledger,
        Actor actor,
        OutputRef reference
    )
    {
        if (
            ledger.ReadOutput(reference, actor) is not LedgerResult<OutputArtifact>.Accepted read
            || read.Value.Reference != reference
        )
        {
            throw new InvalidOperationException("Aggregate read failed.");
        }

        var whole =
            read.Value.Value.Deserialize(MultiWholeJson.Default.MultiWhole)
            ?? throw new InvalidOperationException("Aggregate decode failed.");
        if (
            whole.Header.Currency != "USD"
            || whole.Lines.Count != 2
            || whole.Total.Count != whole.Lines.Count
            || whole.Total.Currency != whole.Header.Currency
        )
        {
            throw new InvalidOperationException("Whole invariant failed.");
        }

        return read.Value;
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            // Enforce the budget before sending; a failed assertion afterward
            // would be too late to prevent an extra paid request.
            if (Calls >= 3)
            {
                throw new InvalidOperationException("Composition request budget exhausted.");
            }

            Calls++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class GradeAuditLedger : IHumanFeedbackLedger
    {
        readonly InMemoryFeedbackLedger inner = new();
        internal List<GradeRequest> Grades { get; } = [];

        public Guid CreateRun(Actor actor) => inner.CreateRun(actor);

        public bool CanAccessRun(Guid runId, Actor actor) => inner.CanAccessRun(runId, actor);

        public LedgerResult<OutputArtifact> RecordOutput(
            Guid runId,
            Guid executionId,
            JsonElement value,
            string schemaVersion,
            ActivityContext origin,
            Actor actor
        ) => inner.RecordOutput(runId, executionId, value, schemaVersion, origin, actor);

        public LedgerResult<OutputArtifact> RecordProducedOutput(
            Guid runId,
            ProducerRef producer,
            JsonElement value,
            string schemaVersion,
            ActivityContext origin,
            Actor actor
        ) => inner.RecordProducedOutput(runId, producer, value, schemaVersion, origin, actor);

        public LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor) =>
            inner.ReadOutput(target, actor);

        public LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor)
        {
            Grades.Add(request);
            return inner.RecordGrade(request, actor);
        }

        public LedgerResult<CorrectionReceipt> SubmitCorrection(
            CorrectionRequest request,
            Actor actor
        ) => inner.SubmitCorrection(request, actor);
    }
}
