using System.Diagnostics;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Optional management tracing excludes retained inputs, credentials and prompt content.</summary>
internal static class ControlPlaneTelemetry
{
    internal static readonly ActivitySource Activity = new("Zeeq.AgentCmd.ControlPlane");

    internal static Activity? Start(string operation, RequestAuthority authority)
    {
        var activity = Activity.StartActivity(operation);
        activity?.SetTag("agentcmd.management.operation", operation);
        activity?.SetTag("agentcmd.management.scope", ContentIdentity.Hash(authority.Scope.Key));
        return activity;
    }
}
