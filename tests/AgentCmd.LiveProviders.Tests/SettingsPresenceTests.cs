using Microsoft.Extensions.Configuration;
using TUnit.Core;

namespace AgentCmd.LiveProviders.Tests;

// Explicit because this test deliberately reads user configuration. It never
// writes values or makes network requests; only presence flags leave the test.
[Explicit, Category("ConfigurationProbe")]
public class SettingsPresenceTests
{
    [Test]
    public void Print_presence_only()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<LiveSettings>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        using var lifetime = config as IDisposable;
        foreach (var item in SettingsPresence.Read(config))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }
    }
}
