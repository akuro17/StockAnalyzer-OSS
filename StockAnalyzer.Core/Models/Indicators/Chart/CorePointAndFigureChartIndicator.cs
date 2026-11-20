using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>Point and Figure on a time-series chart: the state of the Point and Figure chart (<see cref="PointAndFigureBarEngine"/>) after every time bar, the column in progress included.</summary>
[StockAnalyzerIndicator(IndicatorType.PointAndFigure)]
public sealed class CorePointAndFigureChartIndicator : CoreBlockChartOverlayIndicatorBase
{
    private CorePointAndFigureChartParameter _parameter = new();

    public CorePointAndFigureChartIndicator()
    {
        UpdateValues = new List<decimal?>();
    }

    public override string Name => _parameter.GetDisplayName("Point and Figure");

    /// <summary>The price of the next box in the direction of the column.</summary>
    public IReadOnlyList<decimal?> Update => UpdateValues!;

    protected override BlockChartSizingSpec? Sizing => _parameter.ToSizingSpec();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePointAndFigureChartParameter p)
        {
            p.Validate();
            _parameter = (CorePointAndFigureChartParameter)p.Clone();
        }
    }

    protected override IBlockChartBarEngine CreateEngine(decimal size) => new PointAndFigureBarEngine(size, _parameter.ReversalBoxes, _parameter.RoundingMode);
}
