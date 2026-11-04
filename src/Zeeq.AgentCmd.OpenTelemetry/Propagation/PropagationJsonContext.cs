using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeeq.AgentCmd.OpenTelemetry;

// Infrastructure roots are authored here because they are independent of any
// application's prompt DTOs. Consumers do not declare serialization markup.
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = false,
    MaxDepth = 32
)]
[JsonSerializable(typeof(WorkEnvelope))]
[JsonSerializable(typeof(ContextWire))]
internal partial class PropagationJsonContext : JsonSerializerContext;

internal sealed record ContextWire(int Version, OperationContextReference Context);
