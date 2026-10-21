using System;
using System.Collections.Generic;
using System.Text;

namespace StockAnalyzer.Avalonia.Views.Notes;

/// <summary>
/// One piece of the Inlines <see cref="NoteCardView"/> builds for a Note body, in the coordinates of the text layout that
/// <c>SelectableTextBlock.SelectionStart/SelectionEnd</c> use: a <see cref="Avalonia.Controls.Documents.Run"/> occupies its text length,
/// an <see cref="Avalonia.Controls.Documents.InlineUIContainer"/> (hashtag / URL / inline image button) occupies exactly ONE position
/// (measured by NoteCardView_SelectionProbeTests Q3) - while <c>SelectableTextBlock.SelectedText</c> counts it as zero.
/// </summary>
/// <param name="Start">First layout position of the piece.</param>
/// <param name="Length">Layout positions the piece occupies (text length for a Run, 1 for a container).</param>
/// <param name="DrawnText">What the piece draws: the Run text, the hashtag / URL display text, or "" for an image container.</param>
/// <param name="IsContainer">True for an embedded control; its drawn text is selected as a whole or not at all.</param>
public readonly record struct BodyLayoutPiece(int Start, int Length, string DrawnText, bool IsContainer);

/// <summary>
/// Turns a selection range in layout coordinates into the text that is DRAWN there, including the text of hashtag / URL buttons that
/// <c>SelectedText</c> drops (decision row F-Map, sa_ai_context_NotesTextSelection.md section 4.1). Pure: no Avalonia types.
/// </summary>
public static class SelectionOffsetMapper
{
    /// <summary>Total layout positions covered by <paramref name="pieces"/>.</summary>
    public static int TotalLength(IReadOnlyList<BodyLayoutPiece> pieces)
    {
        var total = 0;
        foreach (var piece in pieces)
        {
            total = Math.Max(total, piece.Start + piece.Length);
        }

        return total;
    }

    /// <summary>
    /// The drawn text between <paramref name="selectionStart"/> and <paramref name="selectionEnd"/> (either order; positions outside
    /// the pieces are clamped away). A Run contributes the overlapping substring; a container contributes its whole drawn text as soon
    /// as the range overlaps its single position. An empty range (start == end) gives "".
    /// </summary>
    public static string Build(IReadOnlyList<BodyLayoutPiece> pieces, int selectionStart, int selectionEnd)
    {
        var low = Math.Min(selectionStart, selectionEnd);
        var high = Math.Max(selectionStart, selectionEnd);
        if (low == high)
        {
            return string.Empty;
        }

        var result = new StringBuilder();
        foreach (var piece in pieces)
        {
            var overlapStart = Math.Max(low, piece.Start);
            var overlapEnd = Math.Min(high, piece.Start + piece.Length);
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            if (piece.IsContainer)
            {
                result.Append(piece.DrawnText);
            }
            else
            {
                result.Append(piece.DrawnText, overlapStart - piece.Start, overlapEnd - overlapStart);
            }
        }

        return result.ToString();
    }
}
