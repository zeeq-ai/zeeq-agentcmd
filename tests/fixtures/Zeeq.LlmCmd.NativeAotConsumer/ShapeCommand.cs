using System.Collections.Immutable;
using ExternalNative;
using Zeeq.LlmCmd;

namespace CompositionDemo;

/// <summary>Input for a graph-shaped result probe.</summary>
public sealed record ShapeInput(string Document);

/// <summary>Generic DTO that preserves nullable type arguments.</summary>
public sealed record NativeBox<TValue>(TValue Value);

/// <summary>Nested line used in the graph.</summary>
public sealed record ShapeItem(string Description, decimal Amount);

/// <summary>Exercises collections and closed generic metadata without authored registrations.</summary>
public sealed record ShapeOutput(
    NativeBox<string?> Label,
    IReadOnlyList<ShapeItem> Items,
    Dictionary<string, decimal> Totals,
    ImmutableArray<int> Codes,
    NativeCode Code
);

/// <summary>Executes one synthetic graph-shaped response.</summary>
public sealed partial class ShapeCommand : PromptCommand<ShapeInput, ShapeOutput>
{
    /// <summary>Stable command identity.</summary>
    public const string Id = "native.shapes";

    /// <summary>Prompt understood by the fake client.</summary>
    public const string UserTemplate = "SHAPES {Document}";
}
