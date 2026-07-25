using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Trend;

[StockAnalyzerIndicator(IndicatorType.DEMA)]
public class CoreDemaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"DEMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        decimal mult = 2m / (Period + 1);

        decimal? ema1 = null, ema2 = null;
        var ema1List = new List<decimal?>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1) { ema1List.Add(null); _values.Add(null); continue; }

            if (ema1 == null)
            {
                decimal sum = 0;
                for (int j = 0; j < Period; j++) sum += candles[i - j].Close;
                ema1 = sum / Period;
            }
            else ema1 = (candles[i].Close - ema1.Value) * mult + ema1.Value;
            ema1List.Add(ema1);

            if (i < 2 * Period - 2) { _values.Add(null); continue; }

            if (ema2 == null)
            {
                decimal sum = 0;
                for (int j = 0; j < Period; j++) sum += ema1List[i - j]!.Value;
                ema2 = sum / Period;
            }
            else ema2 = (ema1.Value - ema2.Value) * mult + ema2.Value;

            _values.Add(2 * ema1 - ema2);
        }

        return IndicatorResult.Success(_values);
    }
}
