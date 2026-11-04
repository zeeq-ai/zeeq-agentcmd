using System.Text.Json;

namespace Zeeq.LlmCmd;

/// <summary>
/// Rejects repeated property names in an experiment document, at every depth, before the document is deserialized.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json keeps the last value of a repeated name by default. A duplicate override, variant, or route key
/// would then change the publication silently. The source-generated context sets <c>AllowDuplicateProperties</c> to
/// false, and that is the primary rejection. This guard is a second, independent layer. It does not depend on that
/// option being honored by a later serializer change, and it checks the exact bytes before any value is built.
/// </para>
/// <para>
/// The check uses <see cref="Utf8JsonReader"/> only. It needs no reflection, so it is NativeAOT-safe. Names are
/// compared after unescaping, so "control" and "control" count as the same name.
/// </para>
/// </remarks>
internal static class ExperimentFileDuplicateGuard
{
    // Matches the serializer's default depth limit, so the guard and the typed parse reject the same deep documents.
    const int MaxDepth = 64;

    /// <summary>Checks that the document is well-formed JSON with no repeated property name in any object.</summary>
    /// <param name="utf8Json">Complete document bytes.</param>
    /// <returns>True when the document is well-formed and has no repeated property name; otherwise false.</returns>
    public static bool IsStrictJson(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            }
        );

        // One entry per open container: the names seen so far for an object, or null for an array.
        var scopes = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;

                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        scopes.Pop();
                        break;

                    case JsonTokenType.PropertyName:
                        if (
                            scopes.Peek() is not { } names
                            || !names.Add(reader.GetString() ?? string.Empty)
                        )
                        {
                            return false;
                        }

                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }
}
