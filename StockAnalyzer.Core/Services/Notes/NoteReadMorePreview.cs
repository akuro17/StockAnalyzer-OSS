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
    /// <summary>True when <paramref name="body"/> exceeds either threshold and therefore needs a
    /// "Read more" toggle at all.</summary>
    public static bool RequiresCollapse(string body, int maxCharacters, int maxLines)
        => body.Length > maxCharacters || CountNewlines(body) > maxLines;

    /// <summary>The collapsed ("before Read more") text: <paramref name="body"/> unchanged when it
    /// doesn't require collapsing, otherwise capped first to <paramref name="maxLines"/> lines, then
    /// to <paramref name="maxCharacters"/> characters.</summary>
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

        return lineLimited.Length > maxCharacters
            ? lineLimited[..maxCharacters]
            : lineLimited;
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
