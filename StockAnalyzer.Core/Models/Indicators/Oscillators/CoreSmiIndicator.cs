using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators;

[StockAnalyzerIndicator(IndicatorType.SMI)]
public class CoreSmiIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 14;
    public int Smooth1 { get; set; } = 5;
    public int Smooth2 { get; set; } = 3;
    public int SignalPeriod { get; set; } = 5;

    private readonly List<decimal?> _signalLine = new();
    public IReadOnlyList<decimal?> SignalLine => _signalLine;

    public override string Name => $"SMI ({Period}, {Smooth1}, {Smooth2})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmiParameter p)
        {
            Period = p.Period;
            Smooth1 = p.Smooth1;
            Smooth2 = p.Smooth2;
            SignalPeriod = p.SignalPeriod;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        _signalLine.Clear();

        if (candles == null || candles.Count == 0) return CreateAutomaticResult();

        int requiredCandles = Period + Smooth1 + Smooth2;
        if (candles.Count < requiredCandles)
        {
            for (int i = 0; i < candles.Count; i++)
            {
                _values.Add(null);
                _signalLine.Add(null);
            }
            return CreateAutomaticResult();
        }

        var dValues = new List<decimal?>(candles.Count);
        var hlValues = new List<decimal?>(candles.Count);

        for (int i = 0; i < candles.Count; i++)
        {
            if (i < Period - 1)
            {
                dValues.Add(null);
                hlValues.Add(null);
            }
            else
            {
                decimal maxHigh = decimal.MinValue;
                decimal minLow = decimal.MaxValue;
                for (int j = 0; j < Period; j++)
                {
                    var c = candles[i - j];
                    if (c.High > maxHigh) maxHigh = c.High;
                    if (c.Low < minLow) minLow = c.Low;
                }
                decimal d = (maxHigh + minLow) / 2m;
                dValues.Add(candles[i].Close - d);
                hlValues.Add(maxHigh - minLow);
            }
        }

        var ema1D = IndicatorCalculationHelper.CalculateEmaWithNulls(dValues, Smooth1);
        var ema2D = IndicatorCalculationHelper.CalculateEmaWithNulls(ema1D, Smooth2);
        var ema1Hl = IndicatorCalculationHelper.CalculateEmaWithNulls(hlValues, Smooth1);
        var ema2Hl = IndicatorCalculationHelper.CalculateEmaWithNulls(ema1Hl, Smooth2);

        for (int i = 0; i < candles.Count; i++)
        {
            if (ema2D[i].HasValue && ema2Hl[i].HasValue)
            {
                decimal halfHl = 0.5m * ema2Hl[i]!.Value;
                if (halfHl == 0m)
                {
                    _values.Add(0m);
                }
                else
                {
                    _values.Add(100m * (ema2D[i]!.Value / halfHl));
                }
            }
            else
            {
                _values.Add(null);
            }
        }

        var signal = IndicatorCalculationHelper.CalculateEmaWithNulls(_values, SignalPeriod);
        _signalLine.AddRange(signal);

        return CreateAutomaticResult();
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        _signalLine.Clear();

        if (series == null || series.Count == 0) return CreateAutomaticResult();

        int requiredCandles = Period + Smooth1 + Smooth2;
        if (series.Count < requiredCandles)
        {
            for (int i = 0; i < series.Count; i++)
            {
                _values.Add(null);
                _signalLine.Add(null);
            }
            return CreateAutomaticResult();
        }

        var dValues = new List<decimal?>(series.Count);
        var hlValues = new List<decimal?>(series.Count);

        for (int i = 0; i < series.Count; i++)
        {
            if (i < Period - 1)
            {
                dValues.Add(null);
                hlValues.Add(null);
            }
            else
            {
                decimal maxHigh = decimal.MinValue;
                decimal minLow = decimal.MaxValue;
                bool hasNull = false;

                for (int j = 0; j < Period; j++)
                {
                    var val = series[i - j];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    if (val.Value > maxHigh) maxHigh = val.Value;
                    if (val.Value < minLow) minLow = val.Value;
                }

                if (hasNull || !series[i].HasValue)
                {
                    dValues.Add(null);
                    hlValues.Add(null);
                }
                else
                {
                    decimal d = (maxHigh + minLow) / 2m;
                    dValues.Add(series[i]!.Value - d);
                    hlValues.Add(maxHigh - minLow);
                }
            }
        }

        var ema1D = IndicatorCalculationHelper.CalculateEmaWithNulls(dValues, Smooth1);
        var ema2D = IndicatorCalculationHelper.CalculateEmaWithNulls(ema1D, Smooth2);
        var ema1Hl = IndicatorCalculationHelper.CalculateEmaWithNulls(hlValues, Smooth1);
        var ema2Hl = IndicatorCalculationHelper.CalculateEmaWithNulls(ema1Hl, Smooth2);

        for (int i = 0; i < series.Count; i++)
        {
            if (ema2D[i].HasValue && ema2Hl[i].HasValue)
            {
                decimal halfHl = 0.5m * ema2Hl[i]!.Value;
                if (halfHl == 0m)
                {
                    _values.Add(0m);
                }
                else
                {
                    _values.Add(100m * (ema2D[i]!.Value / halfHl));
                }
            }
            else
            {
                _values.Add(null);
            }
        }

        var signal = IndicatorCalculationHelper.CalculateEmaWithNulls(_values, SignalPeriod);
        _signalLine.AddRange(signal);

        return CreateAutomaticResult();
    }
}
