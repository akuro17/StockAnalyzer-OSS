using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced;

/// <summary>
/// Rolling Pearson Autocorrelation Indicator.
/// Computes autocorrelation oscillator values (-1.0 to +1.0) for analyzing cycle dynamics and wave periodicity.
/// Also provides extracted cycle period series ("Period") for driving adaptive indicators.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.Autocorrelation)]
public class CoreAutocorrelationIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 30;
    public int WindowSize
    {
        get => Period;
        set => Period = value;
    }

    public int Lag { get; set; } = 0;
    public int MinLag { get; set; } = 5;
    public int MaxLag { get; set; } = 50;
    public double Threshold { get; set; } = 0.30;
    public double MinProminence { get; set; } = 0.01;
    public int SmoothingPeriod { get; set; } = 5;
    public int FallbackPeriod { get; set; } = 20;
    public int DefaultPeriod
    {
        get => FallbackPeriod;
        set => FallbackPeriod = value;
    }

    public AutocorrelationPeakSelectionMode SelectionMode { get; set; } = AutocorrelationPeakSelectionMode.MaxCorrelation;
    public AutocorrelationFallbackPolicy FallbackPolicy { get; set; } = AutocorrelationFallbackPolicy.HoldPrevious;
    public int MaxHoldBars { get; set; } = 5;
    public CorrelationCalculationMode CalculationMode { get; set; } = CorrelationCalculationMode.PriceLevel;

    public override string Name =>
        Lag > 0
            ? $"Autocorrelation ({Period}, Lag={Lag})"
            : $"Autocorrelation ({Period}, {MinLag}-{MaxLag}, {Threshold:F2})";
    public override bool IsOverlay => false;

    // Series Names
    public const string AutocorrelationSeriesName = IndicatorResult.MainSeriesName;
    public const string DominantPeriodSeriesName = "DominantPeriod";
    public const string PeriodSeriesName = "Period";
    public const string RawPeriodSeriesName = "RawPeriod";
    public const string PeakCorrelationSeriesName = "PeakCorrelation";

    public IReadOnlyList<decimal?> Autocorrelation => _values;
    public List<decimal?> DominantPeriod { get; } = new();
    public List<decimal?> RawPeriod { get; } = new();
    public IReadOnlyList<decimal?> PeakCorrelation => _values;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreAutocorrelationParameter p)
        {
            Period = p.Period;
            Lag = p.Lag;
            MinLag = p.MinLag;
            MaxLag = p.MaxLag;
            Threshold = p.Threshold;
            MinProminence = p.MinProminence;
            SmoothingPeriod = p.SmoothingPeriod;
            FallbackPeriod = p.FallbackPeriod;
            SelectionMode = p.SelectionMode;
            FallbackPolicy = p.FallbackPolicy;
            MaxHoldBars = p.MaxHoldBars;
            CalculationMode = p.CalculationMode;
            PriceSource = p.PriceSource;
        }
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        DominantPeriod.Clear();
        RawPeriod.Clear();

        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
        return CalculateFromSeries(priceSeries);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        DominantPeriod.Clear();
        RawPeriod.Clear();

        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Empty();
        }

        return CalculateFromSeries(series);
    }

    private IIndicatorResult CalculateFromSeries(IReadOnlyList<decimal?> series)
    {
        IReadOnlyList<decimal?> calcSeries = series;
        if (CalculationMode == CorrelationCalculationMode.LogReturn)
        {
            calcSeries = IndicatorCalculationHelper.ConvertToLogReturns(series);
        }

        var (dom, raw, peak) = AutocorrelationEngine.CalculateDominantPeriod(
            calcSeries,
            Period,
            MinLag,
            MaxLag,
            Threshold,
            SmoothingPeriod,
            FallbackPeriod,
            SelectionMode,
            FallbackPolicy,
            MaxHoldBars,
            Lag,
            MinProminence);

        _values.AddRange(peak);
        DominantPeriod.AddRange(dom);
        RawPeriod.AddRange(raw);

        return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            { AutocorrelationSeriesName, _values },
            { "Autocorrelation", _values },
            { "Correlation", _values },
            { PeakCorrelationSeriesName, _values },
            { PeriodSeriesName, DominantPeriod },
            { DominantPeriodSeriesName, DominantPeriod },
            { RawPeriodSeriesName, RawPeriod }
        });
    }
}

/// <summary>
/// Backward-compatible alias for CoreAutocorrelationIndicator.
/// </summary>
public class CoreAutocorrelationPeriodIndicator : CoreAutocorrelationIndicator
{
}
