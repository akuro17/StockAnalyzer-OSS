using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Technical indicator that models and forecasts price movements using an
/// Autoregressive Integrated Moving Average (ARIMA) model.
/// Plotted as a 1-step-ahead predictive overlay on the main price chart.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.ARIMA)]
public class CoreArimaIndicator : CoreIndicatorBase
{
    public int P { get; set; } = IndicatorDefaultConstants.ArimaDefaultP;
    public int D { get; set; } = IndicatorDefaultConstants.ArimaDefaultD;
    public int Q { get; set; } = IndicatorDefaultConstants.ArimaDefaultQ;
    public int Period { get; set; } = IndicatorDefaultConstants.ArimaDefaultPeriod;

    public override string Name => $"ARIMA({Period},{P},{D},{Q})";
    public override bool IsOverlay => true;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreArimaParameter p)
        {
            P = p.P;
            D = p.D;
            Q = p.Q;
            Period = p.Period;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Failure("No candle data provided.");
        }

        int count = candles.Count;
        _values.Clear();
        _values.Capacity = Math.Max(_values.Capacity, count);

        if (count < Period || Period < 2)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        double[] buffer = ArrayPool<double>.Shared.Rent(Period);

        try
        {
            for (int t = Period - 1; t < count; t++)
            {
                bool hasNull = false;
                for (int i = 0; i < Period; i++)
                {
                    var val = priceSeries[t - Period + 1 + i];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    buffer[i] = (double)val.Value;
                }

                if (hasNull)
                {
                    _values.Add(null);
                    continue;
                }

                bool success = ArimaMath.EstimateArimaForecast(
                    buffer.AsSpan(0, Period),
                    P, D, Q,
                    out double forecast);

                if (success && !double.IsNaN(forecast) && !double.IsInfinity(forecast))
                {
                    _values.Add((decimal)forecast);
                }
                else
                {
                    // Deterministic fallback: last observed price
                    _values.Add(priceSeries[t]!.Value);
                }
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }

        return IndicatorResult.Success(_values);
    }
}
