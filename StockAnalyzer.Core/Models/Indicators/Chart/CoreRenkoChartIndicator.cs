using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>Renko on a time-series chart: the state of the Renko chart (<see cref="RenkoBarEngine"/>) after every time bar. Reversal and Update as defined by <c>RenkoLevels</c>.</summary>
[StockAnalyzerIndicator(IndicatorType.Renko)]
public sealed class CoreRenkoChartIndicator : CoreBlockChartOverlayIndicatorBase
{
    private CoreRenkoChartParameter _parameter = new();

    public CoreRenkoChartIndicator()
    {
        UpdateValues = new List<decimal?>();
    }

    public override string Name => _parameter.GetDisplayName("Renko");

    /// <summary>The price of the next brick in the direction of the trend.</summary>
    public IReadOnlyList<decimal?> Update => UpdateValues!;

    protected override BlockChartSizingSpec? Sizing => _parameter.ToSizingSpec();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreRenkoChartParameter p)
        {
            p.Validate();
            _parameter = (CoreRenkoChartParameter)p.Clone();
        }
    }

    protected override IBlockChartBarEngine CreateEngine(decimal size) => new RenkoBarEngine(size, _parameter.ReversalBricks, _parameter.RoundingMode);
}
