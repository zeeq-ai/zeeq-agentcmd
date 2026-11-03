using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

namespace SerializationSpikes;

public sealed record CheckResult(string Name, bool Passed, string? Detail = null);

/// <summary>
/// Runs identical data through each implementation. The oracle defines wire
/// compatibility; explicit expectations keep an oracle regression visible too.
/// </summary>
public static class Conformance
{
    public static int Run(ISpikeSerializer candidate, string[] args)
    {
        var results = Evaluate(candidate);
        var directory =
            args.FirstOrDefault(argument =>
                argument.StartsWith("--report-dir=", StringComparison.Ordinal)
            )?[13..]
            ?? Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        WriteReports(directory, candidate.Name, results);
        Console.WriteLine(
            $"{candidate.Name}: {results.Count(result => result.Passed)}/{results.Count} checks passed; reflection enabled: {JsonSerializer.IsReflectionEnabledByDefault}"
        );
        foreach (var failure in results.Where(result => !result.Passed))
        {
            Console.WriteLine($"FAIL {failure.Name}: {failure.Detail}");
        }

        return results.All(result => result.Passed) ? 0 : 1;
    }

    public static IReadOnlyList<CheckResult> Evaluate(ISpikeSerializer candidate)
    {
        var oracle = new OracleSerializer();
        var results = new List<CheckResult>();
        var whole = Sample();
        var wholeJson = oracle.Serialize(whole);
        var parameters = new DocumentParameters("A deterministic local document.");

        Check(
            "reflection-is-disabled",
            () =>
                Require(
                    !JsonSerializer.IsReflectionEnabledByDefault,
                    "Reflection must be disabled."
                )
        );
        Roundtrip("parameters", parameters);
        Roundtrip("header", whole.Header);
        Roundtrip("line-array", whole.Lines.ToArray());
        Roundtrip("whole", whole);
        Schema<DocumentParameters>("parameters");
        Schema<Header>("header");
        Schema<Line[]>("line-array");
        Schema<DocumentWhole>("whole");

        Check(
            "optional-constructor-default",
            () =>
                Require(
                    candidate.Deserialize<DocumentParameters>("{\"document\":\"x\"}").Locale
                        == "en",
                    "Locale default was lost."
                )
        );
        Check(
            "optional-member-default",
            () =>
                Require(
                    candidate
                        .Deserialize<Line[]>(
                            "[{\"description\":\"x\",\"amount\":1,\"kind\":\"Goods\"}]"
                        )[0]
                        .Quantity == 1,
                    "Quantity default was lost."
                )
        );
        Check(
            "optional-whole-default",
            () =>
                Require(
                    candidate.Deserialize<DocumentWhole>(Edit(node => node.Remove("count"))).Count
                        == 3,
                    "Whole Count default was lost."
                )
        );
        Check(
            "init-default",
            () =>
                Require(
                    candidate
                        .Deserialize<DocumentWhole>(
                            Edit(node => node["review"]!.AsObject().Remove("revision"))
                        )
                        .Review.Revision == 1,
                    "Init default was lost."
                )
        );
        Check(
            "nullable-generic-value",
            () =>
                Require(
                    candidate.Deserialize<DocumentWhole>(wholeJson).OptionalLabel.Value is null,
                    "Nullable generic value changed."
                )
        );
        Check(
            "mapped-and-ignored-properties",
            () =>
            {
                var node = JsonNode.Parse(candidate.Serialize(whole))!.AsObject();
                Require(
                    node.ContainsKey("header_data") && !node.ContainsKey("header"),
                    "Whole JSON name changed."
                );
                Require(
                    node["header_data"]!["document_number"]!.GetValue<string>() == "INV-42",
                    "Mapped member was lost."
                );
                Require(
                    !node["review"]!.AsObject().ContainsKey("localOnly"),
                    "Ignored member leaked."
                );
            }
        );
        Check(
            "company-derived-roundtrip",
            () =>
            {
                var company = whole with { Party = new CompanyParty("Acme", "R-17") };
                var read = candidate.Deserialize<DocumentWhole>(oracle.Serialize(company));
                Require(
                    read.Party is CompanyParty { Registration: "R-17" },
                    "Approved derived type was lost."
                );
            }
        );
        Check(
            "escaped-property-name",
            () =>
            {
                var json = oracle
                    .Serialize(whole.Header)
                    .Replace("document_number", "document\\u005Fnumber", StringComparison.Ordinal);
                Require(
                    candidate.Deserialize<Header>(json).Number == "INV-42",
                    "Escaped name did not resolve."
                );
            }
        );
        Check(
            "duplicate-property-last-wins",
            () =>
                Require(
                    candidate
                        .Deserialize<DocumentParameters>(
                            "{\"document\":\"first\",\"document\":\"second\"}"
                        )
                        .Document == "second",
                    "Duplicate policy differs from STJ."
                )
        );
        Check(
            "dictionary-values",
            () =>
                Require(
                    candidate.Deserialize<DocumentWhole>(wholeJson).Totals["net"] == 12.50m,
                    "Dictionary values changed."
                )
        );
        Check(
            "immutable-array",
            () =>
                Require(
                    candidate.Deserialize<DocumentWhole>(wholeJson).Codes.SequenceEqual([7, 11]),
                    "Immutable array changed."
                )
        );

        Reject<DocumentParameters>("missing-required-constructor", "{}");
        Reject<DocumentParameters>("null-required-constructor", "{\"document\":null}");
        Reject<DocumentParameters>("wrong-scalar-type", "{\"document\":7}");
        Reject<DocumentParameters>("unknown-member", "{\"document\":\"x\",\"other\":1}");
        Reject<DocumentParameters>("wrong-property-case", "{\"Document\":\"x\"}");
        Reject<DocumentParameters>("malformed-json", "{\"document\":");
        Reject<Line[]>(
            "integer-overflow",
            "[{\"description\":\"x\",\"amount\":1,\"kind\":\"Goods\",\"quantity\":2147483648}]"
        );
        Reject<Line[]>(
            "numeric-string",
            "[{\"description\":\"x\",\"amount\":\"1\",\"kind\":\"Goods\"}]"
        );
        Reject<Line[]>(
            "decimal-overflow",
            "[{\"description\":\"x\",\"amount\":1e400,\"kind\":\"Goods\"}]"
        );
        Reject<Line[]>(
            "unknown-enum",
            "[{\"description\":\"x\",\"amount\":1,\"kind\":\"Unexpected\"}]"
        );
        Reject<DocumentWhole>(
            "missing-required-init",
            Edit(node => node["review"]!.AsObject().Remove("reviewer"))
        );
        Reject<DocumentWhole>(
            "null-required-init",
            Edit(node => node["review"]!["reviewer"] = null)
        );
        Reject<DocumentWhole>("null-nested", Edit(node => node["header_data"]!["address"] = null));
        Reject<DocumentWhole>(
            "null-generic-nonnullable",
            Edit(node => node["destination"]!["value"] = null)
        );
        Reject<DocumentWhole>("invalid-custom-converter", Edit(node => node["code"] = "BAD"));
        Reject<DocumentWhole>("unknown-derived", Edit(node => node["party"]!["$kind"] = "unknown"));
        Reject<DocumentWhole>(
            "missing-derived-field",
            Edit(node => node["party"]!.AsObject().Remove("age"))
        );
        Reject<DocumentWhole>(
            "unknown-nested-field",
            Edit(node => node["header_data"]!["address"]!["extra"] = true)
        );
        Check(
            "late-discriminator",
            () =>
            {
                var json = Edit(node =>
                    node["party"] = JsonNode.Parse(
                        "{\"name\":\"Ada\",\"age\":37,\"$kind\":\"person\"}"
                    )
                );
                // On the pinned SDK this reaches abstract construction before it
                // finds the late discriminator, so STJ throws NotSupportedException.
                Require(
                    FailureType(() => oracle.Deserialize<DocumentWhole>(json))
                        == typeof(NotSupportedException),
                    "Oracle late-discriminator policy changed."
                );
                Require(
                    FailureType(() => candidate.Deserialize<DocumentWhole>(json))
                        == typeof(NotSupportedException),
                    "Late discriminator exception differs from STJ."
                );
            }
        );
        Reject<DocumentWhole>(
            "duplicate-discriminator",
            wholeJson.Replace(
                "\"$kind\":\"person\"",
                "\"$kind\":\"person\",\"$kind\":\"company\"",
                StringComparison.Ordinal
            )
        );

        Check(
            "error-path-nested-null",
            () =>
            {
                var json = Edit(node => node["header_data"]!["address"] = null);
                Require(
                    JsonFailure(() => candidate.Deserialize<DocumentWhole>(json)).Path
                        == JsonFailure(() => oracle.Deserialize<DocumentWhole>(json)).Path,
                    "Nested JsonException.Path differs from STJ."
                );
            }
        );
        Check(
            "error-path-overflow",
            () =>
            {
                const string json =
                    "[{\"description\":\"x\",\"amount\":1,\"kind\":\"Goods\",\"quantity\":2147483648}]";
                Require(
                    JsonFailure(() => candidate.Deserialize<Line[]>(json)).Path
                        == JsonFailure(() => oracle.Deserialize<Line[]>(json)).Path,
                    "Numeric JsonException.Path differs from STJ."
                );
            }
        );
        Check(
            "collection-element-null-policy",
            () =>
            {
                var json = Edit(node => node["lines"] = JsonNode.Parse("[null]"));
                // Neither STJ nullable metadata nor this experiment replaces the
                // library's generated recursive validator for collection elements.
                var baseline = oracle.Deserialize<DocumentWhole>(json);
                var actual = candidate.Deserialize<DocumentWhole>(json);
                Require(
                    baseline.Lines[0] is null && actual.Lines[0] is null,
                    "Collection-element policy differs from STJ."
                );
            }
        );

        Check(
            "read-and-evaluate-stored-whole",
            () =>
            {
                // A separate byte store stands in for the ledger. Readback must use
                // the published whole rather than borrow a component's identity.
                var version = Guid.NewGuid();
                var store = new Dictionary<Guid, string> { [version] = wholeJson };
                var restored = candidate.Deserialize<DocumentWhole>(store[version]);
                Require(
                    restored.Totals["net"]
                        == restored.Lines.Sum(line => line.Amount * line.Quantity),
                    "Cross-part total is invalid."
                );
                Require(
                    restored.Header.DocumentId == whole.Header.DocumentId
                        && restored.Party.Name == "Ada",
                    "Whole readback lost identity or lineage data."
                );
            }
        );

        return results;

        void Check(string name, Action action)
        {
            try
            {
                action();
                results.Add(new CheckResult(name, true));
            }
            catch (Exception exception)
            {
                results.Add(
                    new CheckResult(name, false, $"{exception.GetType().Name}: {exception.Message}")
                );
            }
        }

        void Roundtrip<TValue>(string name, TValue value)
        {
            Check(
                $"serialize-{name}",
                () => EqualJson(oracle.Serialize(value), candidate.Serialize(value))
            );
            Check(
                $"roundtrip-{name}",
                () =>
                    EqualJson(
                        oracle.Serialize(value),
                        oracle.Serialize(candidate.Deserialize<TValue>(oracle.Serialize(value)))
                    )
            );
            Check(
                $"stored-bytes-{name}",
                () =>
                    Require(
                        oracle.Serialize(value)
                            == candidate.Serialize(
                                candidate.Deserialize<TValue>(oracle.Serialize(value))
                            ),
                        "Stored bytes changed on read/write."
                    )
            );
        }

        void Schema<TValue>(string name)
        {
            Check(
                $"schema-{name}",
                () =>
                    EqualJson(
                        oracle.Schema<TValue>().GetRawText(),
                        candidate.Schema<TValue>().GetRawText()
                    )
            );
        }

        void Reject<TValue>(string name, string json)
        {
            Check(
                name,
                () =>
                {
                    Require(
                        FailsAsJson(() => oracle.Deserialize<TValue>(json)),
                        "Test vector is not rejected by oracle as JsonException."
                    );
                    Require(
                        FailsAsJson(() => candidate.Deserialize<TValue>(json)),
                        "Candidate did not reject as JsonException."
                    );
                }
            );
        }

        string Edit(Action<JsonObject> edit)
        {
            var node = JsonNode.Parse(wholeJson)!.AsObject();
            edit(node);
            return node.ToJsonString();
        }
    }

    public static DocumentWhole Sample()
    {
        return new DocumentWhole(
            new Header(
                "INV-42",
                new Address("Boston", "02110"),
                new DateOnly(2026, 11, 2),
                Guid.Parse("11111111-2222-3333-4444-555555555555")
            ),
            [new Line("Local fixture", 6.25m, LineKind.Goods, 2)],
            new Dictionary<string, decimal> { ["net"] = 12.50m },
            new Box<string?>(null),
            new Box<Address>(new Address("Boston", "02110")),
            new ReviewState { Reviewer = "local-eval" },
            new PersonParty("Ada", 37),
            ImmutableArray.Create(7, 11),
            new DocumentCode("DOC-42")
        );
    }

    private static bool FailsAsJson(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static Type? FailureType(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception.GetType();
        }
    }

    private static JsonException JsonFailure(Action action)
    {
        try
        {
            action();
        }
        catch (JsonException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected JsonException.");
    }

    private static void EqualJson(string expected, string actual)
    {
        Require(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            "JSON differs from the source-generated oracle."
        );
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void WriteReports(
        string directory,
        string name,
        IReadOnlyList<CheckResult> results
    )
    {
        using (var stream = File.Create(Path.Combine(directory, "results.json")))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("candidate", name);
            writer.WriteBoolean("reflectionEnabled", JsonSerializer.IsReflectionEnabledByDefault);
            writer.WriteStartArray("checks");
            foreach (var result in results)
            {
                writer.WriteStartObject();
                writer.WriteString("name", result.Name);
                writer.WriteString("status", result.Passed ? "passed" : "failed");
                if (result.Detail is not null)
                {
                    writer.WriteString("message", result.Detail);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var xml = XmlWriter.Create(
            Path.Combine(directory, "tests.xml"),
            new XmlWriterSettings { Indent = true }
        );
        xml.WriteStartElement("testsuite");
        xml.WriteAttributeString("name", name);
        xml.WriteAttributeString(
            "tests",
            results.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        xml.WriteAttributeString(
            "failures",
            results
                .Count(result => !result.Passed)
                .ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        foreach (var result in results)
        {
            xml.WriteStartElement("testcase");
            xml.WriteAttributeString("name", result.Name);
            if (!result.Passed)
            {
                xml.WriteElementString("failure", result.Detail);
            }

            xml.WriteEndElement();
        }

        xml.WriteEndElement();
    }
}
