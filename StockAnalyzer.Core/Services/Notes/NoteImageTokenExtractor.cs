using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Embeds/extracts inline-image placeholder tokens in a Note's Body (Image Display Mode = Inline
/// places an attached image at its original text-cursor position), mirroring
/// <see cref="UrlExtractor"/>'s Tokenize-based design so a caller (the View) can split Body into
/// alternating plain-text and image-token runs without re-implementing the token format.
/// </summary>
public static class NoteImageTokenExtractor
{
    private static readonly Regex Pattern = new(
        @"\[\[note-image:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]\]",
        RegexOptions.Compiled);

    /// <summary>Builds the literal placeholder token text for <paramref name="id"/> - the single
    /// shared format <see cref="Tokenize"/>/<see cref="RemoveToken"/>/<see cref="ReplaceToken"/> all
    /// read and write.</summary>
    public static string Build(Guid id) => $"[[note-image:{id:D}]]";

    /// <summary>True when a complete placeholder token in <paramref name="body"/> straddles
    /// <paramref name="cutIndex"/> (starts before it, ends after it). Used by
    /// <see cref="NoteReadMorePreview.BuildCollapsedText"/>, which truncates Body by raw character
    /// count and would otherwise leak a dangling token fragment as visible garbled text when the cut
    /// lands mid-token, at any position within it (prefix, guid, or suffix) - <see cref="Tokenize"/>
    /// can only recognize a complete, well-formed token, so a partial one is indistinguishable from
    /// ordinary plain text to it. Matches against the full, untruncated <paramref name="body"/> (the
    /// same <see cref="Pattern"/> <see cref="Tokenize"/> uses) rather than inspecting the
    /// already-cut substring, so a partial prefix at the very end of the cut is caught too.
    /// <paramref name="danglingStartIndex"/> is where the straddling token begins, so a caller can
    /// truncate back to exclude it entirely.</summary>
    public static bool TryFindDanglingTokenStart(string body, int cutIndex, out int danglingStartIndex)
    {
        foreach (Match match in Pattern.Matches(body))
        {
            if (match.Index >= cutIndex)
            {
                break;
            }

            if (match.Index + match.Length > cutIndex)
            {
                danglingStartIndex = match.Index;
                return true;
            }
        }

        danglingStartIndex = -1;
        return false;
    }

    /// <summary>One substring of Body as returned by <see cref="Tokenize"/>: either a plain-text run
    /// (<see cref="AttachmentId"/> is null) or a placeholder token occurrence.</summary>
    public readonly record struct ImageToken(string Text, Guid? AttachmentId);

    /// <summary>
    /// Splits <paramref name="body"/> into alternating plain-text and image-token runs, in text
    /// order (same lastIndex-walk shape as <see cref="UrlExtractor.Tokenize"/>).
    /// </summary>
    public static IEnumerable<ImageToken> Tokenize(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            yield break;
        }

        var lastIndex = 0;
        foreach (Match match in Pattern.Matches(body))
        {
            if (match.Index > lastIndex)
            {
                yield return new ImageToken(body[lastIndex..match.Index], null);
            }

            yield return new ImageToken(match.Value, Guid.Parse(match.Groups[1].Value));

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < body.Length)
        {
            yield return new ImageToken(body[lastIndex..], null);
        }
    }

    /// <summary>Removes every occurrence of <paramref name="id"/>'s placeholder token from
    /// <paramref name="body"/> (a pending attachment that fails validation at Save must never leave
    /// a dangling token behind).</summary>
    public static string RemoveToken(string body, Guid id) => body.Replace(Build(id), string.Empty);

    /// <summary>Replaces every occurrence of <paramref name="oldId"/>'s placeholder token with
    /// <paramref name="newId"/>'s (Save success: a staged LocalId token becomes the real
    /// AttachmentId).</summary>
    public static string ReplaceToken(string body, Guid oldId, Guid newId) => body.Replace(Build(oldId), Build(newId));
}
