using System;
using System.Collections.Generic;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Partial Autocorrelation Function (PACF) Indicator.
/// Computes rolling partial autocorrelation coefficients at target lag k using Levinson-Durbin recursion.
/// Also provides two-sided 95% Bartlett confidence bands (+/- 1.96 / sqrt(N')).
/// </summary>
[StockAnalyzerIndicator(IndicatorType.PACF)]
public class CorePacfIndicator : CoreIndicatorBase
{
    private int _period = IndicatorDefaultConstants.PacfDefaultPeriod;
    private int _lag = IndicatorDefaultConstants.PacfDefaultLag;
    private CorrelationCalculationMode _calculationMode = CorrelationCalculationMode.PriceLevel;

    public const string UpperConfidenceBandSeriesName = "UpperConfidenceBand";
    public const string LowerConfidenceBandSeriesName = "LowerConfidenceBand";

    public int Period
    {
        get => _period;
        set => _period = value;
    }

    public int Lag
    {
        get => _lag;
        set => _lag = value;
    }

    public CorrelationCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => _calculationMode = value;
    }

    public override string Name
    {
        get
        {
            string modeSuffix = CalculationMode == CorrelationCalculationMode.LogReturn ? ", Return" : string.Empty;
            return $"PACF({Period}, Lag={Lag}{modeSuffix})";
        }
    }

    public override bool IsOverlay => false;

    public IReadOnlyList<decimal?> Pacf => _values;
    public List<decimal?> UpperConfidenceBand { get; } = new();
    public List<decimal?> LowerConfidenceBand { get; } = new();

    public CorePacfIndicator()
    {
    }

    public CorePacfIndicator(int period, int lag = 1)
    {
        _period = period;
        _lag = lag;
    }

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CorePacfParameter p)
        {
            Period = p.Period;
            Lag = p.Lag;
            CalculationMode = p.CalculationMode;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        UpperConfidenceBand.Clear();
        LowerConfidenceBand.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = candles.Count;
        _values.Capacity = count;
        UpperConfidenceBand.Capacity = count;
        LowerConfidenceBand.Capacity = count;

        int effectiveLength = CalculationMode == CorrelationCalculationMode.LogReturn ? Period - 1 : Period;

        if (count < Period || effectiveLength <= Lag || effectiveLength < 2)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
            }
            return IndicatorResult.Success(CreateSeriesDictionary());
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        decimal bartlettBound = (decimal)Math.Round(PacfMath.BartlettZ95 / Math.Sqrt(effectiveLength), 8, MidpointRounding.AwayFromZero);

        Span<double> rawWindow = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> transformedWindow = effectiveLength <= MathBufferLimits.StackAllocThreshold ? stackalloc double[effectiveLength] : new double[effectiveLength];
        Span<double> pacfCoeffs = (Lag + 1) <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Lag + 1] : new double[Lag + 1];

        for (int i = 0; i < count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
                continue;
            }

            int startIndex = i - Period + 1;
            bool loaded = PriceDataHelper.TryLoadWindow(priceSeries, startIndex, rawWindow);

            if (!loaded)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
                continue;
            }

            bool validTransform = true;
            if (CalculationMode == CorrelationCalculationMode.LogReturn)
            {
                for (int t = 0; t < effectiveLength; t++)
                {
                    double p0 = rawWindow[t];
                    double p1 = rawWindow[t + 1];

                    if (!(p0 > 1e-12) || !(p1 > 1e-12) || !double.IsFinite(p0) || !double.IsFinite(p1))
                    {
                        validTransform = false;
                        break;
                    }

                    transformedWindow[t] = Math.Log(p1 / p0);
                }
            }
            else
            {
                rawWindow.Slice(0, effectiveLength).CopyTo(transformedWindow);
            }

            if (!validTransform)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
                continue;
            }

            bool ok = PacfMath.TryCalculatePacf(transformedWindow, Lag, pacfCoeffs, out _);

            if (ok && double.IsFinite(pacfCoeffs[Lag]))
            {
                decimal pacfDec = (decimal)Math.Round(Math.Clamp(pacfCoeffs[Lag], -1.0, 1.0), 8, MidpointRounding.AwayFromZero);
                _values.Add(pacfDec);
                UpperConfidenceBand.Add(bartlettBound);
                LowerConfidenceBand.Add(-bartlettBound);
            }
            else
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
            }
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        UpperConfidenceBand.Clear();
        LowerConfidenceBand.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        int count = series.Count;
        _values.Capacity = count;
        UpperConfidenceBand.Capacity = count;
        LowerConfidenceBand.Capacity = count;

        int effectiveLength = CalculationMode == CorrelationCalculationMode.LogReturn ? Period - 1 : Period;

        if (count < Period || effectiveLength <= Lag || effectiveLength < 2)
        {
            for (int i = 0; i < count; i++)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
            }
            return IndicatorResult.Success(CreateSeriesDictionary());
        }

        decimal bartlettBound = (decimal)Math.Round(PacfMath.BartlettZ95 / Math.Sqrt(effectiveLength), 8, MidpointRounding.AwayFromZero);

        Span<double> rawWindow = Period <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Period] : new double[Period];
        Span<double> transformedWindow = effectiveLength <= MathBufferLimits.StackAllocThreshold ? stackalloc double[effectiveLength] : new double[effectiveLength];
        Span<double> pacfCoeffs = (Lag + 1) <= MathBufferLimits.StackAllocThreshold ? stackalloc double[Lag + 1] : new double[Lag + 1];

        for (int i = 0; i < count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
                continue;
            }

            int startIndex = i - Period + 1;
            bool loaded = PriceDataHelper.TryLoadWindow(series, startIndex, rawWindow);

            if (!loaded)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
                continue;
            }

            bool validTransform = true;
            if (CalculationMode == CorrelationCalculationMode.LogReturn)
            {
                for (int t = 0; t < effectiveLength; t++)
                {
                    double p0 = rawWindow[t];
                    double p1 = rawWindow[t + 1];

                    if (!(p0 > 1e-12) || !(p1 > 1e-12) || !double.IsFinite(p0) || !double.IsFinite(p1))
                    {
                        validTransform = false;
                        break;
                    }

                    transformedWindow[t] = Math.Log(p1 / p0);
                }
            }
            else
            {
                rawWindow.Slice(0, effectiveLength).CopyTo(transformedWindow);
            }

            if (!validTransform)
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
                continue;
            }

            bool ok = PacfMath.TryCalculatePacf(transformedWindow, Lag, pacfCoeffs, out _);

            if (ok && double.IsFinite(pacfCoeffs[Lag]))
            {
                decimal pacfDec = (decimal)Math.Round(Math.Clamp(pacfCoeffs[Lag], -1.0, 1.0), 8, MidpointRounding.AwayFromZero);
                _values.Add(pacfDec);
                UpperConfidenceBand.Add(bartlettBound);
                LowerConfidenceBand.Add(-bartlettBound);
            }
            else
            {
                _values.Add(null);
                UpperConfidenceBand.Add(null);
                LowerConfidenceBand.Add(null);
            }
        }

        return IndicatorResult.Success(CreateSeriesDictionary());
    }

    private Dictionary<string, IReadOnlyList<decimal?>> CreateSeriesDictionary()
    {
        return new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { IndicatorResult.MainSeriesName, _values },
            { UpperConfidenceBandSeriesName, UpperConfidenceBand },
            { LowerConfidenceBandSeriesName, LowerConfidenceBand }
        };
    }
}
