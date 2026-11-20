using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Chart;

/// <summary>Kagi on a time-series chart: the state of the Kagi chart (<see cref="KagiBarEngine"/>) after every time bar. Close and Reversal only: there is no Update series (no property, no result entry).</summary>
[StockAnalyzerIndicator(IndicatorType.Kagi)]
public sealed class CoreKagiChartIndicator : CoreBlockChartOverlayIndicatorBase
{
    private CoreKagiChartParameter _parameter = new();

    public override string Name => _parameter.GetDisplayName("Kagi");

    protected override BlockChartSizingSpec? Sizing => _parameter.ToSizingSpec();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreKagiChartParameter p)
        {
            p.Validate();
            _parameter = (CoreKagiChartParameter)p.Clone();
        }
    }

    protected override IBlockChartBarEngine CreateEngine(decimal size) => new KagiBarEngine(size);
}
