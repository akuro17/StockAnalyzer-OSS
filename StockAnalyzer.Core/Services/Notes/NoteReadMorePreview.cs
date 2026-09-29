using System;
using System.Linq;

namespace StockAnalyzer.Core.Services.Notes;

/// <summary>
/// Shared "Read more" collapse-boundary logic (Settings &gt; Notes: ReadMoreMaxCharacters/
/// ReadMoreMaxLines), the single source of truth for what a Note body looks like before its
/// "Read more" toggle is expanded. Consumed by both the Notes tab's card display
/// (NoteTimelineItemViewModel.DisplayBody) and the Tickers-tab Notes column preview
/// (TickerMetadataNotesCacheSynchronizer), so the two never define the collapse boundary
/// differently.
/// </summary>
public static class NoteReadMorePreview
{
    /// <summary>sa_minimal_fix (Inline Mode "+N" expand bug investigation): an inline
    /// [[note-image:{guid}]] placeholder token counts as this many "characters" toward the Read-more
    /// budget, matching what it actually renders as (one small image) rather than its own 51-character
    /// markup length - counting the literal markup as prose length used to collapse a Note with just a
    /// few images and no other text prematurely, silently dropping trailing images from the preview
    /// even though there was nothing for a reader to "read more" of.</summary>
    private const int ImageTokenCharacterWeight = 1;

    /// <summary>True when <paramref name="body"/> exceeds either threshold and therefore needs a
    /// "Read more" toggle at all.</summary>
    public static bool RequiresCollapse(string body, int maxCharacters, int maxLines)
        => EffectiveLength(body) > maxCharacters || CountNewlines(body) > maxLines;

    /// <summary>The collapsed ("before Read more") text: <paramref name="body"/> unchanged when it
    /// doesn't require collapsing, otherwise capped first to <paramref name="maxLines"/> lines, then
    /// to <paramref name="maxCharacters"/> effective characters (see <see cref="EffectiveLength"/>).</summary>
    public static string BuildCollapsedText(string body, int maxCharacters, int maxLines)
    {
        if (!RequiresCollapse(body, maxCharacters, maxLines))
        {
            return body;
        }

        var lines = body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var lineLimited = lines.Length > maxLines
            ? string.Join("\n", lines.Take(maxLines))
            : body;

        return EffectiveLength(lineLimited) <= maxCharacters
            ? lineLimited
            : TruncateToEffectiveLength(lineLimited, maxCharacters);
    }

    /// <summary>Sums a plain-text run at its literal character length, but an inline image
    /// placeholder token (see <see cref="NoteImageTokenExtractor"/>) at
    /// <see cref="ImageTokenCharacterWeight"/> instead of its own markup length.</summary>
    private static int EffectiveLength(string body)
        => NoteImageTokenExtractor.Tokenize(body)
            .Sum(token => token.AttachmentId is not null ? ImageTokenCharacterWeight : token.Text.Length);

    /// <summary>Walks the token stream, keeping each image token intact - never partially included,
    /// same "a token is an atomic unit" contract <see cref="NoteImageTokenExtractor"/> already
    /// documents - while cutting a plain-text run mid-way once the weighted budget runs out. A cut
    /// can therefore never land inside a token, unlike the old raw-character-length cut this
    /// replaces.</summary>
    private static string TruncateToEffectiveLength(string body, int maxCharacters)
    {
        var effectiveLength = 0;
        var realIndex = 0;

        foreach (var token in NoteImageTokenExtractor.Tokenize(body))
        {
            if (token.AttachmentId is not null)
            {
                if (effectiveLength + ImageTokenCharacterWeight > maxCharacters)
                {
                    break;
                }

                effectiveLength += ImageTokenCharacterWeight;
                realIndex += token.Text.Length;
                continue;
            }

            var remainingBudget = maxCharacters - effectiveLength;
            if (token.Text.Length <= remainingBudget)
            {
                effectiveLength += token.Text.Length;
                realIndex += token.Text.Length;
                continue;
            }

            realIndex += remainingBudget;
            break;
        }

        return body[..realIndex];
    }

    private static int CountNewlines(string body)
    {
        var count = 0;
        foreach (var c in body)
        {
            if (c == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
