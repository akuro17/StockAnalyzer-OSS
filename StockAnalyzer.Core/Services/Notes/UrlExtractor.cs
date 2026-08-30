using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Extracts and validates http/https URLs from a Note's Body (spec section 8), mirroring
/// <see cref="HashtagExtractor"/>'s design: URLs are always auto-derived from Body text, there is
/// no separate manual URL input field.
/// </summary>
public static class UrlExtractor
{
    // Restricted to RFC 3986 URI characters (all ASCII) rather than "any non-whitespace": Japanese
    // sentences routinely follow a URL with no space at all (e.g. "...2026.pdf)を参照。"), so a
    // \S+-based match would swallow the trailing prose. Trailing punctuation that IS a legal URI
    // character but was really just sentence wrapping (a closing paren, a period) is trimmed below.
    private static readonly Regex UrlPattern = new(@"https?://[A-Za-z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+", RegexOptions.Compiled);
    private static readonly char[] TrailingPunctuation = { '.', ',', ')', ']', '}', '"', '\'', '!', '?', ';', ':' };

    /// <summary>
    /// Scans <paramref name="body"/> for http/https URLs, trims trailing sentence punctuation,
    /// validates each candidate via <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> (spec
    /// section 8: only syntactically valid http/https addresses are kept - javascript:/data:/etc.
    /// cannot occur here since the regex itself requires an http(s) scheme), and drops duplicates.
    /// Body text itself is left untouched by this method.
    /// </summary>
    public static ImmutableArray<string> Extract(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return ImmutableArray<string>.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<string>();

        foreach (Match match in UrlPattern.Matches(body))
        {
            if (!TryValidate(match.Value, out var normalized, out _))
            {
                continue;
            }

            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result.ToImmutable();
    }

    /// <summary>One substring of <paramref name="Text"/> as returned by <see cref="Tokenize"/>:
    /// either a plain-text run (<see cref="NormalizedUrl"/> is null) or a validated http(s) URL
    /// occurrence, trailing sentence punctuation already trimmed off into its own following plain
    /// token (<see cref="NormalizedUrl"/> holds the same normalized form <see cref="Extract"/> would
    /// have produced for it).</summary>
    public readonly record struct UrlToken(string Text, string? NormalizedUrl);

    /// <summary>
    /// Splits <paramref name="body"/> into alternating plain-text and URL tokens using the same
    /// <see cref="UrlPattern"/> and validation <see cref="Extract"/> uses, so a caller (e.g. a View
    /// rendering a clickable inline URL) can tell exactly which substrings are the same URLs
    /// <see cref="Extract"/> would have captured - without re-implementing the parsing/validation
    /// rules. Trailing punctuation trimmed off a match, or a match that fails URI validation, is
    /// emitted as its own plain-text token rather than folded into the URL token, so a caller never
    /// has to re-derive where the clickable span actually ends. Unlike <see cref="Extract"/>, this
    /// does not deduplicate - every occurrence in the text yields its own token, in text order.
    /// </summary>
    public static IEnumerable<UrlToken> Tokenize(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            yield break;
        }

        var lastIndex = 0;
        foreach (Match match in UrlPattern.Matches(body))
        {
            if (match.Index > lastIndex)
            {
                yield return new UrlToken(body[lastIndex..match.Index], null);
            }

            if (TryValidate(match.Value, out var normalized, out var candidateLength))
            {
                yield return new UrlToken(match.Value[..candidateLength], normalized);
                if (candidateLength < match.Value.Length)
                {
                    yield return new UrlToken(match.Value[candidateLength..], null);
                }
            }
            else
            {
                yield return new UrlToken(match.Value, null);
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < body.Length)
        {
            yield return new UrlToken(body[lastIndex..], null);
        }
    }

    /// <summary>Trims trailing sentence punctuation off a raw regex match, then validates the
    /// remainder as an absolute http(s) URI - the single shared rule <see cref="Extract"/> and
    /// <see cref="Tokenize"/> both apply, so a URL's validity and normalized form are computed in
    /// exactly one place. <paramref name="candidateLength"/> is the trimmed candidate's length
    /// within <paramref name="rawMatch"/>, letting a caller split off the trimmed punctuation.</summary>
    private static bool TryValidate(string rawMatch, out string normalized, out int candidateLength)
    {
        var candidate = rawMatch.TrimEnd(TrailingPunctuation);
        candidateLength = candidate.Length;

        if (candidate.Length == 0 ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = uri.ToString();
        return true;
    }
}
