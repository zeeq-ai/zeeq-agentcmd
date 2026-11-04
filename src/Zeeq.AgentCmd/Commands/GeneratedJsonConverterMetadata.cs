using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;

namespace Zeeq.AgentCmd;

/// <summary>Records generated attribute-converter provenance without sharing mutable converter state.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class GeneratedJsonConverterMetadata
{
    private sealed record Declaration(Type ConverterType);

    private static readonly ConditionalWeakTable<JsonTypeInfo, Declaration> Declarations = new();

    /// <summary>Marks metadata whose concrete converter came from the contract's converter attribute.</summary>
    /// <typeparam name="TContract">Contract described by the generated metadata.</typeparam>
    /// <param name="metadata">New metadata with its own converter instance, before configuration becomes read-only.</param>
    /// <returns>The same metadata, carrying declaration provenance for composition preflight.</returns>
    public static JsonTypeInfo<TContract> AttributeDeclared<TContract>(
        JsonTypeInfo<TContract> metadata
    )
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Declarations.GetValue(metadata, static info => new(info.Converter.GetType()));
        return metadata;
    }

    // The declaration, not a process-global converter instance, is shared across
    // assemblies. Explicit contexts and options-level converters remain unmarked.
    internal static bool SameDeclaration(JsonTypeInfo left, JsonTypeInfo right) =>
        left.Type == right.Type
        && Declarations.TryGetValue(left, out var a)
        && Declarations.TryGetValue(right, out var b)
        && a.ConverterType == b.ConverterType
        && left.Converter.GetType() == a.ConverterType
        && right.Converter.GetType() == b.ConverterType;
}
