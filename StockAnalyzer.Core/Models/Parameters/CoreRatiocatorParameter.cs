using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public enum RatiocatorCalculationMode
{
    [Description("Raw Ratio")]
    RawRatio = 0,

    [Description("Normalized Ratio (Base 100)")]
    NormalizedRatio = 1,

    [Description("Rolling Ratio (N-day Lookback)")]
    RollingRatio = 2
}

public class CoreRatiocatorParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private int _lookbackPeriod = IndicatorDefaultConstants.RatiocatorLookbackPeriod;
    private int _smoothingPeriod = IndicatorDefaultConstants.RatiocatorSmoothingPeriod;
    private RatiocatorCalculationMode _calculationMode = RatiocatorCalculationMode.RollingRatio;

    [DisplayName("Benchmark Symbol")]
    [Description("Ticker symbol of the benchmark index (e.g. SPY, ^N225). Required for Ratiocator calculation.")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol,
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant());
    }

    [DisplayName("Benchmark Price Type")]
    [Description("Price type to use for the benchmark symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [DisplayName("Calculation Mode")]
    [Description("Calculation basis: Rolling Ratio (P_t / M_t) / (P_{t-N} / M_{t-N}) * 100, Raw Ratio, or Normalized Ratio (First bar = 100).")]
    public RatiocatorCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    [CoreParameterRange(1, 1000)]
    [Range(1, 1000)]
    [DisplayName("Lookback Period (N)")]
    [Description("Number of trading days (N) for comparing relative strength against N days ago: (P_t / M_t) / (P_{t-N} / M_{t-N}) * 100.")]
    public int LookbackPeriod
    {
        get => _lookbackPeriod;
        set => SetProperty(ref _lookbackPeriod, value);
    }

    [CoreParameterRange(0, 1000)]
    [Range(0, 1000)]
    [DisplayName("Smoothing Period")]
    [Description("EMA smoothing period for the signal line. Set to 0 to disable smoothing.")]
    public int SmoothingPeriod
    {
        get => _smoothingPeriod;
        set => SetProperty(ref _smoothingPeriod, value);
    }

    public override int GetRequiredWarmupBars() =>
        CalculationMode == RatiocatorCalculationMode.RollingRatio
            ? LookbackPeriod + (SmoothingPeriod > 1 ? SmoothingPeriod : 0)
            : (SmoothingPeriod > 1 ? SmoothingPeriod : 0);

    public override string GetDisplayName(string type)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            parts.Add(ComparisonSymbol.Trim().ToUpperInvariant());
        }
        if (CalculationMode == RatiocatorCalculationMode.RollingRatio)
        {
            parts.Add($"Roll{LookbackPeriod}");
        }
        else if (CalculationMode == RatiocatorCalculationMode.NormalizedRatio)
        {
            parts.Add("Norm");
        }
        if (SmoothingPeriod > 0)
        {
            parts.Add($"EMA{SmoothingPeriod}");
        }

        return parts.Count > 0 ? $"{type}({string.Join(", ", parts)})" : type;
    }

    public override void Validate()
    {
        if (LookbackPeriod < 1 || LookbackPeriod > 1000)
            throw new ArgumentOutOfRangeException(nameof(LookbackPeriod),
                "Lookback period must be between 1 and 1000.");

        if (SmoothingPeriod < 0 || SmoothingPeriod > 1000)
            throw new ArgumentOutOfRangeException(nameof(SmoothingPeriod),
                "Smoothing period must be between 0 and 1000.");
    }
}
