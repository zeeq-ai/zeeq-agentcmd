using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.HumanGrading.Tests;

public class ExperimentEvidenceTests
{
    static JsonElement J(string value) => JsonDocument.Parse(value).RootElement.Clone();

    static string Code<T>(LedgerResult<T> result) => ((LedgerResult<T>.Rejected)result).Code;

    [Test]
    public async Task EvidenceIsAtomicAndDetachedFromCallerCollectionsAndDocuments()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var producer = new ProducerRef.CommandExecution(Guid.NewGuid());
        using var document = JsonDocument.Parse("{\"number\":7}");
        var parameters = new List<ExperimentParameter>
        {
            new("Count", "int", document.RootElement, "caller"),
        };
        var members = new List<OutputRef>
        {
            new(run, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1),
        };
        var lineage = new List<CompositeMember>
        {
            new(
                "header",
                "Header",
                members[0],
                MemberProvenance.Executed,
                Guid.NewGuid(),
                Guid.NewGuid()
            ),
        };
        var evidence = new ExperimentEvidence(
            "source",
            Revision: "v1",
            Parameters: parameters,
            Members: members,
            CompositionMembers: lineage
        );
        var result = (LedgerResult<OutputArtifact>.Accepted)
            ledger.RecordProducedOutputWithEvidence(
                run,
                producer,
                J("42"),
                "v1",
                default,
                actor,
                evidence
            );
        parameters.Clear();
        members.Clear();
        lineage.Clear();
        document.Dispose();
        var stored = (
            (LedgerResult<OutputArtifact>.Accepted)ledger.ReadOutput(result.Value.Reference, actor)
        ).Value;
        await Assert.That(stored.Evidence!.Parameters!.Count).IsEqualTo(1);
        await Assert
            .That(stored.Evidence.Parameters[0].Value.GetProperty("number").GetInt32())
            .IsEqualTo(7);
        await Assert.That(stored.Evidence.Members!.Count).IsEqualTo(1);
        await Assert.That(stored.Evidence.CompositionMembers![0].SlotId).IsEqualTo("header");
        await Assert
            .That(stored.Evidence.Parameters is IList<ExperimentParameter> list && list.IsReadOnly)
            .IsTrue();
    }

    [Test]
    public async Task ReplayComparesOutputAndCompleteEvidenceAndRejectsLegacyMismatch()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var producer = new ProducerRef.CommandExecution(Guid.NewGuid());
        var evidence = new ExperimentEvidence(
            "source",
            Revision: "one",
            Parameters: [new("p", "int", J("1"), "caller")]
        );
        var first = (LedgerResult<OutputArtifact>.Accepted)
            ledger.RecordProducedOutputWithEvidence(
                run,
                producer,
                J("42"),
                "v1",
                default,
                actor,
                evidence
            );
        var same = (LedgerResult<OutputArtifact>.Accepted)
            ledger.RecordProducedOutputWithEvidence(
                run,
                producer,
                J("42"),
                "v1",
                default,
                actor,
                evidence with
                {
                    Parameters = [new("p", "int", J("1"), "caller")],
                }
            );
        await Assert.That(same.Value.Reference).IsEqualTo(first.Value.Reference);
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        producer,
                        J("43"),
                        "v1",
                        default,
                        actor,
                        evidence
                    )
                )
            )
            .IsEqualTo("conflict");
        var member = new CompositeMember(
            "slot",
            "Slot",
            new OutputRef(run, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1),
            MemberProvenance.Executed,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        var aggregate = new ProducerRef.Composition(Guid.NewGuid());
        var aggregateEvidence = evidence with { CompositionMembers = [member] };
        ledger.RecordProducedOutputWithEvidence(
            run,
            aggregate,
            J("42"),
            "v1",
            default,
            actor,
            aggregateEvidence
        );
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        aggregate,
                        J("42"),
                        "v1",
                        default,
                        actor,
                        aggregateEvidence with
                        {
                            CompositionMembers = [member with { SlotId = "other" }],
                        }
                    )
                )
            )
            .IsEqualTo("conflict");
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        producer,
                        J("42"),
                        "v1",
                        default,
                        actor,
                        evidence with
                        {
                            Revision = "two",
                        }
                    )
                )
            )
            .IsEqualTo("conflict");
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        producer,
                        J("42"),
                        "v1",
                        default,
                        actor,
                        evidence with
                        {
                            Parameters = [new("p", "int", J("2"), "caller")],
                        }
                    )
                )
            )
            .IsEqualTo("conflict");
        await Assert
            .That(Code(ledger.RecordProducedOutput(run, producer, J("42"), "v1", default, actor)))
            .IsEqualTo("conflict");
        var legacy = new ProducerRef.CommandExecution(Guid.NewGuid());
        ledger.RecordProducedOutput(run, legacy, J("42"), "v1", default, actor);
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        legacy,
                        J("42"),
                        "v1",
                        default,
                        actor,
                        evidence
                    )
                )
            )
            .IsEqualTo("conflict");
    }

    [Test]
    public async Task InvalidAndUnauthorizedWritesAreRejectedAndCorrectionRetainsLineage()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var producer = new ProducerRef.CommandExecution(Guid.NewGuid());
        var evidence = new ExperimentEvidence(
            "source",
            Parameters: [new("p", "int", default, "caller")]
        );
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        producer,
                        J("42"),
                        "v1",
                        default,
                        actor,
                        evidence
                    )
                )
            )
            .IsEqualTo("invalid_evidence");
        await Assert
            .That(
                Code(
                    ledger.RecordProducedOutputWithEvidence(
                        run,
                        producer,
                        J("42"),
                        "v1",
                        default,
                        new Actor("other", "subject"),
                        evidence
                    )
                )
            )
            .IsEqualTo("not_found");
        var valid = evidence with { Parameters = [new("p", "int", J("1"), "caller")] };
        var output = (
            (LedgerResult<OutputArtifact>.Accepted)
                ledger.RecordProducedOutputWithEvidence(
                    run,
                    producer,
                    J("42"),
                    "v1",
                    default,
                    actor,
                    valid
                )
        ).Value;
        var correction = new CorrectionRequest(
            Guid.NewGuid(),
            output.Reference,
            "",
            J("42"),
            J("43")
        );
        var corrected = (
            (LedgerResult<CorrectionReceipt>.Accepted)ledger.SubmitCorrection(correction, actor)
        )
            .Value
            .Output;
        await Assert.That(corrected.Evidence!.Source).IsEqualTo("source");
        await Assert.That(corrected.ParentVersionId).IsEqualTo(output.Reference.VersionId);
        await Assert.That(corrected.Reference.Producer is ProducerRef.HumanCorrection).IsTrue();
    }

    [Test]
    public async Task ConcurrentMatchingWritesCommitOneArtifact()
    {
        var ledger = new InMemoryFeedbackLedger();
        var actor = new Actor("tenant", "subject");
        var run = ledger.CreateRun(actor);
        var producer = new ProducerRef.Composition(Guid.NewGuid());
        var evidence = new ExperimentEvidence("source", Revision: "one");
        var writes = await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(_ =>
                    Task.Run(() =>
                        ledger.RecordProducedOutputWithEvidence(
                            run,
                            producer,
                            J("42"),
                            "v1",
                            default,
                            actor,
                            evidence
                        )
                    )
                )
        );
        var accepted = writes.Cast<LedgerResult<OutputArtifact>.Accepted>().ToArray();
        await Assert.That(accepted.Count(x => !x.Replayed)).IsEqualTo(1);
        await Assert.That(accepted.Select(x => x.Value.Reference).Distinct().Count()).IsEqualTo(1);
        await Assert.That(accepted.All(x => x.Value.Evidence?.Revision == "one")).IsTrue();
    }
}
