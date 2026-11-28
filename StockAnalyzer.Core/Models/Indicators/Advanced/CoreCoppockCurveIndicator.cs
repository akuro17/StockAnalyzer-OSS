using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

[StockAnalyzerIndicator(IndicatorType.CoppockCurve)]
public class CoreCoppockCurveIndicator : CoreIndicatorBase
{
    public int LongRocPeriod { get; set; } = 14;
    public int ShortRocPeriod { get; set; } = 11;
    public int WmaPeriod { get; set; } = 10;

    public override string Name => $"Coppock Curve ({LongRocPeriod},{ShortRocPeriod},{WmaPeriod})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreCoppockCurveParameter p)
        {
            LongRocPeriod = p.LongRocPeriod;
            ShortRocPeriod = p.ShortRocPeriod;
            WmaPeriod = p.WmaPeriod;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (LongRocPeriod <= 0 || ShortRocPeriod <= 0 || WmaPeriod <= 0)
        {
            for (int i = 0; i < series.Count; i++) _values.Add(null);
            return IndicatorResult.Success(_values);
        }

        // Coppock = WMA[WmaPeriod] of (ROC[LongRocPeriod] + ROC[ShortRocPeriod])
        int maxRoc = Math.Max(LongRocPeriod, ShortRocPeriod);
        var rocSum = new List<decimal?>(series.Count);

        for (int i = 0; i < series.Count; i++)
        {
            if (i < maxRoc
                || !series[i - LongRocPeriod].HasValue
                || !series[i - ShortRocPeriod].HasValue
                || !series[i].HasValue)
            {
                rocSum.Add(null);
                continue;
            }

            decimal longBase = series[i - LongRocPeriod]!.Value;
            decimal shortBase = series[i - ShortRocPeriod]!.Value;
            decimal current = series[i]!.Value;
            decimal rocLong = longBase == 0m ? 0m : (current - longBase) / longBase * 100m;
            decimal rocShort = shortBase == 0m ? 0m : (current - shortBase) / shortBase * 100m;
            rocSum.Add(rocLong + rocShort);
        }

        for (int i = 0; i < series.Count; i++)
        {
            if (i < maxRoc + WmaPeriod - 1)
            {
                _values.Add(null);
                continue;
            }

            decimal numerator = 0m;
            decimal denominator = 0m;
            bool hasNull = false;
            for (int j = 0; j < WmaPeriod; j++)
            {
                decimal? val = rocSum[i - j];
                if (!val.HasValue)
                {
                    hasNull = true;
                    break;
                }

                decimal weight = WmaPeriod - j;
                numerator += val.Value * weight;
                denominator += weight;
            }

            _values.Add(hasNull ? null : numerator / denominator);
        }

        return IndicatorResult.Success(_values);
    }
}
