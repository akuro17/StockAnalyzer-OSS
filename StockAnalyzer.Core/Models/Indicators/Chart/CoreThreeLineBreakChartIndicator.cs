using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>Three Line Break on a time-series chart: the state of the Three Line Break chart (<see cref="ThreeLineBreakBarEngine"/>) after every time bar. Close and Reversal only; it has no size.</summary>
[StockAnalyzerIndicator(IndicatorType.ThreeLineBreak)]
public sealed class CoreThreeLineBreakChartIndicator : CoreBlockChartOverlayIndicatorBase
{
    private CoreThreeLineBreakChartParameter _parameter = new();

    public override string Name => _parameter.GetDisplayName("Three Line Break");

    protected override BlockChartSizingSpec? Sizing => null;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreThreeLineBreakChartParameter p)
        {
            p.Validate();
            _parameter = (CoreThreeLineBreakChartParameter)p.Clone();
        }
    }

    protected override IBlockChartBarEngine CreateEngine(decimal size) => new ThreeLineBreakBarEngine(_parameter.NumberOfLines, _parameter.MinimumMove);
}
