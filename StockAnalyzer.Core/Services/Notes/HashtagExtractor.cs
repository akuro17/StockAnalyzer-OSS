using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Extracts and normalizes hashtags from a Note's Body (spec section 3.1). Hashtags are always
/// auto-derived from Body text - there is no separate manual tag input field.
/// </summary>
public static class HashtagExtractor
{
    public const int MaxHashtagCount = 30;
    public const int MaxHashtagLength = 50;

    // Unicode letters/digits/underscore, matching Japanese and alphanumeric hashtags alike.
    private static readonly Regex HashtagPattern = new(@"#[\p{L}\p{N}_]+", RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="body"/> for #hashtags, trims and ASCII-lowercases each one for
    /// duplicate comparison (full-width/half-width unification is a known v1 limitation, spec
    /// section 3.1), drops duplicates, truncates any tag over 50 characters, and caps the result
    /// at 30 tags. Body text itself is left untouched by this method.
    /// </summary>
    public static ImmutableArray<string> Extract(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return ImmutableArray<string>.Empty;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<string>();

        foreach (Match match in HashtagPattern.Matches(body))
        {
            if (result.Count >= MaxHashtagCount)
            {
                break;
            }

            var tag = match.Value[1..].Trim();
            if (tag.Length == 0)
            {
                continue;
            }

            var normalized = NormalizeTag(tag);
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// Returns <paramref name="body"/> with every #hashtag substring removed. Used to build the
    /// hashtag-free preview text cached in TickerMetadata.Notes (spec section 4.4). Unlike
    /// <see cref="Extract"/>, this never mutates the stored Note.Body itself - it only produces a
    /// derived string for a caller (the preview builder) to use.
    /// </summary>
    public static string RemoveHashtags(string? body) =>
        string.IsNullOrEmpty(body) ? string.Empty : HashtagPattern.Replace(body, string.Empty);

    /// <summary>One substring of <paramref name="Text"/> as returned by <see cref="Tokenize"/>:
    /// either a plain-text run (<see cref="NormalizedHashtag"/> is null) or a literal "#tag"
    /// occurrence (<see cref="NormalizedHashtag"/> holds the same normalized form <see cref="Extract"/>
    /// would have produced for it - trimmed, length-capped, ASCII-lowercased).</summary>
    public readonly record struct BodyToken(string Text, string? NormalizedHashtag);

    /// <summary>
    /// Splits <paramref name="body"/> into alternating plain-text and hashtag tokens using the same
    /// <see cref="HashtagPattern"/> and normalization <see cref="Extract"/> uses, so a caller (e.g. a
    /// View rendering clickable inline hashtags) can tell exactly which substrings are the same
    /// hashtags <see cref="Extract"/> would have captured - without re-implementing the parsing rules.
    /// Unlike <see cref="Extract"/>, this does not deduplicate or cap at <see cref="MaxHashtagCount"/>:
    /// every occurrence in the text yields its own token, in text order.
    /// </summary>
    public static IEnumerable<BodyToken> Tokenize(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            yield break;
        }

        var lastIndex = 0;
        foreach (Match match in HashtagPattern.Matches(body))
        {
            if (match.Index > lastIndex)
            {
                yield return new BodyToken(body[lastIndex..match.Index], null);
            }

            var rawTag = match.Value[1..].Trim();
            yield return new BodyToken(match.Value, rawTag.Length == 0 ? null : NormalizeTag(rawTag));

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < body.Length)
        {
            yield return new BodyToken(body[lastIndex..], null);
        }
    }

    /// <summary>Length-caps then ASCII-lowercases a single already-trimmed tag (no leading '#') -
    /// the shared normalization <see cref="Extract"/> and <see cref="Tokenize"/> both apply, so a
    /// hashtag's normalized form is computed in exactly one place.</summary>
    private static string NormalizeTag(string tag)
    {
        if (tag.Length > MaxHashtagLength)
        {
            tag = tag[..MaxHashtagLength];
        }

        return NormalizeAsciiCase(tag);
    }

    private static string NormalizeAsciiCase(string tag)
    {
        var chars = tag.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] <= 0x7F)
            {
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
        }

        return new string(chars);
    }
}
