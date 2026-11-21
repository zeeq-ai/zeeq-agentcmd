namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Declares route wiring only; injected handlers own application behavior.</summary>
internal interface IEndpoint
{
    /// <summary>Maps this feature within the host-owned API group.</summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

/// <summary>Marks feature handlers for transient dependency injection without application-assembly discovery.</summary>
internal interface IEndpointHandler { }
