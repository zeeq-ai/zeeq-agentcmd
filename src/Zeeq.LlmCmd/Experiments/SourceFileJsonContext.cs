using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.LlmCmd;

/// <summary>
/// Source-generated JSON metadata for the file-backed experiment source. The generator emits every serializer, so
/// reflection is not used and the source stays compatible with NativeAOT and trimming.
/// </summary>
/// <remarks>
/// Unmapped members are rejected. A misspelled property such as "weight" would otherwise be ignored, and the variant
/// would silently lose its share of traffic. Repeated names are rejected too, because the last one would win silently.
/// Comments and trailing commas are rejected to keep the edited file strict. <see cref="ExperimentFileDuplicateGuard"/>
/// checks the raw bytes at every depth, which also covers the untyped parameter values.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    AllowDuplicateProperties = false
)]
[JsonSerializable(typeof(ExperimentFileDocument))]
internal sealed partial class ExperimentFileJsonContext : JsonSerializerContext { }
