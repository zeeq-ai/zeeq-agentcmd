using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.LlmCmd;

namespace LlmCmd.CommandRoot.Tests;

/// <summary>Pins the receiver identity that local registration analysis follows.</summary>
public sealed class BuilderChainingTests
{
    [Test]
    public async Task Every_supported_builder_operation_keeps_the_same_registration_scope()
    {
        var identities = new List<bool>();
        var profile = ProfileKey.Declare("typed");
        var actor = new Actor("tenant", "reviewer");
        new ServiceCollection().AddLlmCmd(builder =>
        {
            // The analyzer follows a chain as one mutable scope. Returning a
            // different builder would invalidate that assumption even if setup ran.
            identities.Add(ReferenceEquals(builder, builder.AddChatClient("keyed")));
            identities.Add(ReferenceEquals(builder, builder.AddChatClient("owned", _ => null!)));
            identities.Add(
                ReferenceEquals(builder, builder.AddProfile("default", "keyed", "model"))
            );
            identities.Add(ReferenceEquals(builder, builder.AddProfile(profile, "owned", "model")));
            identities.Add(ReferenceEquals(builder, builder.UseBudget(new())));
            identities.Add(ReferenceEquals(builder, builder.UseDefaultProfile("default")));
            identities.Add(ReferenceEquals(builder, builder.UseActor(_ => actor)));
        });

        new ServiceCollection().AddLlmCmd(builder =>
        {
            // Factories stay deferred: this test establishes builder identity
            // without constructing a client or making a provider request.
            identities.Add(ReferenceEquals(builder, builder.UseChatClient(_ => null!)));
            identities.Add(ReferenceEquals(builder, builder.AddProfile("legacy", "model")));
            identities.Add(ReferenceEquals(builder, builder.AddProfile(profile, "model")));
            identities.Add(ReferenceEquals(builder, builder.UseDefaultProfile(profile)));
            identities.Add(ReferenceEquals(builder, builder.UseActor(_ => actor)));
        });

        await Assert.That(identities.Count).IsEqualTo(12);
        await Assert.That(identities.All(same => same)).IsTrue();
    }
}
