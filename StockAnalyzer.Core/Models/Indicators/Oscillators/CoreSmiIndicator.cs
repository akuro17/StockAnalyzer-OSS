using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.SMI)]
public class CoreSmiIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public int Smooth1 { get; set; } = 5;
    public int Smooth2 { get; set; } = 3;

    public override string Name => $"SMI ({Period}, {Smooth1}, {Smooth2})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmiParameter p)
        {
            Period = p.Period;
            Smooth1 = p.Smooth1;
            Smooth2 = p.Smooth2;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {

        if (candles == null) return IndicatorResult.Success(_values);
        // A rough estimate for the minimum number of candles required.
        int requiredCandles = Period + Smooth1 + Smooth2;
        if (candles.Count < requiredCandles)
        {
             _values.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
            return IndicatorResult.Success(_values);
        }

        // 1. Calculate Highest High (HH) and Lowest Low (LL) over the period
        var hh = new List<decimal?>();
        var ll = new List<decimal?>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                hh.Add(null);
                ll.Add(null);
            }
            else
            {
                decimal maxHigh = 0;
                decimal minLow = decimal.MaxValue;
                for (int j = 0; j < Period; j++)
                {
                    var c = candles[i - j];
                    if (c.High > maxHigh) maxHigh = c.High;
                    if (c.Low < minLow) minLow = c.Low;
                }
                hh.Add(maxHigh);
                ll.Add(minLow);
            }
        }

        // 2. Calculate Intermediate values
        var closeMinusD = new List<decimal?>();
        var halfHl = new List<decimal?>();

        for (int i = 0; i < candles.Count; i++)
        {
            if (hh[i].HasValue && ll[i].HasValue)
            {
                var d = (hh[i]!.Value + ll[i]!.Value) / 2;
                closeMinusD.Add(candles[i].Close - d);
                halfHl.Add((hh[i]!.Value - ll[i]!.Value) / 2);
            }
            else
            {
                closeMinusD.Add(null);
                halfHl.Add(null);
            }
        }

        // 3. Double-smooth the results
        var ds = Ema(Ema(closeMinusD, Smooth1), Smooth2);
        var dhl = Ema(Ema(halfHl, Smooth1), Smooth2);

        // 4. Calculate final SMI
        for (int i = 0; i < candles.Count; i++)
        {
            if (dhl[i].HasValue && dhl[i] != 0 && ds[i].HasValue)
            {
                _values.Add(100 * (ds[i]!.Value / dhl[i]!.Value));
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }

    private static List<decimal?> Ema(IReadOnlyList<decimal?> data, int period)
    {
        var emaValues = new List<decimal?>(new decimal?[data.Count]);
        if (data.Count < period || period <= 0) return Enumerable.Repeat<decimal?>(null, data.Count).ToList();

        decimal multiplier = 2.0m / (period + 1);
        decimal? prevEma = null;

        for (int i = 0; i < data.Count; i++)
        {
            if (prevEma == null)
            {
                // Try to seed the EMA with an SMA over the first valid window
                if (i >= period - 1)
                {
                    var window = data.Skip(i - period + 1).Take(period).ToList();
                    if (window.All(v => v.HasValue))
                    {
                        prevEma = window.Average(v => v!.Value);
                    }
                }
            }
            else
            {
                // Update EMA if we have a current value, otherwise carry over the previous EMA
                if (data[i].HasValue)
                {
                    prevEma = (data[i]!.Value - prevEma!.Value) * multiplier + prevEma!.Value;
                }
            }
            emaValues[i] = prevEma;
        }
        return emaValues;
    }
}
