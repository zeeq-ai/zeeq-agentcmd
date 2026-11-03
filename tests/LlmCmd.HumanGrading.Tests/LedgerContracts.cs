using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.HumanGrading.Tests;

public class LedgerContracts
{
    static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static (InMemoryFeedbackLedger Ledger, Actor Actor, OutputArtifact Output) Create(
        string json = "{\"items\":[{\"quantity\":2}],\"a/b\":{\"~name\":4},\"\":5,\"nil\":null}"
    )
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant-a", "person-a");
        var run = ledger.CreateRun(actor);
        var result = (LedgerResult<OutputArtifact>.Accepted)
            ledger.RecordOutput(run, Guid.NewGuid(), J(json), "extract.v1", default, actor);
        return (ledger, actor, result.Value);
    }

    static string Code<T>(LedgerResult<T> result) => ((LedgerResult<T>.Rejected)result).Code;

    static GradeRequest Grade(
        OutputRef target,
        string pointer,
        string expected,
        double score = 0.5,
        Guid? id = null,
        Guid? supersedes = null
    ) =>
        new(
            id ?? Guid.NewGuid(),
            target,
            pointer,
            J(expected),
            score,
            SupersedesEventId: supersedes
        );

    static CorrectionRequest Correct(
        OutputRef target,
        string pointer,
        string expected,
        string replacement,
        Guid? id = null
    ) => new(id ?? Guid.NewGuid(), target, pointer, J(expected), J(replacement));

    [Test]
    public async Task Nested_escaped_empty_key_and_root_pointers_resolve()
    {
        var (l, a, o) = Create();
        foreach (
            var (pointer, expected) in new[]
            {
                ("/items/0/quantity", "2"),
                ("/a~1b/~0name", "4"),
                ("/", "5"),
                ("", o.Value.GetRawText()),
            }
        )
        {
            var r = (LedgerResult<GradeReceipt>.Accepted)
                l.RecordGrade(Grade(o.Reference, pointer, expected), a);
            await Assert.That(r.Value.Event.Pointer).IsEqualTo(pointer);
            await Assert
                .That(JsonElement.DeepEquals(r.Value.Event.ObservedValue, J(expected)))
                .IsTrue();
        }
    }

    [Test]
    public async Task Invalid_pointer_syntax_and_array_indices_reject()
    {
        var (l, a, o) = Create();
        foreach (
            string pointer in new[]
            {
                "items/0",
                "/a~2b",
                "/a~",
                "/items/00/quantity",
                "/items/01/quantity",
                "/items/-1/quantity",
                "/items/1/quantity",
                "/items/-",
                "/items/999999999999999999999",
            }
        )
        {
            var r = l.RecordGrade(Grade(o.Reference, pointer, "2"), a);
            await Assert.That(Code(r)).IsEqualTo("value_conflict");
        }
    }

    [Test]
    public async Task Correction_retains_original_and_grade_can_target_original_version()
    {
        var (l, a, o) = Create();
        var result = (LedgerResult<CorrectionReceipt>.Accepted)
            l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "2", "5"), a);
        var edited = result.Value.Output;
        var original = ((LedgerResult<OutputArtifact>.Accepted)l.ReadOutput(o.Reference, a)).Value;
        await Assert
            .That(original.Value.GetProperty("items")[0].GetProperty("quantity").GetInt32())
            .IsEqualTo(2);
        await Assert
            .That(edited.Value.GetProperty("items")[0].GetProperty("quantity").GetInt32())
            .IsEqualTo(5);
        await Assert.That(edited.ParentVersionId).IsEqualTo(o.Reference.VersionId);
        await Assert.That(edited.Reference.Version).IsEqualTo(2);
        await Assert.That(edited.Reference.ArtifactId).IsEqualTo(o.Reference.ArtifactId);
        await Assert.That(edited.Reference.ExecutionId).IsEqualTo(o.Reference.ExecutionId);
        var grade = (LedgerResult<GradeReceipt>.Accepted)
            l.RecordGrade(Grade(o.Reference, "/items/0/quantity", "2", .8), a);
        await Assert.That(grade.Value.Event.Target).IsEqualTo(o.Reference);
        await Assert.That(grade.Value.Event.ObservedValue.GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Correction_supports_root_and_escaped_property_replacement()
    {
        var (l, a, o) = Create();
        var escaped = (LedgerResult<CorrectionReceipt>.Accepted)
            l.SubmitCorrection(Correct(o.Reference, "/a~1b/~0name", "4", "7"), a);
        await Assert
            .That(escaped.Value.Output.Value.GetProperty("a/b").GetProperty("~name").GetInt32())
            .IsEqualTo(7);
        var root = (LedgerResult<CorrectionReceipt>.Accepted)
            l.SubmitCorrection(Correct(o.Reference, "", o.Value.GetRawText(), "[1,2]"), a);
        await Assert.That(root.Value.Output.Value.ValueKind).IsEqualTo(JsonValueKind.Array);
        await Assert.That(root.Value.Output.Value[1].GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Other_tenant_and_other_subject_receive_same_not_found()
    {
        var (l, a, o) = Create();
        var otherTenant = new Actor("tenant-b", a.Subject);
        var otherSubject = new Actor(a.Tenant, "person-b");
        foreach (var other in new[] { otherTenant, otherSubject })
        {
            await Assert.That(Code(l.ReadOutput(o.Reference, other))).IsEqualTo("not_found");
            await Assert
                .That(Code(l.RecordGrade(Grade(o.Reference, "/nil", "null"), other)))
                .IsEqualTo("not_found");
            await Assert
                .That(Code(l.SubmitCorrection(Correct(o.Reference, "/nil", "null", "4"), other)))
                .IsEqualTo("not_found");
        }
    }

    [Test]
    public async Task Tampered_exact_reference_is_rejected()
    {
        var (l, a, o) = Create();
        foreach (
            var reference in new[]
            {
                o.Reference with
                {
                    RunId = Guid.NewGuid(),
                },
                o.Reference with
                {
                    ExecutionId = Guid.NewGuid(),
                },
                o.Reference with
                {
                    ArtifactId = Guid.NewGuid(),
                },
                o.Reference with
                {
                    VersionId = Guid.NewGuid(),
                },
                o.Reference with
                {
                    Version = 9,
                },
            }
        )
        {
            await Assert.That(Code(l.ReadOutput(reference, a))).IsEqualTo("not_found");
            await Assert
                .That(Code(l.RecordGrade(Grade(reference, "/nil", "null"), a)))
                .IsEqualTo("not_found");
        }
    }

    [Test]
    public async Task Expected_value_mismatch_does_not_reserve_id()
    {
        var (l, a, o) = Create();
        var id = Guid.NewGuid();
        await Assert
            .That(
                Code(l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "9", "5", id), a))
            )
            .IsEqualTo("value_conflict");
        var accepted = (LedgerResult<CorrectionReceipt>.Accepted)
            l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "2", "5", id), a);
        await Assert.That(accepted.Replayed).IsFalse();
        await Assert
            .That(
                accepted
                    .Value.Output.Value.GetProperty("items")[0]
                    .GetProperty("quantity")
                    .GetInt32()
            )
            .IsEqualTo(5);
    }

    [Test]
    public async Task Retry_returns_same_correction_and_changed_payload_conflicts()
    {
        var (l, a, o) = Create();
        var request = Correct(o.Reference, "/items/0/quantity", "2", "5");
        var first = (LedgerResult<CorrectionReceipt>.Accepted)l.SubmitCorrection(request, a);
        var again = (LedgerResult<CorrectionReceipt>.Accepted)l.SubmitCorrection(request, a);
        await Assert.That(first.Replayed).IsFalse();
        await Assert.That(again.Replayed).IsTrue();
        await Assert
            .That(again.Value.Output.Reference.VersionId)
            .IsEqualTo(first.Value.Output.Reference.VersionId);
        await Assert
            .That(Code(l.SubmitCorrection(request with { Replacement = J("6") }, a)))
            .IsEqualTo("idempotency_conflict");
    }

    [Test]
    public async Task Event_id_cannot_be_reused_across_feedback_types()
    {
        var (l, a, o) = Create();
        var id = Guid.NewGuid();
        var first = l.RecordGrade(Grade(o.Reference, "/items/0/quantity", "2", id: id), a);
        await Assert.That(first is LedgerResult<GradeReceipt>.Accepted).IsTrue();
        var next = l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "2", "5", id), a);
        await Assert.That(Code(next)).IsEqualTo("idempotency_conflict");
    }

    [Test]
    public async Task Supersession_requires_same_actor_version_and_field()
    {
        var (l, a, o) = Create();
        var prior = Grade(o.Reference, "/items/0/quantity", "2");
        l.RecordGrade(prior, a);
        var valid = (LedgerResult<GradeReceipt>.Accepted)
            l.RecordGrade(
                Grade(o.Reference, "/items/0/quantity", "2", .6, supersedes: prior.EventId),
                a
            );
        await Assert.That(valid.Value.Event.SupersedesEventId).IsEqualTo(prior.EventId);
        await Assert
            .That(
                Code(
                    l.RecordGrade(Grade(o.Reference, "/nil", "null", supersedes: prior.EventId), a)
                )
            )
            .IsEqualTo("invalid_supersession");
        var revised = (
            (LedgerResult<CorrectionReceipt>.Accepted)
                l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "2", "5"), a)
        )
            .Value
            .Output;
        await Assert
            .That(
                Code(
                    l.RecordGrade(
                        Grade(
                            revised.Reference,
                            "/items/0/quantity",
                            "5",
                            supersedes: prior.EventId
                        ),
                        a
                    )
                )
            )
            .IsEqualTo("invalid_supersession");
        await Assert
            .That(
                Code(
                    l.RecordGrade(
                        Grade(o.Reference, "/items/0/quantity", "2", supersedes: prior.EventId),
                        new Actor(a.Tenant, "other")
                    )
                )
            )
            .IsEqualTo("not_found");
        await Assert
            .That(
                Code(
                    l.RecordGrade(
                        Grade(o.Reference, "/items/0/quantity", "2", supersedes: Guid.NewGuid()),
                        a
                    )
                )
            )
            .IsEqualTo("invalid_supersession");
    }

    [Test]
    public async Task Independent_corrections_branch_from_original_without_overwrite()
    {
        var (l, a, o) = Create();
        var one = (
            (LedgerResult<CorrectionReceipt>.Accepted)
                l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "2", "5"), a)
        )
            .Value
            .Output;
        var two = (
            (LedgerResult<CorrectionReceipt>.Accepted)
                l.SubmitCorrection(Correct(o.Reference, "/items/0/quantity", "2", "6"), a)
        )
            .Value
            .Output;
        await Assert.That(one.Reference.VersionId).IsNotEqualTo(two.Reference.VersionId);
        await Assert.That(one.Reference.Version).IsEqualTo(two.Reference.Version);
        await Assert.That(one.ParentVersionId).IsEqualTo(o.Reference.VersionId);
        await Assert.That(two.ParentVersionId).IsEqualTo(o.Reference.VersionId);
        await Assert
            .That(one.Value.GetProperty("items")[0].GetProperty("quantity").GetInt32())
            .IsEqualTo(5);
        await Assert
            .That(two.Value.GetProperty("items")[0].GetProperty("quantity").GetInt32())
            .IsEqualTo(6);
    }

    [Test]
    public async Task Null_is_a_value_but_undefined_is_rejected()
    {
        var (l, a, o) = Create();
        var grade = (LedgerResult<GradeReceipt>.Accepted)
            l.RecordGrade(Grade(o.Reference, "/nil", "null"), a);
        await Assert.That(grade.Value.Event.ObservedValue.ValueKind).IsEqualTo(JsonValueKind.Null);
        var corrected = (LedgerResult<CorrectionReceipt>.Accepted)
            l.SubmitCorrection(Correct(o.Reference, "/nil", "null", "4"), a);
        await Assert.That(corrected.Value.Output.Value.GetProperty("nil").GetInt32()).IsEqualTo(4);
        await Assert
            .That(
                Code(
                    l.RecordGrade(
                        new GradeRequest(Guid.NewGuid(), o.Reference, "/nil", default, .5),
                        a
                    )
                )
            )
            .IsEqualTo("value_conflict");
        await Assert
            .That(
                Code(
                    l.SubmitCorrection(
                        new CorrectionRequest(Guid.NewGuid(), o.Reference, "/nil", default, J("1")),
                        a
                    )
                )
            )
            .IsEqualTo("value_conflict");
        await Assert
            .That(
                Code(
                    l.SubmitCorrection(
                        new CorrectionRequest(
                            Guid.NewGuid(),
                            o.Reference,
                            "/nil",
                            J("null"),
                            default
                        ),
                        a
                    )
                )
            )
            .IsEqualTo("invalid_correction");
    }

    [Test]
    public async Task Score_boundaries_accept_zero_and_one_reject_nonfinite_or_out_of_range()
    {
        var (l, a, o) = Create();
        foreach (double score in new[] { 0d, 1d })
        {
            var r = (LedgerResult<GradeReceipt>.Accepted)
                l.RecordGrade(Grade(o.Reference, "/nil", "null", score), a);
            await Assert.That(r.Value.Event.Score).IsEqualTo(score);
        }
        foreach (
            double score in new[]
            {
                double.NaN,
                double.PositiveInfinity,
                double.NegativeInfinity,
                -.1,
                1.1,
            }
        )
        {
            await Assert
                .That(Code(l.RecordGrade(Grade(o.Reference, "/nil", "null", score), a)))
                .IsEqualTo("invalid_grade");
        }
    }

    [Test]
    public async Task Source_json_document_can_be_disposed_after_recording()
    {
        var l = new InMemoryFeedbackLedger();
        var a = new Actor("t", "s");
        var run = l.CreateRun(a);
        OutputArtifact output;
        using (var document = JsonDocument.Parse("{\"nested\":[1,2]}"))
        {
            output = (
                (LedgerResult<OutputArtifact>.Accepted)
                    l.RecordOutput(run, Guid.NewGuid(), document.RootElement, "v1", default, a)
            ).Value;
        }

        var saved = (
            (LedgerResult<OutputArtifact>.Accepted)l.ReadOutput(output.Reference, a)
        ).Value;
        await Assert.That(saved.Value.GetProperty("nested")[1].GetInt32()).IsEqualTo(2);
        var grade = (LedgerResult<GradeReceipt>.Accepted)
            l.RecordGrade(Grade(output.Reference, "/nested/1", "2"), a);
        await Assert.That(grade.Value.Event.ObservedValue.GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Concurrent_retry_publishes_only_one_correction_version()
    {
        var (l, a, o) = Create();
        var request = Correct(o.Reference, "/items/0/quantity", "2", "9");
        var results = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => l.SubmitCorrection(request, a)))
        );
        var accepted = results.Cast<LedgerResult<CorrectionReceipt>.Accepted>().ToArray();
        await Assert.That(accepted.Count(x => !x.Replayed)).IsEqualTo(1);
        await Assert.That(accepted.Count(x => x.Replayed)).IsEqualTo(31);
        await Assert
            .That(accepted.Select(x => x.Value.Output.Reference.VersionId).Distinct().Count())
            .IsEqualTo(1);
        await Assert
            .That(
                accepted[0]
                    .Value.Output.Value.GetProperty("items")[0]
                    .GetProperty("quantity")
                    .GetInt32()
            )
            .IsEqualTo(9);
    }

    [Test]
    public async Task Grade_replay_returns_original_receipt_and_changed_score_conflicts()
    {
        var (l, a, o) = Create();
        var request = Grade(o.Reference, "/nil", "null", .4);
        var first = (LedgerResult<GradeReceipt>.Accepted)l.RecordGrade(request, a);
        var again = (LedgerResult<GradeReceipt>.Accepted)l.RecordGrade(request, a);
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Value.Event.EventId).IsEqualTo(first.Value.Event.EventId);
        await Assert.That(again.Value.Event.RecordedAt).IsEqualTo(first.Value.Event.RecordedAt);
        await Assert
            .That(Code(l.RecordGrade(request with { Score = .8 }, a)))
            .IsEqualTo("idempotency_conflict");
    }

    [Test]
    public async Task Original_output_replay_reuses_identity_and_duplicate_keys_reject()
    {
        var l = new InMemoryFeedbackLedger();
        var a = new Actor("t", "s");
        var run = l.CreateRun(a);
        var execution = Guid.NewGuid();
        var first = (LedgerResult<OutputArtifact>.Accepted)
            l.RecordOutput(run, execution, J("{\"v\":1}"), "v1", default, a);
        var again = (LedgerResult<OutputArtifact>.Accepted)
            l.RecordOutput(run, execution, J("{\"v\":1}"), "v1", default, a);
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Value.Reference).IsEqualTo(first.Value.Reference);
        await Assert
            .That(Code(l.RecordOutput(run, execution, J("{\"v\":2}"), "v1", default, a)))
            .IsEqualTo("conflict");
        await Assert
            .That(
                Code(
                    l.RecordOutput(
                        run,
                        Guid.NewGuid(),
                        J("{\"nested\":{\"x\":1,\"x\":2}}"),
                        "v1",
                        default,
                        a
                    )
                )
            )
            .IsEqualTo("invalid_output");
    }

    [Test]
    public async Task Missing_or_blank_actor_cannot_create_or_access_runs()
    {
        var ledger = new InMemoryFeedbackLedger();
        var owner = new Actor("tenant", "subject");
        var run = ledger.CreateRun(owner);
        var invalid = new Actor?[]
        {
            null,
            new Actor("", "subject"),
            new Actor("tenant", " "),
            new Actor(null!, "subject"),
            new Actor("tenant", null!),
        };
        foreach (var actor in invalid)
        {
            await Assert.That(ledger.CreateRun(actor!)).IsEqualTo(Guid.Empty);
            await Assert
                .That(
                    Code(ledger.RecordOutput(run, Guid.NewGuid(), J("{}"), "v1", default, actor!))
                )
                .IsEqualTo("not_found");
        }
        await Assert
            .That(
                Code(ledger.RecordOutput(Guid.Empty, Guid.NewGuid(), J("{}"), "v1", default, null!))
            )
            .IsEqualTo("not_found");
    }

    [Test]
    public async Task Null_output_reference_returns_not_found()
    {
        var (ledger, actor, _) = Create();
        var result = ledger.ReadOutput(null!, actor);
        await Assert.That(Code(result)).IsEqualTo("not_found");
    }
}
