using System;
using System.ComponentModel;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Settings of the Renko overlay (the inputs of the Renko chart type; no drawing options of that chart, no Breakout Patterns).
/// The class name is persisted (WorkspacePolymorphicResolver uses type.Name). NEVER rename after release.
/// </summary>
public class CoreRenkoChartParameter : CoreBlockChartSizingParameterBase
{
    private int _reversalBricks = ChartConstants.DefaultRenkoReversalBricks;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinReversal, IndicatorDefaultConstants.BlockChartOverlayMaxReversal)]
    [DisplayName("Reversal Bricks")]
    [Description("Bricks beyond the edge of the last brick (its bottom in an up trend, its top in a down trend) that reverse the trend; inclusive.")]
    [Category("Reversal")]
    public int ReversalBricks
    {
        get => _reversalBricks;
        set => SetProperty(ref _reversalBricks, value);
    }

    public override string GetDisplayName(string type) => string.Create(CultureInfo.InvariantCulture, $"{type} ({DescribeSizing()}, R{ReversalBricks})");

    public override void Validate()
    {
        ValidateSizing();
        ThrowIfOutOfRange(ReversalBricks, IndicatorDefaultConstants.BlockChartOverlayMinReversal, IndicatorDefaultConstants.BlockChartOverlayMaxReversal, nameof(ReversalBricks));
    }
}
