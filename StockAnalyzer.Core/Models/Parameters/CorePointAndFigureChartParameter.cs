using System;
using System.ComponentModel;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Settings of the Point and Figure overlay (the inputs of the Point and Figure chart type; no drawing options of that chart, no Breakout Patterns).
/// The class name is persisted (WorkspacePolymorphicResolver uses type.Name). NEVER rename after release.
/// </summary>
public class CorePointAndFigureChartParameter : CoreBlockChartSizingParameterBase
{
    private int _reversalBoxes = ChartConstants.DefaultReversalAmount;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinReversal, IndicatorDefaultConstants.BlockChartOverlayMaxReversal)]
    [DisplayName("Reversal Boxes")]
    [Description("Boxes back from the end of the current column that reverse it into the opposite column; inclusive.")]
    [Category("Reversal")]
    public int ReversalBoxes
    {
        get => _reversalBoxes;
        set => SetProperty(ref _reversalBoxes, value);
    }

    public override string GetDisplayName(string type) => string.Create(CultureInfo.InvariantCulture, $"{type} ({DescribeSizing()}, R{ReversalBoxes})");

    public override void Validate()
    {
        ValidateSizing();
        ThrowIfOutOfRange(ReversalBoxes, IndicatorDefaultConstants.BlockChartOverlayMinReversal, IndicatorDefaultConstants.BlockChartOverlayMaxReversal, nameof(ReversalBoxes));
    }
}
