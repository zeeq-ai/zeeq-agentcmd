using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

public class DynamicExperimentRouteTests
{
    sealed class Client : IChatClient
    {
        public ConcurrentQueue<ChatOptions> Options { get; } = new();
        public int Disposals;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Options.Enqueue(options!);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    sealed class MutatingFailureClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            // A provider may mutate its options. Evidence must describe what it received.
            options!.TopP = .99f;
            options.Temperature = .99f;
            throw new HttpRequestException("transient");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    sealed class BadMetadataClient : IChatClient
    {
        public int Disposals;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            throw new InvalidOperationException("metadata failure");

        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    [Test]
    public async Task DynamicRouteUsesLazyRegisteredClientAndExplicitOptions()
    {
        var baseline = new Client();
        var dynamicClient = new Client();
        var created = 0;
        var built = ProfileRouter.TryCreate(
            [new(ProfileKey.Declare("base"), "base", "base-model")],
            new Dictionary<string, ProviderRegistration>
            {
                ["base"] = new(() => baseline),
                ["dynamic"] = new(() =>
                {
                    Interlocked.Increment(ref created);
                    return dynamicClient;
                }),
            }
        );
        var router = ((RouterBuildResult.Ready)built).Router;
        var definition = new ExecutionProfile(
            ProfileKey.Declare("experiment"),
            "dynamic",
            "new-model",
            Defaults: new(Temperature: .3f, TopP: .7f)
        );
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 20)
                .Select(_ => Task.Run(() => router.TryResolveRoute(definition, _ => true)))
        );
        await Assert.That(created).IsEqualTo(1);
        var route = ((DynamicRouteResult.Ready)results[0]).Route;
        await route
            .For()
            .GetResponseAsync(
                [new ChatMessage(ChatRole.User, "hello")],
                new ChatOptions { Temperature = .9f }
            );
        var options = dynamicClient.Options.Single();
        await Assert.That(options.ModelId).IsEqualTo("new-model");
        await Assert.That(options.Temperature).IsEqualTo(.9f);
        await Assert.That(options.TopP).IsEqualTo(.7f);
        await Assert
            .That(
                results
                    .Cast<DynamicRouteResult.Ready>()
                    .All(x => x.Route.Fingerprint == route.Fingerprint)
            )
            .IsTrue();
        router.Dispose();
        await Assert.That(dynamicClient.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task RejectsUnauthorizedAndFixedModelMismatchBeforeClientCreation()
    {
        var count = 0;
        using var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [new(ProfileKey.Declare("base"), "base", "model")],
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["base"] = new(() => new Client()),
                        ["fixed"] = new(
                            () =>
                            {
                                count++;
                                return new Client();
                            },
                            FixedModel: "bound"
                        ),
                    }
                )
        ).Router;
        var bad = new ExecutionProfile(ProfileKey.Declare("dynamic"), "fixed", "other");
        await Assert
            .That(router.TryResolveRoute(bad, _ => true) is DynamicRouteResult.Invalid)
            .IsTrue();
        await Assert
            .That(
                router.TryResolveRoute(bad with { Model = "bound" }, _ => false)
                    is DynamicRouteResult.Invalid
            )
            .IsTrue();
        await Assert.That(count).IsEqualTo(0);
    }

    [Test]
    public async Task RejectsUnauthorizedFallbackBeforeCreatingTheDynamicClient()
    {
        var created = 0;
        using var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [new(ProfileKey.Declare("fallback"), "base", "base-model")],
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["base"] = new(() => new Client()),
                        ["dynamic"] = new(() =>
                        {
                            created++;
                            return new Client();
                        }),
                    }
                )
        ).Router;
        var route = new ExecutionProfile(
            ProfileKey.Declare("treatment"),
            "dynamic",
            "new-model",
            Fallback: ProfileKey.Declare("fallback")
        );

        var result = router.TryResolveRoute(route, candidate => candidate.Provider == "dynamic");

        await Assert.That(result is DynamicRouteResult.Invalid).IsTrue();
        await Assert.That(created).IsEqualTo(0);
    }

    [Test]
    public async Task FallbackUsesItsOwnSamplingAndCapturesEachAttemptBeforeMutation()
    {
        var fallback = new Client();
        using var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [
                        new(
                            ProfileKey.Declare("fallback"),
                            "base",
                            "base-model",
                            Defaults: new(Temperature: .6f, TopP: .4f)
                        ),
                    ],
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["base"] = new(() => fallback),
                        ["dynamic"] = new(() => new MutatingFailureClient()),
                    }
                )
        ).Router;
        var route = new ExecutionProfile(
            ProfileKey.Declare("treatment"),
            "dynamic",
            "new-model",
            Fallback: ProfileKey.Declare("fallback"),
            Defaults: new(Temperature: .2f, TopP: .8f)
        );
        var resolved = (DynamicRouteResult.Ready)router.TryResolveRoute(route, _ => true);
        var view = resolved.Route.For();

        await view.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        await Assert.That(fallback.Options.Single().Temperature).IsEqualTo(.6f);
        await Assert.That(fallback.Options.Single().TopP).IsEqualTo(.4f);
        await Assert.That(view.Capture!.Attempts.Count).IsEqualTo(2);
        await Assert.That(view.Capture.Attempts[0].RequestSettings!.Temperature).IsEqualTo(.2f);
        await Assert.That(view.Capture.Attempts[0].RequestSettings!.TopP).IsEqualTo(.8f);
        await Assert.That(view.Capture.Attempts[1].RequestSettings!.Temperature).IsEqualTo(.6f);
        await Assert.That(view.Capture.Attempts[1].RequestSettings!.TopP).IsEqualTo(.4f);
        await Assert.That(view.Capture.ServedProfile).IsEqualTo("fallback");
    }

    [Test]
    public async Task FailedLazyMetadataValidationDisposesOwnedClient()
    {
        var broken = new BadMetadataClient();
        using var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [new(ProfileKey.Declare("base"), "base", "base-model")],
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["base"] = new(() => new Client()),
                        ["broken"] = new(() => broken, FixedModel: "bound"),
                    }
                )
        ).Router;
        var route = new ExecutionProfile(ProfileKey.Declare("dynamic"), "broken", "bound");

        var result = router.TryResolveRoute(route, _ => true);

        await Assert.That(result is DynamicRouteResult.Invalid).IsTrue();
        await Assert.That(broken.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task FailedLazyValidationDoesNotDisposeAnExistingAlias()
    {
        var client = new BadMetadataClient();
        var router = (
            (RouterBuildResult.Ready)
                ProfileRouter.TryCreate(
                    [new(ProfileKey.Declare("base"), "base", "base-model")],
                    new Dictionary<string, ProviderRegistration>
                    {
                        ["base"] = new(() => client),
                        ["alias"] = new(() => client, FixedModel: "bound"),
                    }
                )
        ).Router;
        var route = new ExecutionProfile(ProfileKey.Declare("dynamic"), "alias", "bound");

        var result = router.TryResolveRoute(route, _ => true);

        await Assert.That(result is DynamicRouteResult.Invalid).IsTrue();
        await Assert.That(client.Disposals).IsEqualTo(0);
        router.Dispose();
        await Assert.That(client.Disposals).IsEqualTo(1);
    }
}
