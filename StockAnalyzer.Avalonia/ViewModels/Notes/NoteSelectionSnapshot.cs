using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Notes;

/// <summary>Why the "Apply to ..." actions cannot use the current text selection. <see cref="None"/> means the selection is usable
/// for Apply to Search (and, when <see cref="NoteSelectionSnapshot.FilterSuggestion"/> is set, for Apply to Filter).</summary>
public enum NoteSelectionBlockReason
{
    None,

    /// <summary>No selection, only whitespace, or only object-replacement characters.</summary>
    EmptySelection,

    /// <summary>The selection contains a line break (the search box is single-line and the search is a raw substring match).</summary>
    MultiLine,

    /// <summary>The item has no <see cref="INoteSelectionTarget"/> (design time / tests), so an Apply would do nothing.</summary>
    NoTarget,
}

/// <summary>
/// The text selection of a Note body frozen at the moment its context menu opens (sa_ai_context_NotesTextSelection.md section 5.5):
/// the menu items are enabled from it and their clicks act on it, never on a fresh read, because the selection can be cleared by the
/// time a click arrives. A plain value; no Avalonia types (Primitive Barrier).
/// </summary>
/// <param name="RawText">The selection exactly as selected (untrimmed). What Copy writes.</param>
/// <param name="ApplyText">The trimmed, single-line text the Apply actions use; empty when <paramref name="BlockReason"/> is not None.</param>
/// <param name="FilterSuggestion">The filter candidate <see cref="ApplyText"/> resolves to; null when nothing matches.</param>
/// <param name="BlockReason">Why <see cref="ApplyText"/> is empty/unusable.</param>
public sealed record NoteSelectionSnapshot(
    string RawText,
    string ApplyText,
    NoteScopeSuggestion? FilterSuggestion,
    NoteSelectionBlockReason BlockReason)
{
    public static NoteSelectionSnapshot Empty { get; } = new(string.Empty, string.Empty, null, NoteSelectionBlockReason.EmptySelection);

    public bool CanCopy => RawText.Length > 0;

    public bool CanApplyToSearch => BlockReason == NoteSelectionBlockReason.None && ApplyText.Length > 0;

    public bool CanApplyToFilter => CanApplyToSearch && FilterSuggestion is not null;

    /// <summary>Localized reason Copy is disabled; null when enabled (SA_UI_INTERACTION section 29: a disabled-control tooltip is a
    /// dynamic property that is null while the control is enabled).</summary>
    public string? CopyDisabledReason => CanCopy ? null : LocalizationManager.Instance["Note_Selection_Reason_Empty"];

    /// <summary>Localized reason Apply to Search is disabled; null when enabled.</summary>
    public string? SearchDisabledReason => CanApplyToSearch ? null : BlockReasonText(BlockReason);

    /// <summary>Localized reason Apply to Filter is disabled; null when enabled.</summary>
    public string? FilterDisabledReason =>
        CanApplyToFilter ? null
        : BlockReason != NoteSelectionBlockReason.None ? BlockReasonText(BlockReason)
        : LocalizationManager.Instance["Note_Selection_Reason_NoMatch"];

    private static string BlockReasonText(NoteSelectionBlockReason reason) => reason switch
    {
        NoteSelectionBlockReason.EmptySelection => LocalizationManager.Instance["Note_Selection_Reason_Empty"],
        NoteSelectionBlockReason.MultiLine => LocalizationManager.Instance["Note_Selection_Reason_MultiLine"],
        _ => LocalizationManager.Instance["Note_Selection_Reason_Unavailable"],
    };
}
