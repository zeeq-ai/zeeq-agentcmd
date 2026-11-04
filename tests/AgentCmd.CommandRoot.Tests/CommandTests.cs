using CommandRoot;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public class CommandTests
{
    static (
        InMemoryFeedbackLedger Ledger,
        Actor Actor,
        Guid Run,
        FakeClient Client,
        CommandRuntime Runtime,
        ExtractInvoice Command
    ) Setup(
        string response = "{\"Total\":12.5,\"Currency\":\"USD\"}",
        Action<ChatClientBuilder>? configure = null
    )
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("test", "reviewer");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient(response);
        var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile>
            {
                ["extraction"] = new("model-a", .4f),
                ["other"] = new("model-b", .8f),
            },
            ledger,
            () => actor,
            configure
        );
        return (ledger, actor, run, client, runtime, new ExtractInvoice(runtime));
    }

    [Test]
    public async Task Actual_generated_command_executes_and_records_typed_data()
    {
        int middleware = 0;
        var f = Setup(configure: b =>
            b.Use(
                async (m, o, n, c) =>
                {
                    middleware++;
                    return await n.GetResponseAsync(m, o, c);
                },
                null
            )
        );
        using var runtime = f.Runtime;
        Invocation<InvoiceParameters, Invoice> invocation = f
            .Command.WithParameters(new("invoice document"))
            .WithProfile("extraction")
            .Configure(o => o.Temperature = .1f)
            .InRun(f.Run);
        Outcome<Invoice> outcome = await invocation.ExecuteAsync();
        var success = (Outcome<Invoice>.Success)outcome;
        await Assert.That(success.Value.Total).IsEqualTo(12.5m);
        await Assert.That(success.Value.Currency).IsEqualTo("USD");
        await Assert.That(middleware).IsEqualTo(1);
        await Assert
            .That(f.Client.Prompt)
            .IsEqualTo("Extract an invoice from invoice document (en)");
        await Assert.That(f.Client.System).IsEqualTo("You extract invoices. Reply with JSON only.");
        await Assert.That(f.Client.Model).IsEqualTo("model-a");
        await Assert.That(f.Client.Temperature).IsEqualTo(.1f);
        await Assert
            .That(
                System.Text.RegularExpressions.Regex.IsMatch(
                    f.Client.Format!.SchemaName!,
                    "^[A-Za-z0-9_-]{1,64}$"
                )
            )
            .IsTrue();
        await Assert.That(success.Snapshot.PromptId).IsEqualTo("invoice.extract");
        await Assert.That(success.Snapshot.Revision).IsEqualTo(1);
        await Assert.That(success.Snapshot.DefinitionHash.Length).IsEqualTo(64);
        await Assert
            .That(success.Snapshot.RawTemplate)
            .IsEqualTo("Extract an invoice from {Document} ({Locale})");
        await Assert.That(success.Snapshot.Parameters.Count).IsEqualTo(2);
        var doc = success.Snapshot.Parameters.Single(p => p.Name == "Document");
        var locale = success.Snapshot.Parameters.Single(p => p.Name == "Locale");
        await Assert.That(doc.TypeId).IsEqualTo("string");
        await Assert.That(doc.Value.GetString()).IsEqualTo("invoice document");
        await Assert.That(locale.TypeId).IsEqualTo("string?");
        await Assert.That(locale.Value.GetString()).IsEqualTo("en");
        var stored = ((OutputDelivery.Stored)success.Delivery).Output;
        await Assert.That(stored.Reference.RunId).IsEqualTo(f.Run);
        await Assert.That(stored.Reference.ExecutionId).IsEqualTo(success.ExecutionId);
        var review = InvoiceService.Review(
            success,
            f.Ledger,
            f.Actor,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        var grade = (LedgerResult<GradeReceipt>.Accepted)review.Item1;
        var correction = (LedgerResult<CorrectionReceipt>.Accepted)review.Item2;
        await Assert.That(grade.Value.Event.Target).IsEqualTo(stored.Reference);
        await Assert
            .That(correction.Value.Output.Value.GetProperty("Total").GetDecimal())
            .IsEqualTo(42m);
    }

    [Test]
    public async Task Retried_review_reuses_caller_event_ids_and_original_receipts()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var success = (Outcome<Invoice>.Success)
            await f.Command.WithParameters(new("doc")).InRun(f.Run).ExecuteAsync();
        var gradeId = Guid.NewGuid();
        var correctionId = Guid.NewGuid();
        var first = InvoiceService.Review(success, f.Ledger, f.Actor, gradeId, correctionId);
        var retry = InvoiceService.Review(success, f.Ledger, f.Actor, gradeId, correctionId);
        var firstGrade = (LedgerResult<GradeReceipt>.Accepted)first.Item1;
        var retryGrade = (LedgerResult<GradeReceipt>.Accepted)retry.Item1;
        var firstCorrection = (LedgerResult<CorrectionReceipt>.Accepted)first.Item2;
        var retryCorrection = (LedgerResult<CorrectionReceipt>.Accepted)retry.Item2;
        await Assert.That(firstGrade.Replayed).IsFalse();
        await Assert.That(firstCorrection.Replayed).IsFalse();
        await Assert.That(retryGrade.Replayed).IsTrue();
        await Assert.That(retryCorrection.Replayed).IsTrue();
        await Assert.That(retryGrade.Value.Event.EventId).IsEqualTo(gradeId);
        await Assert.That(retryCorrection.Value.Event.EventId).IsEqualTo(correctionId);
        await Assert
            .That(retryCorrection.Value.Output.Reference)
            .IsEqualTo(firstCorrection.Value.Output.Reference);
        await Assert.That(retryGrade.Value.Event.Target).IsEqualTo(firstGrade.Value.Event.Target);
    }

    [Test]
    public async Task Sample_service_runs_the_actual_generated_chain()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var service = CommandComposition.Activate(runtime);
        var success = (Outcome<Invoice>.Success)await service.ExtractAsync("sample", f.Run);
        await Assert.That(success.Value.Currency).IsEqualTo("USD");
        await Assert.That(success.Snapshot.Temperature).IsEqualTo(.1f);
        await Assert
            .That(success.Snapshot.FormattedPrompt)
            .IsEqualTo("Extract an invoice from sample (en)");
    }

    [Test]
    public async Task Profile_resolution_and_explicit_patch_are_order_independent_and_frozen()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var root = f.Command.WithParameters(new("doc"));
        int calls = 0;
        ChatSettingsPatch? captured = null;
        var first = root.Configure(o =>
            {
                calls++;
                captured = o;
                o.Temperature = .1f;
            })
            .WithProfile("other")
            .InRun(f.Run);
        var second = root.WithProfile("other").Configure(o => o.Temperature = .1f).InRun(f.Run);
        captured!.Temperature = .9f;
        var a = (Outcome<Invoice>.Success)await first.ExecuteAsync();
        var firstOptions = f.Client.LastOptions;
        var b = (Outcome<Invoice>.Success)await second.ExecuteAsync();
        var secondOptions = f.Client.LastOptions;
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(a.Snapshot.Temperature).IsEqualTo(.1f);
        await Assert.That(b.Snapshot.Temperature).IsEqualTo(.1f);
        await Assert.That(a.Snapshot.Model).IsEqualTo("model-b");
        await Assert.That(b.Snapshot.Profile).IsEqualTo("other");
        await Assert.That(ReferenceEquals(firstOptions, secondOptions)).IsFalse();
        var baseResult = (Outcome<Invoice>.Success)await root.InRun(f.Run).ExecuteAsync();
        await Assert.That(baseResult.Snapshot.Temperature).IsEqualTo(.4f);
        await Assert.That(baseResult.Snapshot.Model).IsEqualTo("model-a");
    }

    [Test]
    public async Task Nullable_parameter_and_default_are_projected()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var success = (Outcome<Invoice>.Success)
            await f.Command.WithParameters(new("doc", null)).InRun(f.Run).ExecuteAsync();
        var locale = success.Snapshot.Parameters.Single(x => x.Name == "Locale");
        await Assert.That(locale.TypeId).IsEqualTo("string?");
        await Assert.That(locale.Value.ValueKind).IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert
            .That(success.Snapshot.FormattedPrompt)
            .IsEqualTo("Extract an invoice from doc ()");
    }

    static Task<Outcome<Invoice>> CallAtKnownLine(ExtractInvoice command, Guid run)
    {
#line 500 "contract-callsite.cs"
        return command.WithParameters(new("caller")).InRun(run).ExecuteAsync();
#line default
    }

    [Test]
    public async Task Caller_info_is_user_terminal_call()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var s = (Outcome<Invoice>.Success)await CallAtKnownLine(f.Command, f.Run);
        await Assert.That(Path.GetFileName(s.Caller.File)).IsEqualTo("contract-callsite.cs");
        await Assert.That(s.Caller.Line).IsEqualTo(500);
        await Assert.That(s.Caller.Member).IsEqualTo("CallAtKnownLine");
    }

    [Test]
    public async Task Invalid_input_and_unknown_profile_never_call_provider()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var a = await f.Command.WithParameters(new(" ")).InRun(f.Run).ExecuteAsync();
        var b = await f
            .Command.WithParameters(new("doc"))
            .WithProfile("missing")
            .InRun(f.Run)
            .ExecuteAsync();
        await Assert
            .That(a is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidInput })
            .IsTrue();
        await Assert
            .That(b is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidInput })
            .IsTrue();
        await Assert.That(f.Client.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("null")]
    [Arguments("{\"Total\":-5,\"Currency\":\"USD\"}")]
    [Arguments("{\"Total\":10}")]
    [Arguments("{\"Currency\":\"USD\"}")]
    public async Task Invalid_output_is_not_success(string response)
    {
        var f = Setup(response);
        using var runtime = f.Runtime;
        var result = await f.Command.WithParameters(new("doc")).InRun(f.Run).ExecuteAsync();
        await Assert
            .That(result is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidOutput })
            .IsTrue();
    }

    [Test]
    public async Task Failure_and_cancelled_outcomes_include_execution_id()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var cmd = f.Command.WithParameters(new("doc")).InRun(f.Run);
        f.Client.Mode = "fail";
        var failure = (ExecutionFailure.ProviderFailure)
            ((Outcome<Invoice>.Failed)await cmd.ExecuteAsync()).Failure;
        await Assert.That(failure.ExecutionId).IsNotEqualTo(Guid.Empty);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        f.Client.Mode = "cancel";
        var cancelled = (ExecutionFailure.Cancelled)
            ((Outcome<Invoice>.Failed)await cmd.ExecuteAsync(cts.Token)).Failure;
        await Assert.That(cancelled.ExecutionId).IsNotEqualTo(Guid.Empty);
        var invalid = (ExecutionFailure.InvalidInput)
            (
                (Outcome<Invoice>.Failed)
                    await f.Command.WithParameters(new(" ")).InRun(f.Run).ExecuteAsync()
            ).Failure;
        await Assert.That(invalid.ExecutionId).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task Json_context_names_ignore_and_nullable_are_honored()
    {
        var f = Setup("{\"total_due\":null,\"note\":\"ok\",\"hidden\":\"wire-only\"}");
        using var runtime = f.Runtime;
        var result = (Outcome<MappedInvoice>.Success)
            await new MappedExtract(runtime)
                .WithParameters(new("named", null, "secret"))
                .InRun(f.Run)
                .ExecuteAsync();
        await Assert.That(result.Snapshot.Parameters.Count).IsEqualTo(1);
        await Assert.That(result.Snapshot.Parameters[0].Name).IsEqualTo("Document");
        await Assert.That(result.Snapshot.Parameters[0].Value.GetString()).IsEqualTo("named");
        await Assert
            .That(result.Snapshot.Parameters.Any(p => p.Name == "Secret" || p.Name == "Locale"))
            .IsFalse();
        await Assert.That(result.Value.Total).IsNull();
        await Assert.That(result.Value.Note).IsEqualTo("ok");
        var schema = f.Client.Format!.Schema!.Value;
        var properties = schema.GetProperty("properties");
        await Assert.That(properties.TryGetProperty("total_due", out _)).IsTrue();
        await Assert.That(properties.TryGetProperty("note", out _)).IsTrue();
        await Assert.That(properties.TryGetProperty("hidden", out _)).IsFalse();
        await Assert
            .That(properties.GetProperty("total_due").GetRawText().Contains("null"))
            .IsTrue();
        var stored = ((OutputDelivery.Stored)result.Delivery).Output;
        await Assert
            .That(stored.Value.GetProperty("total_due").ValueKind)
            .IsEqualTo(System.Text.Json.JsonValueKind.Null);
        await Assert.That(stored.Value.GetProperty("hidden").GetString()).IsEqualTo("wire-only");
        await Assert.That(result.Value.Hidden).IsNull();
    }

    [Test]
    public async Task Omitted_run_is_created_and_can_be_reviewed()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var result = (Outcome<Invoice>.Success)
            await f.Command.WithParameters(new("doc")).ExecuteAsync();
        await Assert.That(result.RunId).IsNotEqualTo(Guid.Empty);
        var output = ((OutputDelivery.Stored)result.Delivery).Output;
        await Assert.That(output.Reference.RunId).IsEqualTo(result.RunId);
        await Assert
            .That(
                f.Ledger.ReadOutput(output.Reference, f.Actor)
                    is LedgerResult<OutputArtifact>.Accepted
            )
            .IsTrue();
    }

    [Test]
    [Arguments("ValidateInput")]
    [Arguments("Render")]
    [Arguments("Project")]
    [Arguments("ValidateResult")]
    [Arguments("Schema")]
    public async Task Hook_or_schema_exception_is_converted(string phase)
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var result = await new TrustedProbe(runtime, phase)
            .WithParameters(new("doc"))
            .InRun(f.Run)
            .ExecuteAsync();
        if (phase == "Schema")
        {
            var r = (ExecutionFailure.ConfigurationFailure)
                ((Outcome<Invoice>.Failed)result).Failure;
            await Assert.That(r.ExecutionId).IsNotEqualTo(Guid.Empty);
            await Assert.That(r.Snapshot!.Profile).IsEqualTo("extraction");
        }
        else
        {
            var r = (ExecutionFailure.HookFailure)((Outcome<Invoice>.Failed)result).Failure;
            await Assert.That(r.Phase).IsEqualTo(phase);
            await Assert.That(r.ExecutionId).IsNotEqualTo(Guid.Empty);
            if (phase != "ValidateInput")
            {
                await Assert.That(r.Snapshot!.Profile).IsEqualTo("extraction");
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Ledger_failure_preserves_success_and_typed_value(bool throws)
    {
        var ledger = new FailingLedger(throws);
        var actor = new Actor("t", "s");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient("{\"Total\":12.5,\"Currency\":\"USD\"}");
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { { "extraction", new("m") } },
            ledger,
            () => actor
        );
        var success = (Outcome<Invoice>.Success)
            await new ExtractInvoice(runtime).WithParameters(new("doc")).InRun(run).ExecuteAsync();
        await Assert.That(success.Value.Total).IsEqualTo(12.5m);
        await Assert.That(success.ExecutionId).IsNotEqualTo(Guid.Empty);
        if (throws)
        {
            await Assert.That(success.Delivery is OutputDelivery.Failed).IsTrue();
        }
        else
        {
            await Assert
                .That(((OutputDelivery.Rejected)success.Delivery).Code)
                .IsEqualTo("not_found");
        }
    }

    [Test]
    public async Task Runtime_freezes_actor_per_concurrent_execution()
    {
        var ledger = new InMemoryFeedbackLedger();
        var one = new Actor("a", "one");
        var two = new Actor("b", "two");
        var run1 = ledger.CreateRun(one);
        var run2 = ledger.CreateRun(two);
        var current = new AsyncLocal<Actor?>();
        int calls = 0;
        using var runtime = CommandRuntime.Create(
            () => new FakeClient("{\"Total\":2,\"Currency\":\"USD\"}"),
            new Dictionary<string, Profile> { { "extraction", new("m") } },
            ledger,
            () =>
            {
                Interlocked.Increment(ref calls);
                return current.Value!;
            }
        );
        var cmd = new ExtractInvoice(runtime);
        async Task<Outcome<Invoice>.Success> Execute(Actor actor, Guid run)
        {
            current.Value = actor;
            await Task.Yield();
            return (Outcome<Invoice>.Success)
                await cmd.WithParameters(new("doc")).InRun(run).ExecuteAsync();
        }
        var results = await Task.WhenAll(
            Task.Run(() => Execute(one, run1)),
            Task.Run(() => Execute(two, run2))
        );
        var r1 = ((OutputDelivery.Stored)results[0].Delivery).Output.Reference;
        var r2 = ((OutputDelivery.Stored)results[1].Delivery).Output.Reference;
        await Assert.That(calls).IsEqualTo(2);
        await Assert
            .That(ledger.ReadOutput(r1, one) is LedgerResult<OutputArtifact>.Accepted)
            .IsTrue();
        await Assert
            .That(ledger.ReadOutput(r2, two) is LedgerResult<OutputArtifact>.Accepted)
            .IsTrue();
        await Assert
            .That(ledger.ReadOutput(r1, two) is LedgerResult<OutputArtifact>.Rejected)
            .IsTrue();
        await Assert
            .That(ledger.ReadOutput(r2, one) is LedgerResult<OutputArtifact>.Rejected)
            .IsTrue();
    }

    [Test]
    public async Task Sink_is_notified_once_for_success_and_failure_and_cannot_replace_outcome()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("t", "s");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient("{\"Total\":2,\"Currency\":\"USD\"}");
        var notices = new List<ExecutionOutcomeRecord>();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { { "extraction", new("m") } },
            ledger,
            () => actor,
            sink: r =>
            {
                notices.Add(r);
                throw new Exception("sink failed");
            }
        );
        var cmd = new ExtractInvoice(runtime).WithParameters(new("doc")).InRun(run);
        var good = (Outcome<Invoice>.Success)await cmd.ExecuteAsync();
        client.Mode = "fail";
        var bad = (ExecutionFailure.ProviderFailure)
            ((Outcome<Invoice>.Failed)await cmd.ExecuteAsync()).Failure;
        await Assert.That(notices.Count).IsEqualTo(2);
        await Assert.That(notices[0].ExecutionId).IsEqualTo(good.ExecutionId);
        await Assert.That(notices[0].Outcome).IsEqualTo("success");
        await Assert.That(notices[1].ExecutionId).IsEqualTo(bad.ExecutionId);
        await Assert.That(notices[1].Outcome).IsEqualTo("provider_failure");
        await Assert
            .That(bad.Snapshot!.FormattedPrompt)
            .IsEqualTo("Extract an invoice from doc (en)");
    }

    [Test]
    public async Task Every_outcome_notifies_once_with_available_snapshot()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("t", "s");
        var run = ledger.CreateRun(actor);
        var client = new FakeClient("{\"Total\":2,\"Currency\":\"USD\"}");
        var notices = new List<ExecutionOutcomeRecord>();
        using var runtime = CommandRuntime.Create(
            () => client,
            new Dictionary<string, Profile> { { "extraction", new("m") } },
            ledger,
            () => actor,
            sink: r => notices.Add(r)
        );
        var command = new ExtractInvoice(runtime);
        var cmd = command.WithParameters(new("doc")).InRun(run);
        await cmd.ExecuteAsync();
        await command.WithParameters(new(" ")).InRun(run).ExecuteAsync();
        client.Mode = "fail";
        await cmd.ExecuteAsync();
        client.Mode = null;
        await new TrustedProbe(runtime, "Project")
            .WithParameters(new("doc"))
            .InRun(run)
            .ExecuteAsync();
        await new TrustedProbe(runtime, "Schema")
            .WithParameters(new("doc"))
            .InRun(run)
            .ExecuteAsync();
        client.Mode = "invalid";
        await cmd.ExecuteAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        client.Mode = "cancel";
        await cmd.ExecuteAsync(cts.Token);
        await Assert
            .That(string.Join(",", notices.Select(n => n.Outcome)))
            .IsEqualTo(
                "success,invalid_input,provider_failure,hook_failure,configuration_failure,invalid_output,cancelled"
            );
        await Assert.That(notices.Count).IsEqualTo(7);
        await Assert.That(notices.Select(n => n.ExecutionId).Distinct().Count()).IsEqualTo(7);
        await Assert.That(notices[1].Snapshot).IsNull();
        await Assert
            .That(
                notices
                    .Skip(2)
                    .All(n => n.Snapshot is not null && n.Snapshot.Profile == "extraction")
            )
            .IsTrue();
    }

    [Test]
    public async Task Invalid_settings_after_profile_resolution_keep_snapshot()
    {
        var f = Setup();
        using var runtime = f.Runtime;
        var result = (ExecutionFailure.InvalidInput)
            (
                (Outcome<Invoice>.Failed)
                    await f
                        .Command.WithParameters(new("doc"))
                        .WithTemperature(float.NaN)
                        .InRun(f.Run)
                        .ExecuteAsync()
            ).Failure;
        await Assert.That(result.Snapshot!.Profile).IsEqualTo("extraction");
        await Assert.That(result.Snapshot.Model).IsEqualTo("model-a");
        await Assert.That(f.Client.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Owned_runtime_disposes_client_once()
    {
        var f = Setup();
        f.Runtime.Dispose();
        f.Runtime.Dispose();
        await Assert.That(f.Client.Disposals).IsEqualTo(1);
        var result = await f.Command.WithParameters(new("doc")).InRun(f.Run).ExecuteAsync();
        await Assert
            .That(result is Outcome<Invoice>.Failed { Failure: ExecutionFailure.InvalidInput })
            .IsTrue();
    }
}

sealed class FakeClient(string response) : IChatClient
{
    public string Response = response;
    public string? Mode;
    public string? Prompt,
        System,
        Model;
    public float? Temperature;
    public ChatResponseFormatJson? Format;
    public int Calls,
        Disposals;
    public ChatOptions? LastOptions;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        Calls++;
        if (Mode == "fail")
        {
            throw new InvalidOperationException();
        }

        if (Mode == "cancel")
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var items = messages.ToArray();
        System = items.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
        Prompt = items.Last(m => m.Role == ChatRole.User).Text;
        Model = options?.ModelId;
        Temperature = options?.Temperature;
        Format = options?.ResponseFormat as ChatResponseFormatJson;
        LastOptions = options;
        return Task.FromResult(
            new ChatResponse(
                new ChatMessage(ChatRole.Assistant, Mode == "invalid" ? "not json" : Response)
            )
        );
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException();

    public object? GetService(Type type, object? key = null) => null;

    public void Dispose() => Disposals++;
}

sealed class TrustedProbe : PromptCommand<InvoiceParameters, Invoice>
{
    public TrustedProbe(CommandRuntime runtime, string phase)
        : base(runtime, Make(phase)) { }

    static PromptDescriptor<InvoiceParameters, Invoice> Make(string phase) =>
        new(
            "probe.test",
            1,
            "extraction",
            "system",
            "{Document}",
            "hash",
            "probe_test",
            new Lazy<System.Text.Json.JsonElement>(() =>
                phase == "Schema"
                    ? throw new Exception("secret")
                    : System.Text.Json.JsonDocument.Parse("{} ").RootElement.Clone()
            ),
            TrustedProbeJson.Default.Invoice,
            p => phase == "Render" ? throw new Exception("secret") : p.Document,
            p => "system",
            p =>
                phase == "Project" ? throw new Exception("secret") : Array.Empty<ParameterRecord>(),
            p => phase == "ValidateInput" ? throw new Exception("secret") : null,
            r => phase == "ValidateResult" ? throw new Exception("secret") : null
        );
}

sealed class FailingLedger(bool throws) : IHumanFeedbackLedger
{
    readonly InMemoryFeedbackLedger inner = new();

    public Guid CreateRun(Actor actor) => inner.CreateRun(actor);

    public LedgerResult<OutputArtifact> RecordOutput(
        Guid runId,
        Guid executionId,
        System.Text.Json.JsonElement value,
        string schemaVersion,
        System.Diagnostics.ActivityContext origin,
        Actor actor
    ) =>
        throws
            ? throw new Exception("failure")
            : new LedgerResult<OutputArtifact>.Rejected("not_found", "missing");

    public LedgerResult<OutputArtifact> RecordProducedOutput(
        Guid runId,
        ProducerRef producer,
        System.Text.Json.JsonElement value,
        string schemaVersion,
        System.Diagnostics.ActivityContext origin,
        Actor actor
    ) =>
        throws
            ? throw new Exception("failure")
            : new LedgerResult<OutputArtifact>.Rejected("not_found", "missing");

    public LedgerResult<OutputArtifact> ReadOutput(OutputRef target, Actor actor) =>
        inner.ReadOutput(target, actor);

    public LedgerResult<GradeReceipt> RecordGrade(GradeRequest request, Actor actor) =>
        inner.RecordGrade(request, actor);

    public LedgerResult<CorrectionReceipt> SubmitCorrection(
        CorrectionRequest request,
        Actor actor
    ) => inner.SubmitCorrection(request, actor);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Invoice))]
internal partial class TrustedProbeJson : System.Text.Json.Serialization.JsonSerializerContext;
