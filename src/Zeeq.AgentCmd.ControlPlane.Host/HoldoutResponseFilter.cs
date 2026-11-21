using Microsoft.AspNetCore.Http.HttpResults;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Applies one browser evidence boundary to read and mutation responses; trusted worker claims are separate payloads.</summary>
internal sealed class HoldoutResponseFilter(RelationalControlPlaneStore store) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var result = await next(context);
        if (result is not IValueHttpResult value || value.Value is null)
        {
            return result;
        }
        var authority =
            context.HttpContext.Items[typeof(RequestAuthority)] as RequestAuthority
            ?? throw new ManagementException("request_authority_unavailable", 503);
        var cancellationToken = context.HttpContext.RequestAborted;
        object projected;
        if (value.Value is ManagementResource resource)
        {
            projected = await store.ProjectBrowserResourceAsync(
                authority.Scope,
                resource,
                cancellationToken
            );
        }
        else if (value.Value is IEnumerable<ManagementResource> resources)
        {
            var list = new List<ManagementResource>();
            foreach (var item in resources)
            {
                list.Add(
                    await store.ProjectBrowserResourceAsync(
                        authority.Scope,
                        item,
                        cancellationToken
                    )
                );
            }
            projected = list;
        }
        else if (value.Value is ComparisonReport comparison)
        {
            projected = await store.ProjectBrowserComparisonAsync(
                authority.Scope,
                comparison,
                cancellationToken
            );
        }
        else
        {
            return result;
        }
        if (result is Created<ManagementResource> created && created.Location is not null)
        {
            context.HttpContext.Response.Headers.Location = created.Location;
        }
        return Results.Json(
            projected,
            statusCode: (result as IStatusCodeHttpResult)?.StatusCode ?? 200
        );
    }
}
