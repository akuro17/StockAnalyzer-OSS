namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>
/// What a Note card needs from the timeline to act on a text selection of its body (right-click "Apply to Filter" / "Apply to
/// Search"). Implemented by <see cref="NoteTimelineViewModel"/> and handed to each <see cref="NoteTimelineItemViewModel"/> as an
/// optional constructor argument, so an item built without it (every existing test) keeps working unchanged.
/// </summary>
public interface INoteSelectionTarget
{
    /// <summary>Freezes <paramref name="rawSelectedText"/> into a snapshot: which actions are available and what they would apply.</summary>
    NoteSelectionSnapshot Evaluate(string? rawSelectedText);

    /// <summary>Applies <see cref="NoteSelectionSnapshot.FilterSuggestion"/> through the same path a suggestion picked from the filter
    /// box takes. No-op when the snapshot cannot apply to the filter.</summary>
    void ApplyToFilter(NoteSelectionSnapshot snapshot);

    /// <summary>Replaces the search text with <see cref="NoteSelectionSnapshot.ApplyText"/>. No-op when the snapshot cannot apply to search.</summary>
    void ApplyToSearch(NoteSelectionSnapshot snapshot);
}
