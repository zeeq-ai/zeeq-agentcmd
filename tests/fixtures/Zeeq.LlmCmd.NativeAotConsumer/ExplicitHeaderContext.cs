using System.Text.Json.Serialization;

namespace CompositionDemo;

// The explicit override probe also checks STJ's fast-path wrapper against the
// default generated resolver; converter implementation names are not the wire.
[JsonSerializable(typeof(Header))]
internal partial class ExplicitHeaderContext : JsonSerializerContext;
