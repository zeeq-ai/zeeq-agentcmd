using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Zeeq.AgentCmd;

namespace AgentCmd.CommandRoot.Tests;

/// <summary>
/// Offline coverage for the file-backed experiment source: live edits after cache expiry, strict document shape,
/// route mapping, and stale or invalid compiled revisions. Every document is written under a temporary directory.
/// </summary>
public class ExperimentFileSourceTests
{
    const string CommandId = "invoice.extract";
    const string Signature = "sig-1";

    [Test]
    public async Task FileEditAppearsAfterCacheLifetimeWithoutRecompile()
    {
        using var document = new TempDocument();
        document.Write(Document(Control("first")));
        var resolver = Resolver(document.Path, lifetime: TimeSpan.FromMilliseconds(400));

        await Assert.That(UserText(await ResolveWith(resolver))).IsEqualTo("first");

        // The edit is inside the cache lifetime, so callers still see the first publication.
        document.Write(Document(Control("second")));
        await Assert.That(UserText(await ResolveWith(resolver))).IsEqualTo("first");

        // After the absolute lifetime, the next call reloads the file and sees the edit.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        await Assert.That(UserText(await ResolveWith(resolver))).IsEqualTo("second");
    }

    [Test]
    public async Task MissingFileSelectsDefaultAsUnavailable()
    {
        using var document = new TempDocument();
        var resolver = Resolver(Path.Combine(document.Path, "does-not-exist.json"));

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.Unavailable);
    }

    [Test]
    public async Task MalformedJsonSelectsDefaultAsInvalidRelease()
    {
        using var document = new TempDocument();
        document.Write("{ \"format\": 1, \"partitions\": ");
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task MisspelledPropertyIsRejectedInsteadOfIgnored()
    {
        // A typo such as "weigth" would silently drop the share. The source must reject the whole document instead.
        using var document = new TempDocument();
        document.Write(
            Document(
                """{ "weigth": 1, "prompt": { "system": { "mode": "inherit" }, "user": { "mode": "replaced", "text": "typo" } } }"""
            )
        );
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task MissingWeightIsRejectedNotTreatedAsZero()
    {
        using var document = new TempDocument();
        document.Write(
            Document(
                """{ "prompt": { "system": { "mode": "inherit" }, "user": { "mode": "replaced", "text": "no weight" } } }"""
            )
        );
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task PartitionTheFileDoesNotNameIsAnExplicitNoOverride()
    {
        using var document = new TempDocument();
        document.Write(Document(Control("text"), partition: "tenant-b"));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver, partition: "tenant-a");

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.NoOverride);
    }

    [Test]
    public async Task RouteAndSamplingReachTheTreatmentWithoutAnyRouterInstance()
    {
        using var document = new TempDocument();
        var route =
            """, "route": { "key": "primary", "provider": "anthropic", "model": "claude-test", "temperature": 0.2, "maxOutputTokens": 512 }""";
        document.Write(Document(Control("routed", route: route)));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(resolution is ExperimentResolution.Treatment).IsTrue();
        var profile = ((ExperimentResolution.Treatment)resolution)
            .Assignment
            .View
            .Variants["control"]
            .Route;
        await Assert.That(profile is not null).IsTrue();
        await Assert.That(profile!.Provider).IsEqualTo("anthropic");
        await Assert.That(profile.Model).IsEqualTo("claude-test");
        await Assert.That(profile.Defaults!.Temperature).IsEqualTo(0.2f);
        await Assert.That(profile.Defaults!.MaxOutputTokens).IsEqualTo(512);
    }

    [Test]
    public async Task InvalidRouteKeyIsRejected()
    {
        using var document = new TempDocument();
        var route = """, "route": { "key": "", "provider": "anthropic", "model": "claude-test" }""";
        document.Write(Document(Control("routed", route: route)));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task StaleCommandRevisionSelectsConfiguredDefault()
    {
        // The file was authored for compiled revision 2, but the configured default targets revision 1.
        using var document = new TempDocument();
        document.Write(Document(Control("stale"), commandRevision: 2));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver, fallbackRevision: 1);

        await Assert
            .That(ReasonOf(resolution))
            .IsEqualTo(ExperimentFallbackReason.StaleCommandRevision);
    }

    [Test]
    public async Task ZeroCommandRevisionIsInvalid()
    {
        using var document = new TempDocument();
        document.Write(Document(Control("zero"), commandRevision: 0));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver, fallbackRevision: 0);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task PinnedRevisionNotPublishedInTheFileIsRejected()
    {
        // The file only serves its current publication. A pin for another revision must not receive it.
        using var document = new TempDocument();
        document.Write(Document(Control("current"), revision: "r2"));
        var resolver = Resolver(document.Path);
        ExperimentLookup.TryCreate(
            "tenant-a",
            ExperimentRevision.TryCreate("r1", out var pinned) ? pinned : null,
            out var lookup
        );

        var resolution = await resolver.ResolveAsync(
            lookup!,
            Fallback(),
            AgentCmdContext.Capture(),
            null,
            null,
            default
        );

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task DuplicateParameterPropertyIsRejectedInsteadOfLastWins()
    {
        // System.Text.Json would keep "fr-CA" silently. The publication must not change without an error.
        using var document = new TempDocument();
        var control =
            """{ "weight": 1, "prompt": { "system": { "mode": "inherit" }, "user": { "mode": "replaced", "text": "text" } }, "parameters": { "Locale": "en-CA", "Locale": "fr-CA" } }""";
        document.Write(Document(control));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task DuplicateKeyInsideAParameterValueIsRejected()
    {
        // Parameter values are untyped JsonElement trees. The typed option and the byte guard both have to reject this.
        using var document = new TempDocument();
        var control =
            """{ "weight": 1, "prompt": { "system": { "mode": "inherit" }, "user": { "mode": "replaced", "text": "text" } }, "parameters": { "Options": { "mode": "a", "mode": "b" } } }""";
        document.Write(Document(control));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task DuplicateVariantIdIsRejected()
    {
        using var document = new TempDocument();
        document.Write(
            Envelope($"\"control\": {Control("first")}, \"control\": {Control("second")}")
        );
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task DuplicateRoutePropertyIsRejected()
    {
        using var document = new TempDocument();
        var route =
            """, "route": { "key": "primary", "provider": "anthropic", "model": "first", "model": "second" }""";
        document.Write(Document(Control("routed", route: route)));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task EscapedDuplicateVariantNameIsRejected()
    {
        // "\u0063ontrol" unescapes to "control", so it repeats the first name and must be rejected too.
        using var document = new TempDocument();
        document.Write(
            Envelope($"\"control\": {Control("first")}, \"\\u0063ontrol\": {Control("second")}")
        );
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task DuplicateTopLevelPropertyIsRejected()
    {
        using var document = new TempDocument();
        document.Write("""{ "format": 1, "format": 1, "partitions": {} }""");
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    [Test]
    public async Task SameNameInDifferentObjectsIsStillAccepted()
    {
        // The guard tracks names per object. "weight" and "prompt" in two sibling variants are not duplicates.
        using var document = new TempDocument();
        document.Write(
            Envelope(
                $"\"control\": {Control("kept")}, \"treatment\": {Control("other", weight: "3")}"
            )
        );
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(UserText(resolution)).IsEqualTo("kept");
    }

    [Test]
    public async Task OversizedDocumentIsRejectedBeforeItIsRead()
    {
        using var document = new TempDocument();
        document.Write(new string(' ', 4 * 1024 * 1024 + 1));
        var resolver = Resolver(document.Path);

        var resolution = await ResolveWith(resolver);

        await Assert.That(ReasonOf(resolution)).IsEqualTo(ExperimentFallbackReason.InvalidRelease);
    }

    static ExperimentResolver Resolver(string path, TimeSpan? lifetime = null)
    {
        var store = new ExperimentSnapshotCache(
            new ExperimentCacheOptions
            {
                LatestDefinitionLifetime = lifetime ?? TimeSpan.FromSeconds(30),
            }
        );
        var source = new CachedExperimentSnapshotSource(
            "file-source",
            new FileExperimentSnapshotSource(path),
            store
        );
        return new ExperimentResolver(source);
    }

    static Task<ExperimentResolution> ResolveWith(
        ExperimentResolver resolver,
        string partition = "tenant-a",
        int fallbackRevision = 1
    )
    {
        var lookup = ExperimentLookup.TryCreate(partition, null, out var created)
            ? created!
            : throw new InvalidOperationException(partition);
        return resolver
            .ResolveAsync(
                lookup,
                Fallback(fallbackRevision),
                AgentCmdContext.Capture(),
                null,
                null,
                default
            )
            .AsTask();
    }

    static ConfiguredDefaultView Fallback(int commandRevision = 1) =>
        new(
            CommandId,
            Signature,
            "system",
            "compiled user",
            new Dictionary<string, JsonElement>(),
            null,
            commandRevision
        );

    /// <summary>A single control variant. It is the only variant, so it is always the one chosen without a selector.</summary>
    static string Control(string text = "text", string weight = "1", string route = "") =>
        $$"""{ "weight": {{weight}}, "prompt": { "system": { "mode": "inherit" }, "user": { "mode": "replaced", "text": "{{text}}" } }{{route}} }""";

    static string Document(
        string control,
        string partition = "tenant-a",
        string revision = "r1",
        int commandRevision = 1,
        string signature = Signature
    ) => Envelope($"\"control\": {control}", partition, revision, commandRevision, signature);

    /// <summary>Wraps raw variant members, so a test can write a duplicate key that a typed builder would never produce.</summary>
    static string Envelope(
        string variants,
        string partition = "tenant-a",
        string revision = "r1",
        int commandRevision = 1,
        string signature = Signature
    ) =>
        $$"""
            {
              "format": 1,
              "partitions": {
                "{{partition}}": {
                  "revision": "{{revision}}",
                  "commands": {
                    "{{CommandId}}": {
                      "commandRevision": {{commandRevision}},
                      "contractSignature": "{{signature}}",
                      "experimentId": "exp-1",
                      "controlVariantId": "control",
                      "variants": {
                        {{variants}}
                      }
                    }
                  }
                }
              }
            }
            """;

    /// <summary>Parses a variant's user text, or returns null when the resolution was not a treatment.</summary>
    static string? UserText(ExperimentResolution resolution) =>
        resolution is ExperimentResolution.Treatment treatment
        && treatment.Assignment.View.Variants["control"].Prompt.User
            is TemplateChange.Replaced { Text: var text }
            ? text
            : null;

    static ExperimentFallbackReason? ReasonOf(ExperimentResolution resolution) =>
        resolution is ExperimentResolution.ConfiguredDefault fallback ? fallback.Reason : null;

    /// <summary>A temporary document file that is deleted when the test ends.</summary>
    sealed class TempDocument : IDisposable
    {
        readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agentcmd-file-source-" + Guid.NewGuid().ToString("N")
        );

        public TempDocument()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Path => System.IO.Path.Combine(_directory, "experiments.json");

        public void Write(string content) => File.WriteAllText(Path, content);

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
