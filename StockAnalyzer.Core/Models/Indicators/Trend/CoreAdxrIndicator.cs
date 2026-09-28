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

        if (candles == null) return IndicatorResult.Success(_values);
        var adxValues = CalculateAdx(candles, Period);

        for (int i = 0; i < adxValues.Count; i++)
        {
            if (i < AdxrPeriod)
            {
                _values.Add(null);
                continue;
            }

            var currentAdx = adxValues[i];
            var prevAdx = adxValues[i - AdxrPeriod];

            if (currentAdx.HasValue && prevAdx.HasValue)
            {
                _values.Add((currentAdx.Value + prevAdx.Value) / 2);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }

    private static List<decimal?> CalculateAdx(IReadOnlyList<CoreCandleData> candles, int period)
    {
        if (candles.Count < 2) return Enumerable.Repeat<decimal?>(null, candles.Count).ToList();

        var trList = new List<decimal>();
        var plusDmList = new List<decimal>();
        var minusDmList = new List<decimal>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i == 0)
            {
                trList.Add(candles[i].High - candles[i].Low);
                plusDmList.Add(0);
                minusDmList.Add(0);
                continue;
            }

            var high = candles[i].High;
            var low = candles[i].Low;
            var prevClose = candles[i - 1].Close;
            var prevHigh = candles[i - 1].High;
            var prevLow = candles[i - 1].Low;

            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trList.Add(tr);

            var upMove = high - prevHigh;
            var downMove = prevLow - low;

            plusDmList.Add(upMove > downMove && upMove > 0 ? upMove : 0);
            minusDmList.Add(downMove > upMove && downMove > 0 ? downMove : 0);
        }

        var atrSma = Sma(trList, period);
        var plusDmSma = Sma(plusDmList, period);
        var minusDmSma = Sma(minusDmList, period);

        var plusDi = new List<decimal?>();
        var minusDi = new List<decimal?>();
        for(int i = 0; i < candles.Count; i++)
        {
            var atr = atrSma[i];
            if (atr.HasValue && atr != 0 && plusDmSma[i].HasValue && minusDmSma[i].HasValue)
            {
                plusDi.Add(plusDmSma[i]!.Value / atr!.Value * 100);
                minusDi.Add(minusDmSma[i]!.Value / atr!.Value * 100);
            }
            else
            {
                plusDi.Add(null);
                minusDi.Add(null);
            }
        }

        var dx = new List<decimal?>();
        for (int i = 0; i < plusDi.Count; i++)
        {
            var pdi = plusDi[i];
            var mdi = minusDi[i];
            if (pdi.HasValue && mdi.HasValue && (pdi + mdi) != 0)
            {
                dx.Add(Math.Abs(pdi.Value - mdi.Value) / (pdi.Value + mdi.Value) * 100);
            }
            else
            {
                dx.Add(null);
            }
        }

        return SmaForNullable(dx, period);
    }

    private static List<decimal?> Sma(IReadOnlyList<decimal> data, int period)
    {
        var results = new List<decimal?>();
        for (int i = 0; i < data.Count; i++)
        {
            if (i < period - 1) results.Add(null);
            else results.Add(data.Skip(i - period + 1).Take(period).Average());
        }
        return results;
    }

    private static List<decimal?> SmaForNullable(IReadOnlyList<decimal?> data, int period)
    {
        var results = new List<decimal?>();
        for (int i = 0; i < data.Count; i++)
        {
            var window = data.Skip(i - period + 1).Take(period).ToList();
            if (i < period - 1 || window.Any(v => !v.HasValue))
            {
                results.Add(null);
            }
            else
            {
                results.Add(window.Average(v => v!.Value));
            }
        }
        return results;
    }
}
