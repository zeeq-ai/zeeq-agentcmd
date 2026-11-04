using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace AgentCmd.LiveProviders.Tests;

public class LiveSafetyTests
{
    [Test]
    public async Task External_and_secret_dependent_tests_require_explicit_selection()
    {
        var tested = 0;
        var unprotected = new List<string>();
        foreach (var type in typeof(LiveSafetyTests).Assembly.GetTypes())
        {
            var classAttributes = type.GetCustomAttributesData();
            foreach (
                var method in type.GetMethods(
                    BindingFlags.Public
                        | BindingFlags.Instance
                        | BindingFlags.Static
                        | BindingFlags.DeclaredOnly
                )
            )
            {
                var methodAttributes = method.GetCustomAttributesData();
                if (!methodAttributes.Any(a => a.AttributeType == typeof(TestAttribute)))
                {
                    continue;
                }

                var categories = classAttributes
                    .Concat(methodAttributes)
                    .Where(a => a.AttributeType == typeof(CategoryAttribute));
                if (
                    !categories.Any(a =>
                        a.ConstructorArguments.Any(arg =>
                            arg.Value is string s && (s == "Live" || s == "ConfigurationProbe")
                        )
                    )
                )
                {
                    continue;
                }

                tested++;
                if (
                    !classAttributes
                        .Concat(methodAttributes)
                        .Any(a => a.AttributeType == typeof(ExplicitAttribute))
                )
                {
                    unprotected.Add(type.Name + "." + method.Name);
                }
            }
        }
        await Assert.That(tested >= 7).IsTrue();
        await Assert.That(unprotected.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Live_gate_requires_exact_opt_in_even_if_a_test_is_filtered()
    {
        foreach (var value in new string?[] { null, "", "0", "true", " 1", "1 " })
        {
            var rejected = false;
            try
            {
                LiveSettings.RequireLiveOptIn(value);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            await Assert.That(rejected).IsTrue();
        }
        LiveSettings.RequireLiveOptIn("1");
    }
}
