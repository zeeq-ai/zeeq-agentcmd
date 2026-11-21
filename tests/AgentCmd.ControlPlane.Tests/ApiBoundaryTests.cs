using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace AgentCmd.ControlPlane.Tests;

public class ApiBoundaryTests
{
    private static async Task<(
        Microsoft.AspNetCore.Builder.WebApplication App,
        HttpClient Client,
        string Directory
    )> StartAsync(bool local)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "agentcmd-api-" + Guid.NewGuid().ToString("N")
        );
        var app = ControlPlaneApplication.Build(
            [
                "--urls=http://127.0.0.1:0",
                "--ControlPlane:LocalMode=" + local,
                "--ControlPlane:DatabasePath=" + Path.Combine(directory, "management.db"),
                "--ControlPlane:AssetPath=" + Path.Combine(directory, "assets"),
            ],
            builder => builder.Logging.ClearProviders()
        );
        await app.StartAsync();
        var address = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        return (app, new HttpClient { BaseAddress = new Uri(address) }, directory);
    }

    [Test]
    public async Task UnconfiguredIdentityDoesNotGrantReadOrMutationAccess()
    {
        var (app, client, directory) = await StartAsync(false);
        try
        {
            using var read = await client.GetAsync("/api/v1/workspace");
            using var write = await client.PostAsJsonAsync(
                "/api/v1/datasets",
                new
                {
                    name = "unauthorized",
                    body = new { purpose = "Regression", cases = Array.Empty<object>() },
                }
            );
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            await Assert.That(write.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task ScopeAndForeignWebOriginsCannotBypassExplicitLocalMode()
    {
        var (app, client, directory) = await StartAsync(true);
        try
        {
            using var crossScope = new HttpRequestMessage(HttpMethod.Get, "/api/v1/builds");
            crossScope.Headers.Add("X-AgentCmd-Partition", "other-tenant");
            using var forbiddenScope = await client.SendAsync(crossScope);
            using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/datasets")
            {
                Content = JsonContent.Create(
                    new
                    {
                        name = "foreign",
                        body = new { purpose = "Regression", cases = Array.Empty<object>() },
                    }
                ),
            };
            foreign.Headers.Add("Origin", "https://untrusted.example");
            using var forbiddenOrigin = await client.SendAsync(foreign);
            await Assert.That(forbiddenScope.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(forbiddenOrigin.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workspace");
            rebound.Headers.Host = "untrusted.example";
            using var reboundResponse = await client.SendAsync(rebound);
            await Assert.That(reboundResponse.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public async Task HttpDraftTransitionsRequireRevisionAndFreezeValidDataOnly()
    {
        var (app, client, directory) = await StartAsync(true);
        try
        {
            var body = new
            {
                name = "invoices",
                body = new
                {
                    purpose = "Regression",
                    cases = new[]
                    {
                        new
                        {
                            id = "c1",
                            sourceFamilyId = "doc1",
                            input = new { Document = "Net 30 days" },
                            assets = Array.Empty<string>(),
                            tags = Array.Empty<string>(),
                            exposed = false,
                        },
                    },
                },
            };
            using var created = await client.PostAsJsonAsync("/api/v1/datasets", body);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created);
            var resource = await created.Content.ReadFromJsonAsync<JsonElement>();
            var id = resource.GetProperty("id").GetString();
            using var noToken = await client.PutAsJsonAsync("/api/v1/datasets/" + id, body);
            await Assert.That((int)noToken.StatusCode).IsEqualTo(428);
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/datasets/" + id)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            using var saved = await client.SendAsync(request);
            await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
            using var stale = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/datasets/" + id + "/freeze"
            );
            stale.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            using var conflict = await client.SendAsync(stale);
            await Assert.That(conflict.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            using var freeze = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/datasets/" + id + "/freeze"
            );
            freeze.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
            using var frozen = await client.SendAsync(freeze);
            await Assert.That(frozen.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert
                .That(
                    (await frozen.Content.ReadFromJsonAsync<JsonElement>())
                        .GetProperty("state")
                        .GetString()
                )
                .IsEqualTo("Frozen");
            using var openapi = await client.GetAsync("/openapi/v1.json");
            var schema = await openapi.Content.ReadFromJsonAsync<JsonElement>();
            var post = schema
                .GetProperty("paths")
                .GetProperty("/api/v1/datasets")
                .GetProperty("post");
            await Assert
                .That(post.GetProperty("operationId").GetString())
                .IsEqualTo("CreateDatasets");
            var requestSchema = post.GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema");
            await Assert
                .That(
                    requestSchema
                        .GetProperty("properties")
                        .GetProperty("body")
                        .GetProperty("properties")
                        .TryGetProperty("cases", out _)
                )
                .IsTrue();
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
