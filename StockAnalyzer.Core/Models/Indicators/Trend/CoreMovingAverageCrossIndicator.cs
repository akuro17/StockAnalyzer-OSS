using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.MovingAverageCross)]
public class CoreMovingAverageCrossIndicator : CoreIndicatorBase
{
    public int ShortPeriod { get; set; } = 10;
    public int LongPeriod { get; set; } = 20;

    public override string Name => $"MA Cross ({ShortPeriod}, {LongPeriod})";
    public override bool IsOverlay => true;

    public List<decimal?> ShortMA { get; } = new();
    public List<decimal?> LongMA { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BullishSignals { get; } = new();

    [StockAnalyzer.Core.Models.Attributes.IndicatorResultIgnore]
    public List<decimal?> BearishSignals { get; } = new();

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreMovingAverageCrossParameter p)
        {
            ShortPeriod = p.ShortPeriod;
            LongPeriod = p.LongPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        ShortMA.Clear();
        LongMA.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        if (candles.Count < LongPeriod)
        {
            var nulls = Enumerable.Repeat<decimal?>(null, candles.Count);
            ShortMA.AddRange(nulls);
            LongMA.AddRange(nulls);
            BullishSignals.AddRange(nulls);
            BearishSignals.AddRange(nulls);
            return CreateAutomaticResult();
        }

        // Calculate SMAs
        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        CalculateSma(priceSeries, ShortPeriod, ShortMA);
        CalculateSma(priceSeries, LongPeriod, LongMA);

        // Signal Detection
        decimal? prevShort = null;
        decimal? prevLong = null;

        for (int i = 0; i < candles.Count; i++)
        {
            decimal? currShort = ShortMA[i];
            decimal? currLong = LongMA[i];

            if (prevShort.HasValue && prevLong.HasValue && currShort.HasValue && currLong.HasValue)
            {
                decimal prevDiff = prevShort.Value - prevLong.Value;
                decimal currDiff = currShort.Value - currLong.Value;

                if (prevDiff <= 0 && currDiff > 0) BullishSignals.Add(1m);
                else BullishSignals.Add(null);

                if (prevDiff >= 0 && currDiff < 0) BearishSignals.Add(1m);
                else BearishSignals.Add(null);
            }
            else
            {
                BullishSignals.Add(null);
                BearishSignals.Add(null);
            }

            prevShort = currShort;
            prevLong = currLong;
        }

        return CreateAutomaticResult();
    }

    private static void CalculateSma(IReadOnlyList<decimal?> priceSeries, int period, List<decimal?> target)
    {
        decimal sum = 0;
        for (int i = 0; i < priceSeries.Count; i++)
        {
            sum += priceSeries[i] ?? 0m;
            if (i >= period)
            {
                sum -= priceSeries[i - period] ?? 0m;
            }

            if (i >= period - 1)
            {
                target.Add(sum / period);
            }
            else
            {
                target.Add(null);
            }
        }
    }

}
