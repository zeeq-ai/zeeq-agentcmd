using SerializationSpikes;

var oracle = new OracleSerializer();
var codec = new CodecSerializer();
var whole = Conformance.Sample();
var directory = "/tmp/opencode/llmcmd-serialization-aot/codec";
File.WriteAllText(Path.Combine(directory, "oracle-whole.json"), oracle.Serialize(whole));
File.WriteAllText(
    Path.Combine(directory, "codec-whole.json"),
    codec.Serialize(codec.Deserialize<DocumentWhole>(oracle.Serialize(whole)))
);
File.WriteAllText(
    Path.Combine(directory, "oracle-schema.json"),
    oracle.Schema<DocumentWhole>().GetRawText()
);
File.WriteAllText(
    Path.Combine(directory, "codec-schema.json"),
    codec.Schema<DocumentWhole>().GetRawText()
);
File.WriteAllText(
    Path.Combine(directory, "oracle-parameters-schema.json"),
    oracle.Schema<DocumentParameters>().GetRawText()
);
File.WriteAllText(
    Path.Combine(directory, "codec-parameters-schema.json"),
    codec.Schema<DocumentParameters>().GetRawText()
);
File.WriteAllText(
    Path.Combine(directory, "oracle-array-schema.json"),
    oracle.Schema<Line[]>().GetRawText()
);
File.WriteAllText(
    Path.Combine(directory, "codec-array-schema.json"),
    codec.Schema<Line[]>().GetRawText()
);
