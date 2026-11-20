using System;
using System.ComponentModel;
using System.Globalization;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Settings of the Three Line Break overlay (the inputs of the Three Line Break chart type: the number of lines and the minimum move; no size, no drawing options, no Breakout Patterns).
/// The class name is persisted (WorkspacePolymorphicResolver uses type.Name). NEVER rename after release.
/// </summary>
public class CoreThreeLineBreakChartParameter : CoreIndicatorParameterBase
{
    private int _numberOfLines = ChartConstants.DefaultThreeLineBreakLineCount;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinLineCount, IndicatorDefaultConstants.BlockChartOverlayMaxLineCount)]
    [DisplayName("Number of Lines")]
    [Description("Number of the last lines whose extreme must be broken for a reversal.")]
    [Category("Reversal")]
    public int NumberOfLines
    {
        get => _numberOfLines;
        set => SetProperty(ref _numberOfLines, value);
    }

    private decimal _minimumMove = ChartConstants.DefaultThreeLineBreakMinMove;

    [CoreParameterRange(IndicatorDefaultConstants.BlockChartOverlayMinMinimumMove, IndicatorDefaultConstants.BlockChartOverlayMaxSize)]
    [DisplayName("Minimum Move")]
    [Description("Closes that are less than this far from the close of the last line are ignored; 0 turns the filter off.")]
    [Category("Reversal")]
    public decimal MinimumMove
    {
        get => _minimumMove;
        set => SetProperty(ref _minimumMove, value);
    }

    /// <summary>"Three Line Break (3)", and with a small-move filter "Three Line Break (3, min 0.5)", so that several settings of the same chart stay distinguishable.</summary>
    public override string GetDisplayName(string type) => MinimumMove > 0m
        ? string.Create(CultureInfo.InvariantCulture, $"{type} ({NumberOfLines}, min {MinimumMove:0.####})")
        : string.Create(CultureInfo.InvariantCulture, $"{type} ({NumberOfLines})");

    public override void Validate()
    {
        ThrowIfOutOfRange(NumberOfLines, IndicatorDefaultConstants.BlockChartOverlayMinLineCount, IndicatorDefaultConstants.BlockChartOverlayMaxLineCount, nameof(NumberOfLines));
        ThrowIfOutOfRange(MinimumMove, IndicatorDefaultConstants.BlockChartOverlayMinMinimumMove, IndicatorDefaultConstants.BlockChartOverlayMaxSize, nameof(MinimumMove));
    }
}
