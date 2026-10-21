using System;
using System.Collections.Generic;

namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>
/// Pure rules that turn a Note body's text selection into a <see cref="NoteSelectionSnapshot"/>
/// (sa_ai_context_NotesTextSelection.md section 5.2). No Avalonia types, no I/O, no state.
/// </summary>
public static class NoteSelectionTextRules
{
    /// <summary>U+FFFC: what a text engine leaves for an embedded control (hashtag/URL/image button) inside a selection.</summary>
    private const char ObjectReplacementChar = '\uFFFC';

    private static readonly char[] LineBreakChars = { '\r', '\n', '\u2028', '\u2029', '\u0085' };

    /// <summary>Removes object-replacement characters and trims both ends (<see cref="string.Trim()"/>, i.e. char.IsWhiteSpace).
    /// Null or empty input gives an empty string.</summary>
    public static string NormalizeForApply(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return raw.Replace(ObjectReplacementChar.ToString(), string.Empty, StringComparison.Ordinal).Trim();
    }

    public static bool ContainsLineBreak(string text) => text.IndexOfAny(LineBreakChars) >= 0;

    /// <summary>Resolution priority when several suggestions share a display name (a ticker code that is also a watchlist name, ...):
    /// a ticker is the strongest identity and matches the Enter-key rule of the filter box; the scope kinds differ only by name.
    /// Single source of truth for the order.</summary>
    public static int KindPriority(NoteScopeSuggestionKind kind) => kind switch
    {
        NoteScopeSuggestionKind.Ticker => 0,
        NoteScopeSuggestionKind.Hashtag => 1,
        NoteScopeSuggestionKind.Tag => 2,
        NoteScopeSuggestionKind.Watchlist => 3,
        NoteScopeSuggestionKind.Portfolio => 4,
        NoteScopeSuggestionKind.Filter => 5,
        NoteScopeSuggestionKind.NoTicker => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"Unhandled {nameof(NoteScopeSuggestionKind)} value."),
    };

    /// <summary>The suggestion whose <see cref="NoteScopeSuggestion.DisplayName"/> equals <paramref name="normalizedText"/> ignoring
    /// case; the lowest <see cref="KindPriority"/> wins and ties keep list order. Null for empty/multi-line text or no match.</summary>
    public static NoteScopeSuggestion? ResolveFilterSuggestion(string normalizedText, IReadOnlyList<NoteScopeSuggestion> suggestions)
    {
        if (normalizedText.Length == 0 || ContainsLineBreak(normalizedText))
        {
            return null;
        }

        NoteScopeSuggestion? best = null;
        var bestPriority = int.MaxValue;
        foreach (var suggestion in suggestions)
        {
            if (!string.Equals(suggestion.DisplayName, normalizedText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var priority = KindPriority(suggestion.Kind);
            if (priority < bestPriority)
            {
                best = suggestion;
                bestPriority = priority;
            }
        }

        return best;
    }

    /// <summary>Builds the snapshot for <paramref name="raw"/>: Copy uses it untouched; the Apply actions need a non-empty, single-line,
    /// trimmed text (a multi-line selection is refused rather than altered, because the search is a raw substring match against the
    /// body and a whitespace-normalized needle would silently stop matching).</summary>
    public static NoteSelectionSnapshot Evaluate(string? raw, IReadOnlyList<NoteScopeSuggestion> suggestions)
    {
        var rawText = raw ?? string.Empty;
        var apply = NormalizeForApply(raw);
        if (apply.Length == 0)
        {
            return new NoteSelectionSnapshot(rawText, string.Empty, null, NoteSelectionBlockReason.EmptySelection);
        }

        if (ContainsLineBreak(apply))
        {
            return new NoteSelectionSnapshot(rawText, string.Empty, null, NoteSelectionBlockReason.MultiLine);
        }

        return new NoteSelectionSnapshot(rawText, apply, ResolveFilterSuggestion(apply, suggestions), NoteSelectionBlockReason.None);
    }

    /// <summary>Snapshot for a card that has no <see cref="INoteSelectionTarget"/>: Copy still works, every Apply is refused.</summary>
    public static NoteSelectionSnapshot EvaluateCopyOnly(string? raw) =>
        new(raw ?? string.Empty, string.Empty, null, NoteSelectionBlockReason.NoTarget);
}
