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

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        ShortMA.Clear();
        LongMA.Clear();
        BullishSignals.Clear();
        BearishSignals.Clear();

        if (series == null || series.Count == 0)
        {
            return CreateAutomaticResult();
        }

        if (ShortPeriod <= 0 || LongPeriod <= 0 || series.Count < LongPeriod)
        {
            var nulls = Enumerable.Repeat<decimal?>(null, series.Count);
            ShortMA.AddRange(nulls);
            LongMA.AddRange(nulls);
            BullishSignals.AddRange(nulls);
            BearishSignals.AddRange(nulls);
            return CreateAutomaticResult();
        }

        // Calculate SMAs with strict null propagation
        ShortMA.AddRange(IndicatorCalculationHelper.CalculateSmaWithNulls(series, ShortPeriod));
        LongMA.AddRange(IndicatorCalculationHelper.CalculateSmaWithNulls(series, LongPeriod));

        // Signal Detection
        decimal? prevShort = null;
        decimal? prevLong = null;

        for (int i = 0; i < series.Count; i++)
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

}
