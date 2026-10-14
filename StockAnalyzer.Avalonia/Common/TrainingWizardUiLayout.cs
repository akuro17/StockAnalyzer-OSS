namespace StockAnalyzer.Avalonia.Common;

/// <summary>Layout tokens of the Training Wizard's ticker-list tree, in device independent pixels: the list shows this many rows before it scrolls.</summary>
public static class TrainingWizardUiLayout
{
    public const double SourceRowHeight = 28, TreeIconSize = 12;
    public const int SourceListVisibleRows = 10;
    public const double SourceListMaxHeight = SourceListVisibleRows * SourceRowHeight;

    /// <summary>The footer is 64 px (the sub-window standard) and grows only when its content needs more; the run id and the two deploy buttons wrap inside this width so the reason and the state labels keep room.</summary>
    public const double FooterHeight = 64, FooterActionsMaximumWidth = 380, FooterActionsSpacing = 12;
}
