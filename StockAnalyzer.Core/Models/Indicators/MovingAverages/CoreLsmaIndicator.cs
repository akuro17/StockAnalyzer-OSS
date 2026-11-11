using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.LSMA)]
public class CoreLsmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 25;

    public override string Name => $"LSMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);
        if (candles.Count < Period)
        {
            _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        LinearRegressionEndpoint.AppendEndpoints(priceSeries, Period, _values);

        return IndicatorResult.Success(_values);
    }
}
