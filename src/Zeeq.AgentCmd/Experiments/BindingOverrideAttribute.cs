namespace Zeeq.AgentCmd;

/// <summary>
/// Permits a runtime experiment to replace this scalar input property when the generated binding proves the input is immutable.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RuntimeOverrideAttribute : Attribute;
