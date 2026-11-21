using System.Net;
using System.Security.Claims;
using Zeeq.AgentCmd.ControlPlane;

namespace Zeeq.AgentCmd.ControlPlane.Host;

/// <summary>Bootstrap settings. Remote deployments must supply an authenticated principal through a host authentication handler.</summary>
public sealed class ControlPlaneOptions
{
    /// <summary>Explicit trusted local-process mode; remote connections and foreign web origins remain denied.</summary>
    public bool LocalMode { get; set; }

    /// <summary>Actor used only by explicit loopback mode.</summary>
    public string LocalSubject { get; set; } = "local-operator";

    /// <summary>Application scope allowed in local mode.</summary>
    public string Application { get; set; } = "local";

    /// <summary>Environment scope allowed in local mode.</summary>
    public string Environment { get; set; } = "dev";

    /// <summary>Partition scope allowed in local mode.</summary>
    public string Partition { get; set; } = "default";

    /// <summary>Bootstrap provider; changing it requires migration and restart.</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Application-owned SQLite path on persistent local disk.</summary>
    public string DatabasePath { get; set; } = "artifacts/control-plane/data/management.db";

    /// <summary>Server environment-variable reference for a PostgreSQL credential; values are never returned to clients.</summary>
    public string? ConnectionStringReference { get; set; }

    /// <summary>Application-owned immutable asset root.</summary>
    public string AssetPath { get; set; } = "artifacts/control-plane/data/assets";
}

internal sealed record RequestAuthority(WorkspaceScope Scope, ManagementActor Actor);

/// <summary>Enforces identity, action and namespace authority independently of browser-selected identifiers.</summary>
internal static class ApiAccess
{
    internal static readonly string[] Permissions =
    [
        "Read",
        "Configuration.Write",
        "Data.Curate",
        "Evaluation.Launch",
        "Evaluation.Work",
        "Evaluation.Review",
        "Publication.Publish",
        "Operations.Manage",
    ];

    internal static RequestAuthority Authorize(HttpContext context, string permission)
    {
        var options = context.RequestServices.GetRequiredService<ControlPlaneOptions>();
        var user = context.User;
        if (options.LocalMode)
        {
            if (
                !Uri.TryCreate(
                    "http://" + context.Request.Host.Value,
                    UriKind.Absolute,
                    out var host
                ) || !host.IsLoopback
            )
            {
                throw new ManagementException("local_mode_requires_loopback_host", 403);
            }
            var address = context.Connection.RemoteIpAddress;
            if (address is null || !IPAddress.IsLoopback(address))
            {
                throw new ManagementException("local_mode_requires_loopback", 403);
            }
            if (
                context.Request.Headers.TryGetValue("Origin", out var origin)
                && (
                    !Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var uri)
                    || !uri.IsLoopback
                )
            )
            {
                throw new ManagementException("origin_not_allowed", 403);
            }
            user = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, options.LocalSubject) }.Concat(
                        Permissions.Select(value => new Claim("agentcmd.permission", value))
                    ),
                    "Local"
                )
            );
            context.User = user;
        }
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new ManagementException("authentication_required", 401);
        }
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ManagementException("actor_subject_required", 403);
        }
        if (
            !user.HasClaim("agentcmd.permission", permission)
            && !user.HasClaim("agentcmd.permission", "*")
        )
        {
            throw new ManagementException("action_forbidden", 403);
        }
        string Scope(string header, string claim, string local)
        {
            var value = context.Request.Headers[header].ToString();
            if (string.IsNullOrWhiteSpace(value) && options.LocalMode)
            {
                value = local;
            }
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
            {
                throw new ManagementException("scope_required");
            }
            if (
                options.LocalMode
                    ? value != local
                    : !user.HasClaim(claim, value) && !user.HasClaim(claim, "*")
            )
            {
                throw new ManagementException("scope_forbidden", 403);
            }
            return value;
        }
        var scope = new WorkspaceScope(
            Scope("X-AgentCmd-Application", "agentcmd.application", options.Application),
            Scope("X-AgentCmd-Environment", "agentcmd.environment", options.Environment),
            Scope("X-AgentCmd-Partition", "agentcmd.partition", options.Partition)
        );
        var authority = new RequestAuthority(scope, new(subject));
        context.Items[typeof(RequestAuthority)] = authority;
        return authority;
    }

    internal static long Revision(HttpContext context)
    {
        var value = context.Request.Headers.IfMatch.ToString().Trim('"');
        if (!long.TryParse(value, out var revision) || revision < 1)
        {
            throw new ManagementException("revision_precondition_required", 428);
        }
        return revision;
    }

    internal static IResult Result(ManagementResource resource, HttpContext context)
    {
        context.Response.Headers.ETag =
            '"'
            + resource.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + '"';
        return Results.Ok(resource);
    }
}
