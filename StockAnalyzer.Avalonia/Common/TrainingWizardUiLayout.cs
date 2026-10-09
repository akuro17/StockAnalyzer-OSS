namespace StockAnalyzer.Avalonia.Common;

/// <summary>Layout tokens of the Training Wizard's ticker-list tree, in device independent pixels: the list shows this many rows before it scrolls.</summary>
public static class TrainingWizardUiLayout
{
    public const double SourceRowHeight = 28, TreeIconSize = 12;
    public const int SourceListVisibleRows = 10;
    public const double SourceListMaxHeight = SourceListVisibleRows * SourceRowHeight;
}
