using System.Text.Json;

namespace Zeeq.AgentCmd.ControlPlane;

/// <summary>Validated judge findings; source quotes prove text presence, not entailment.</summary>
/// <param name="Valid">Whether findings conform to the bound values and citation rules.</param>
/// <param name="HasUnresolved">Whether a finding needs further review.</param>
/// <param name="FailureCode">Sanitized rejection category.</param>
public sealed record JudgeFindingsValidation(
    bool Valid,
    bool HasUnresolved,
    string? FailureCode = null
);

/// <summary>Checks structured judge findings against exact output, reference and reviewed source.</summary>
public static class JudgeFindingsValidator
{
    /// <summary>Rejects invented pointers, duplicate credit and source locators that do not match supplied text.</summary>
    /// <remarks>Source offsets are zero-based UTF-16 code-unit offsets into the supplied source string. Coverage must come from the host, not the judge.</remarks>
    public static JudgeFindingsValidation Validate(
        JsonElement findings,
        JsonElement output,
        JsonElement? reference,
        string? source,
        string sourceCoverage
    )
    {
        if (
            findings.ValueKind != JsonValueKind.Array
            || findings.GetArrayLength() > 10000
            || sourceCoverage is not ("Complete" or "Partial" or "Unknown")
        )
        {
            return new(false, false, "judge_findings_invalid");
        }
        var candidateMatches = new HashSet<string>(StringComparer.Ordinal);
        var referenceMatches = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = false;
        foreach (var finding in findings.EnumerateArray())
        {
            if (finding.ValueKind != JsonValueKind.Object)
            {
                return new(false, false, "judge_finding_invalid");
            }
            var category = Text(finding, "category");
            var candidate = Text(finding, "candidatePointer");
            var referencePointer = Text(finding, "referencePointer");
            if (
                category
                    is not (
                        "Matched"
                        or "MissingKnownReference"
                        or "SupportedAddition"
                        or "UnsupportedInReviewedSource"
                        or "Contradiction"
                        or "Unresolved"
                        or "ReferenceConflict"
                    )
                || candidate is not null && !JsonPointer.TryGet(output, candidate, out _)
                || referencePointer is not null
                    && (
                        reference is null
                        || !JsonPointer.TryGet(reference.Value, referencePointer, out _)
                    )
            )
            {
                return new(false, false, "judge_pointer_invalid");
            }
            if (
                category == "Matched"
                    && (
                        candidate is null
                        || referencePointer is null
                        || !candidateMatches.Add(candidate)
                        || !referenceMatches.Add(referencePointer)
                    )
                || category == "MissingKnownReference" && referencePointer is null
                || category
                    is "SupportedAddition"
                        or "UnsupportedInReviewedSource"
                        or "Contradiction"
                    && candidate is null
                || category == "ReferenceConflict" && referencePointer is null
            )
            {
                return new(false, false, "judge_finding_invalid");
            }
            if (
                (
                    category
                    is "SupportedAddition"
                        or "UnsupportedInReviewedSource"
                        or "Contradiction"
                )
                    && candidate is not null
                    && !candidateMatches.Add(candidate)
                || category == "MissingKnownReference"
                    && referencePointer is not null
                    && !referenceMatches.Add(referencePointer)
            )
            {
                return new(false, false, "judge_duplicate_credit");
            }
            var hasLocator = finding.TryGetProperty("sourceLocator", out var locator);
            if (hasLocator && !ValidLocator(locator, source))
            {
                return new(false, false, "judge_source_locator_invalid");
            }
            if (
                (category is "SupportedAddition" or "Contradiction" or "ReferenceConflict")
                    && !hasLocator
                || category == "UnsupportedInReviewedSource"
                    && (source is null || sourceCoverage != "Complete")
            )
            {
                return new(false, false, "judge_source_coverage_insufficient");
            }
            unresolved |= category is "Unresolved" or "ReferenceConflict";
        }
        return new(true, unresolved);
    }

    private static bool ValidLocator(JsonElement locator, string? source)
    {
        if (
            source is null
            || locator.ValueKind != JsonValueKind.Object
            || !locator.TryGetProperty("start", out var startValue)
            || !startValue.TryGetInt32(out var start)
            || !locator.TryGetProperty("end", out var endValue)
            || !endValue.TryGetInt32(out var end)
            || Text(locator, "quote") is not { Length: > 0 } quote
            || start < 0
            || end < start
            || end > source.Length
        )
        {
            return false;
        }
        return source.AsSpan(start, end - start).SequenceEqual(quote.AsSpan());
    }

    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var member)
        && member.ValueKind == JsonValueKind.String
            ? member.GetString()
            : null;
}
