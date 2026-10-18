using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.RCI)]
public class CoreRciIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 9;
    public override string Name => $"RCI ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
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
        if (Period < 2 || n < Period)
        {
            for (int i = 0; i < n; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        var priceSeries = PriceDataHelper.ExtractNonNullablePriceSeries(candles, PriceSource);
        decimal denom = (decimal)Period * ((long)Period * Period - 1L);

        for (int i = Period - 1; i < n; i++)
        {
            int start = i - Period + 1;

            // Check if all prices in the window are identical
            bool allEqual = true;
            decimal firstPrice = priceSeries[start];
            for (int j = 1; j < Period; j++)
            {
                if (priceSeries[start + j] != firstPrice)
                {
                    allEqual = false;
                    break;
                }
            }

            if (allEqual)
            {
                _values.Add(0m);
                continue;
            }

            // Zero-allocation mid-rank counting in O(P) per bar
            decimal dSum = 0m;
            for (int j = 0; j < Period; j++)
            {
                decimal v = priceSeries[start + j];
                int less = 0;
                int equal = 0;

                for (int k = 0; k < Period; k++)
                {
                    decimal pk = priceSeries[start + k];
                    if (pk < v)
                    {
                        less++;
                    }
                    else if (pk == v)
                    {
                        equal++;
                    }
                }

                // Mid-rank for ties: 1 + less + (equal - 1) * 0.5
                decimal priceRank = 1m + less + (equal - 1) * 0.5m;
                decimal timeRank = j + 1m;
                decimal diff = timeRank - priceRank;
                dSum += diff * diff;
            }

            decimal rci = (1m - (6m * dSum) / denom) * 100m;
            _values.Add(Math.Clamp(rci, -100m, 100m));
        }

        return IndicatorResult.Success(_values);
    }
}
