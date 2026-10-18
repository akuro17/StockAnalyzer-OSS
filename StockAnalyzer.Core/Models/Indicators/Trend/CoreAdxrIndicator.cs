using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.ADXR)]
public class CoreAdxrIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public int AdxrPeriod { get; set; } = 14;

    public override string Name => $"ADXR ({Period}, {AdxrPeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAdxrParameter p)
        {
            Period = p.Period;
            AdxrPeriod = p.AdxrPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        int n = candles.Count;
        int seedIndex = 2 * Period - 2;
        int minRequired = seedIndex + AdxrPeriod + 1;

        if (Period <= 0 || AdxrPeriod <= 0 || n < Period)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        var (_, _, dxList) = IndicatorCalculationHelper.CalculateWilderDmiSeries(candles, Period);

        var adxList = new List<decimal?>(n);
        decimal prevAdx = 0m;

        for (int i = 0; i < n; i++)
        {
            if (i < seedIndex)
            {
                adxList.Add(null);
            }
            else if (i == seedIndex)
            {
                decimal sumDx = 0m;
                for (int k = Period - 1; k <= seedIndex; k++)
                {
                    sumDx += dxList[k] ?? 0m;
                }
                prevAdx = sumDx / Period;
                adxList.Add(prevAdx);
            }
            else
            {
                decimal dx = dxList[i] ?? 0m;
                decimal adx = (prevAdx * (Period - 1) + dx) / Period;
                prevAdx = adx;
                adxList.Add(adx);
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (i < seedIndex + AdxrPeriod)
            {
                _values.Add(null);
                continue;
            }

            var currentAdx = adxList[i];
            var pastAdx = adxList[i - AdxrPeriod];

            if (currentAdx.HasValue && pastAdx.HasValue)
            {
                _values.Add((currentAdx.Value + pastAdx.Value) / 2m);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
