using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Attributes;
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
    public const string SelectedPSeriesName = nameof(SelectedP);
    public const string SelectedDSeriesName = nameof(SelectedD);
    public const string SelectedQSeriesName = nameof(SelectedQ);

    public int P { get; set; } = IndicatorDefaultConstants.ArimaDefaultP;
    public int D { get; set; } = IndicatorDefaultConstants.ArimaDefaultD;
    public int Q { get; set; } = IndicatorDefaultConstants.ArimaDefaultQ;
    public int Period { get; set; } = IndicatorDefaultConstants.ArimaDefaultPeriod;
    public bool AutoOrderSelection { get; set; } = IndicatorDefaultConstants.ArimaDefaultAutoOrderSelection;
    public int MaxAutoP { get; set; } = IndicatorDefaultConstants.ArimaDefaultMaxAutoP;
    public int MaxAutoQ { get; set; } = IndicatorDefaultConstants.ArimaDefaultMaxAutoQ;
    public int OrderRefitInterval { get; set; } = IndicatorDefaultConstants.ArimaDefaultOrderRefitInterval;

    [IndicatorResultIgnore]
    public List<decimal?> SelectedP { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> SelectedD { get; } = new();

    [IndicatorResultIgnore]
    public List<decimal?> SelectedQ { get; } = new();

    public override string Name => AutoOrderSelection
        ? $"Auto-ARIMA({Period}, max:{MaxAutoP},{MaxAutoQ})"
        : $"ARIMA({Period},{P},{D},{Q})";
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
            AutoOrderSelection = p.AutoOrderSelection;
            MaxAutoP = p.MaxAutoP;
            MaxAutoQ = p.MaxAutoQ;
            OrderRefitInterval = p.OrderRefitInterval;
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
        SelectedP.Clear();
        SelectedD.Clear();
        SelectedQ.Clear();

        if (!AutoOrderSelection)
        {
            return CalculateFixedOrderSeries(candles, count);
        }

        if (count < Period || Period < 2)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
                SelectedP.Add(null);
                SelectedD.Add(null);
                SelectedQ.Add(null);
            }
            return CreateAutoOrderResult();
        }

        for (int i = 0; i < Period - 1; i++)
        {
            _values.Add(null);
            SelectedP.Add(null);
            SelectedD.Add(null);
            SelectedQ.Add(null);
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        double[] buffer = ArrayPool<double>.Shared.Rent(Period);
        int maxAutoP = Math.Clamp(MaxAutoP, IndicatorDefaultConstants.ArimaMinMaxAutoOrder, IndicatorDefaultConstants.ArimaMaxMaxAutoOrder);
        int maxAutoQ = Math.Clamp(MaxAutoQ, IndicatorDefaultConstants.ArimaMinMaxAutoOrder, IndicatorDefaultConstants.ArimaMaxMaxAutoOrder);
        int refitInterval = Math.Max(1, OrderRefitInterval);
        bool hasSelectedOrder = false;
        int activeP = 0;
        int activeD = 0;
        int activeQ = 0;
        int lastRefitBar = -refitInterval;

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
                    hasSelectedOrder = false;
                    _values.Add(null);
                    SelectedP.Add(null);
                    SelectedD.Add(null);
                    SelectedQ.Add(null);
                    continue;
                }

                ReadOnlySpan<double> window = buffer.AsSpan(0, Period);
                if (!hasSelectedOrder || t - lastRefitBar >= refitInterval)
                {
                    (activeP, activeD, activeQ, _) = ArimaMath.SelectBestArimaOrder(window, maxAutoP, maxAutoQ);
                    hasSelectedOrder = true;
                    lastRefitBar = t;
                }

                bool success = ArimaMath.EstimateArimaForecast(window, activeP, activeD, activeQ, out double forecast);
                _values.Add(success && double.IsFinite(forecast)
                    ? IndicatorCalculationHelper.ToDecimalOrNull(forecast) ?? priceSeries[t]!.Value
                    : priceSeries[t]!.Value);
                SelectedP.Add(activeP);
                SelectedD.Add(activeD);
                SelectedQ.Add(activeQ);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }

        return CreateAutoOrderResult();
    }

    private IIndicatorResult CreateAutoOrderResult() =>
        IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [IndicatorResult.MainSeriesName] = _values,
            [SelectedPSeriesName] = SelectedP,
            [SelectedDSeriesName] = SelectedD,
            [SelectedQSeriesName] = SelectedQ
        });

    private IIndicatorResult CalculateFixedOrderSeries(IReadOnlyList<CoreCandleData> candles, int count)
    {
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
