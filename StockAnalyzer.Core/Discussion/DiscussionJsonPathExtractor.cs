using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// Picks fields out of a stored analysis result by path (the <c>paths</c> argument of <c>sa_discussion_get_analysis</c>), so that a large result need not travel whole.
/// <para>
/// Grammar (a manual scanner, no regular expression, no backtracking):
/// <c>path = name { "." name | "[" index "]" | "[" quoted "]" }</c>.
/// A <c>name</c> is one or more of <c>A-Z a-z 0-9 _</c> and always an object property (so <c>a.5</c> is the property "5"). An <c>index</c> is <c>0</c> or one to six digits
/// without a leading zero and always an array index. A <c>quoted</c> key is written between double quotes, with only the escapes <c>\"</c> and <c>\\</c>, is at least one
/// character long, holds no control character and no unpaired surrogate, and always names an object property (so <c>["0"]</c> is the property "0", never the index 0).
/// The first token must be a name. A path has at most <see cref="DiscussionExtensionLimits.MaxJsonExtractPathSegments"/> segments and
/// <see cref="DiscussionExtensionLimits.MaxJsonExtractPathChars"/> characters. It is not JSONPath: there is no <c>$</c>, no wildcard and no filter.
/// </para>
/// <para>
/// The value of a path is written with <see cref="JsonElement.WriteTo(Utf8JsonWriter)"/>, so a number keeps its exact text; it is never read as a double or a decimal.
/// </para>
/// </summary>
internal static class DiscussionJsonPathExtractor
{
    /// <summary>One step of a path: an object property (<see cref="Name"/> set) or an array index (<see cref="Name"/> null).</summary>
    internal readonly record struct Segment(string? Name, int Index);

    /// <summary>
    /// Checks the list of paths and parses each of them. Distinctness is by the exact text of the path (two spellings of one place are allowed).
    /// </summary>
    /// <exception cref="DiscussionException"><c>INVALID_ARGUMENT</c>, parameter <c>paths</c>: the list is empty or too long, a path is too long or breaks the grammar, or a path is repeated.</exception>
    public static IReadOnlyList<Segment[]> ParseAll(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count < 1 || paths.Count > DiscussionExtensionLimits.MaxJsonExtractPaths)
        {
            throw Invalid($"paths must hold 1 to {DiscussionExtensionLimits.MaxJsonExtractPaths} paths.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parsed = new List<Segment[]>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (path.Length < 1 || path.Length > DiscussionExtensionLimits.MaxJsonExtractPathChars)
            {
                throw Invalid(string.Create(CultureInfo.InvariantCulture, $"paths[{i}] must be 1 to {DiscussionExtensionLimits.MaxJsonExtractPathChars} characters."));
            }

            if (!TryParse(path, out Segment[] segments, out string reason))
            {
                throw Invalid(string.Create(CultureInfo.InvariantCulture, $"paths[{i}] {reason}"));
            }

            if (!seen.Add(path))
            {
                throw Invalid(string.Create(CultureInfo.InvariantCulture, $"paths[{i}] repeats an earlier path."));
            }

            parsed.Add(segments);
        }

        return parsed;
    }

    /// <summary>
    /// Writes <c>"extracted"</c> (an object from each path that exists to its value, in the order of the request; a JSON null that is there counts as present) and
    /// <c>"missingPaths"</c> (the paths that do not exist, in the order of the request) as two members of the object the writer is inside.
    /// </summary>
    /// <param name="resultJson">The stored result text (it was valid JSON when it was stored).</param>
    /// <param name="paths">The paths as the caller wrote them (used as the member names).</param>
    /// <param name="parsed">The result of <see cref="ParseAll"/> for <paramref name="paths"/>.</param>
    public static void Write(Utf8JsonWriter writer, string resultJson, IReadOnlyList<string> paths, IReadOnlyList<Segment[]> parsed)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(resultJson);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(resultJson);
        }
        catch (JsonException)
        {
            throw new DiscussionException(DiscussionErrorCodes.InternalError, "The stored result could not be read.");
        }

        using (document)
        {
            var missing = new List<string>();
            writer.WriteStartObject("extracted");
            for (int i = 0; i < paths.Count; i++)
            {
                if (TryResolve(document.RootElement, parsed[i], out JsonElement value))
                {
                    writer.WritePropertyName(paths[i]);
                    value.WriteTo(writer);
                }
                else
                {
                    missing.Add(paths[i]);
                }
            }

            writer.WriteEndObject();
            writer.WriteStartArray("missingPaths");
            foreach (string path in missing)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
        }
    }

    private static bool TryResolve(JsonElement root, Segment[] segments, out JsonElement value)
    {
        JsonElement current = root;
        foreach (Segment segment in segments)
        {
            if (segment.Name is { } name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out JsonElement property))
                {
                    value = default;
                    return false;
                }

                current = property;
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Array || segment.Index >= current.GetArrayLength())
                {
                    value = default;
                    return false;
                }

                current = current[segment.Index];
            }
        }

        value = current;
        return true;
    }

    // ---- The scanner ----------------------------------------------------------------------------------------------------------------------

    private static bool TryParse(string path, out Segment[] segments, out string reason)
    {
        var found = new List<Segment>();
        segments = Array.Empty<Segment>();
        int position = 0;

        // The first token is a name.
        if (!TryReadName(path, ref position, out string name))
        {
            reason = "must start with a name (letters, digits and underscore).";
            return false;
        }

        found.Add(new Segment(name, 0));
        while (position < path.Length)
        {
            if (found.Count >= DiscussionExtensionLimits.MaxJsonExtractPathSegments)
            {
                reason = $"has more than {DiscussionExtensionLimits.MaxJsonExtractPathSegments} segments.";
                return false;
            }

            char current = path[position];
            if (current == '.')
            {
                position++;
                if (!TryReadName(path, ref position, out name))
                {
                    reason = "has a '.' that is not followed by a name.";
                    return false;
                }

                found.Add(new Segment(name, 0));
            }
            else if (current == '[')
            {
                position++;
                if (position < path.Length && path[position] == '"')
                {
                    if (!TryReadQuoted(path, ref position, out string key, out reason))
                    {
                        return false;
                    }

                    found.Add(new Segment(key, 0));
                }
                else
                {
                    if (!TryReadIndex(path, ref position, out int index))
                    {
                        reason = $"has an array index that is not 0 or 1 to {DiscussionExtensionLimits.MaxJsonExtractIndexDigits} digits without a leading zero.";
                        return false;
                    }

                    found.Add(new Segment(null, index));
                }

                if (position >= path.Length || path[position] != ']')
                {
                    reason = "has a '[' without a closing ']'.";
                    return false;
                }

                position++;
            }
            else
            {
                reason = "has a character where '.' or '[' is expected.";
                return false;
            }
        }

        segments = found.ToArray();
        reason = string.Empty;
        return true;
    }

    private static bool TryReadName(string path, ref int position, out string name)
    {
        int start = position;
        while (position < path.Length && (char.IsAsciiLetterOrDigit(path[position]) || path[position] == '_'))
        {
            position++;
        }

        name = path[start..position];
        return position > start;
    }

    private static bool TryReadIndex(string path, ref int position, out int index)
    {
        int start = position;
        while (position < path.Length && char.IsAsciiDigit(path[position]))
        {
            position++;
        }

        int digits = position - start;
        index = 0;
        if (digits < 1 || digits > DiscussionExtensionLimits.MaxJsonExtractIndexDigits || (digits > 1 && path[start] == '0'))
        {
            return false;
        }

        index = int.Parse(path.AsSpan(start, digits), NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Reads <c>"key"</c> with the position on the opening quote; leaves it after the closing quote.</summary>
    private static bool TryReadQuoted(string path, ref int position, out string key, out string reason)
    {
        var decoded = new StringBuilder();
        key = string.Empty;
        position++; // the opening quote
        while (true)
        {
            if (position >= path.Length)
            {
                reason = "has a quoted key without a closing quote.";
                return false;
            }

            char current = path[position];
            if (current == '"')
            {
                position++;
                break;
            }

            if (current == '\\')
            {
                if (position + 1 >= path.Length || path[position + 1] is not ('"' or '\\'))
                {
                    reason = "has a quoted key with an escape other than \\\" and \\\\.";
                    return false;
                }

                decoded.Append(path[position + 1]);
                position += 2;
                continue;
            }

            if (current < ' ')
            {
                reason = "has a quoted key with a control character.";
                return false;
            }

            if (char.IsHighSurrogate(current))
            {
                if (position + 1 >= path.Length || !char.IsLowSurrogate(path[position + 1]))
                {
                    reason = "has a quoted key with an unpaired surrogate.";
                    return false;
                }

                decoded.Append(current).Append(path[position + 1]);
                position += 2;
                continue;
            }

            if (char.IsLowSurrogate(current))
            {
                reason = "has a quoted key with an unpaired surrogate.";
                return false;
            }

            decoded.Append(current);
            position++;
        }

        if (decoded.Length == 0)
        {
            reason = "has an empty quoted key.";
            return false;
        }

        key = decoded.ToString();
        reason = string.Empty;
        return true;
    }

    private static DiscussionException Invalid(string message) => new(DiscussionErrorCodes.InvalidArgument, message, "paths");
}
