using System.Text.Json;
using HumanGrading;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace LlmCmd.HumanGrading.Tests;

public class ProducerContracts
{
    static JsonElement J(string json) { using var doc=JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    [Test] public async Task Composite_has_its_own_producer_and_no_model_execution_identity()
    {
        var ledger=new HumanLedger(); var actor=new Actor("tenant","worker"); var run=ledger.CreateRun(actor); var draft=Guid.NewGuid();
        var first=(LedgerResult<OutputArtifact>.Accepted)ledger.RecordProducedOutput(run,new ProducerRef.Composition(draft),J("{\"total\":42}"),"document-v1",default,actor);
        var repeat=(LedgerResult<OutputArtifact>.Accepted)ledger.RecordProducedOutput(run,new ProducerRef.Composition(draft),J("{\"total\":42}"),"document-v1",default,actor);
        await Assert.That(first.Value.Reference.ExecutionId).IsNull();
        await Assert.That(first.Value.Reference.Producer is ProducerRef.Composition { Id:var id } && id==draft).IsTrue();
        await Assert.That(repeat.Replayed).IsTrue();
        await Assert.That(repeat.Value.Reference).IsEqualTo(first.Value.Reference);
        await Assert.That(ledger.CanAccessRun(run,actor)).IsTrue();
        await Assert.That(ledger.CanAccessRun(run,new Actor("other","worker"))).IsFalse();
    }

    [Test] public async Task Composite_grade_and_correction_keep_exact_version_and_composition_ancestry()
    {
        var ledger=new HumanLedger(); var actor=new Actor("tenant","worker"); var run=ledger.CreateRun(actor); var draft=Guid.NewGuid();
        var output=((LedgerResult<OutputArtifact>.Accepted)ledger.RecordProducedOutput(run,new ProducerRef.Composition(draft),J("{\"total\":42}"),"v1",default,actor)).Value;
        var grade=(LedgerResult<GradeReceipt>.Accepted)ledger.RecordGrade(new(Guid.NewGuid(),output.Reference,"/total",J("42"),.8),actor);
        var id=Guid.NewGuid(); var request=new CorrectionRequest(id,output.Reference,"/total",J("42"),J("43"));
        var correction=(LedgerResult<CorrectionReceipt>.Accepted)ledger.SubmitCorrection(request,actor);
        var retry=(LedgerResult<CorrectionReceipt>.Accepted)ledger.SubmitCorrection(request,actor);
        await Assert.That(grade.Value.Event.Target).IsEqualTo(output.Reference);
        await Assert.That(correction.Value.Output.Reference.Producer is ProducerRef.HumanCorrection).IsTrue();
        await Assert.That(correction.Value.Output.Reference.Producer.SourceDraftId).IsEqualTo(draft);
        await Assert.That(correction.Value.Output.Reference.ExecutionId).IsNull();
        await Assert.That(retry.Replayed).IsTrue();
        await Assert.That(retry.Value.Output.Reference).IsEqualTo(correction.Value.Output.Reference);
        await Assert.That(((LedgerResult<OutputArtifact>.Accepted)ledger.ReadOutput(output.Reference,actor)).Value.Value.GetProperty("total").GetInt32()).IsEqualTo(42);
    }

    [Test] public async Task Producer_references_roundtrip_without_turning_corrections_into_command_outputs()
    {
        var id=Guid.NewGuid(); var command=new OutputRef(Guid.NewGuid(),id,Guid.NewGuid(),Guid.NewGuid(),1);
        var correction=command with { VersionId=Guid.NewGuid(),Version=2,Producer=new ProducerRef.HumanCorrection(Guid.NewGuid(),command.Producer) };
        foreach(var reference in new[] { command,correction,new OutputRef(Guid.NewGuid(),new ProducerRef.Composition(Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid(),1) })
        {
            var json=JsonSerializer.Serialize(reference); var read=JsonSerializer.Deserialize<OutputRef>(json)!;
            await Assert.That(read).IsEqualTo(reference);
        }
        await Assert.That(correction.ExecutionId).IsEqualTo(id);
    }

    [Test] public async Task Store_read_options_allow_reordered_polymorphic_metadata()
    {
        var reference=new OutputRef(Guid.NewGuid(),new ProducerRef.Composition(Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid(),1);
        // Database JSON storage can reorder object properties. Its read options
        // must accept the discriminator after ordinary producer properties.
        var json=$$"""{"RunId":"{{reference.RunId}}","Producer":{"Id":"{{reference.Producer.Id}}","$producer":"composition"},"ArtifactId":"{{reference.ArtifactId}}","VersionId":"{{reference.VersionId}}","Version":1}""";
        var options=new JsonSerializerOptions { AllowOutOfOrderMetadataProperties=true };
        await Assert.That(JsonSerializer.Deserialize<OutputRef>(json,options)).IsEqualTo(reference);
    }

    [Test] public async Task Malformed_reference_has_no_execution_identity_and_is_rejected_by_the_ledger()
    {
        var ledger=new HumanLedger(); var actor=new Actor("tenant","worker");
        var reference=new OutputRef(ledger.CreateRun(actor),null!,Guid.NewGuid(),Guid.NewGuid(),1);
        await Assert.That(reference.ExecutionId).IsNull();
        var wire=JsonSerializer.Serialize(reference);
        await Assert.That(JsonSerializer.Deserialize<OutputRef>(wire)!.ExecutionId).IsNull();
        await Assert.That(ledger.ReadOutput(reference,actor) is LedgerResult<OutputArtifact>.Rejected).IsTrue();
    }
}
