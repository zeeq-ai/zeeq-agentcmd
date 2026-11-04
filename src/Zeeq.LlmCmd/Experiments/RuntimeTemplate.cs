using System.Text;

namespace Zeeq.LlmCmd;

/// <summary>
/// Renders external text from previously frozen scalar values without interpreting those values as templates.
/// </summary>
public static class RuntimeTemplate
{
    /// <summary>Renders a template using case-sensitive CLR property names and the compiled renderer's brace grammar.</summary>
    /// <param name="template">The externally supplied template.</param>
    /// <param name="values">Invariant-formatted values captured from the typed input.</param>
    /// <param name="maximumLength">The maximum accepted template length.</param>
    /// <returns>The rendered text.</returns>
    /// <exception cref="ArgumentException">The template is too long, malformed, or references an unknown property.</exception>
    public static string Render(
        string template,
        IReadOnlyDictionary<string, string?> values,
        int maximumLength = 65536
    )
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (maximumLength < 0 || template.Length > maximumLength)
        {
            throw new ArgumentException(
                "Template exceeds the configured size limit.",
                nameof(template)
            );
        }

        var result = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];
            if ((ch == '{' || ch == '}') && i + 1 < template.Length && template[i + 1] == ch)
            {
                result.Append(ch);
                i++;
                continue;
            }

            if (ch == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (
                    end < 0
                    || !TryGetOrdinal(values, template.Substring(i + 1, end - i - 1), out var value)
                )
                {
                    throw new ArgumentException(
                        "Template contains an invalid or unknown placeholder.",
                        nameof(template)
                    );
                }

                result.Append(value);
                i = end;
                continue;
            }

            if (ch == '}')
            {
                throw new ArgumentException(
                    "Template contains an unmatched closing brace.",
                    nameof(template)
                );
            }

            result.Append(ch);
        }

        return result.ToString();
    }

    private static bool TryGetOrdinal(
        IReadOnlyDictionary<string, string?> values,
        string key,
        out string? value
    )
    {
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }
}
