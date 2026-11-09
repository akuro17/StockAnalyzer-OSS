using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter configuration for Cross-Ticker Rank Correlation indicator.
/// </summary>
public class CoreRankCorrelationParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.RankCorrelationDefaultPeriod;
    private string _comparisonSymbol = string.Empty;
    private PriceType _priceSource = PriceType.Close;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private RankCorrelationMethod _method = RankCorrelationMethod.Spearman;

    [Category("1. Basic Settings")]
    [DisplayName("Period")]
    [Description("Calculation window size (bars) for rolling rank correlation.")]
    [CoreParameterRange(5, 200)]
    [Range(5, 200)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Comparison Symbol")]
    [Description("Benchmark ticker symbol. When left blank, correlates Price against Volume of primary ticker.")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, value ?? string.Empty);
    }

    [Category("2. Source Settings")]
    [DisplayName("Price Source")]
    [Description("Price field of the primary chart symbol.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    [Category("2. Source Settings")]
    [DisplayName("Comparison Price Source")]
    [Description("Price field of the benchmark symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [Category("3. Method")]
    [DisplayName("Method")]
    [Description("Rank correlation algorithm: Spearman's rho or Kendall's tau-b.")]
    public RankCorrelationMethod Method
    {
        get => _method;
        set => SetProperty(ref _method, value);
    }

    // SSoT Mandate: Implemented EXPLICITLY because empty ComparisonSymbol falls back to Price vs Volume.
    bool ICrossTickerParameter.ComparisonSymbolOptional => true;

    public override string GetDisplayName(string type)
    {
        string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol) ? ComparisonSymbol.Trim().ToUpperInvariant() : "Volume";
        return $"{type} ({Period}, {sym}, {Method})";
    }

    public override void Validate()
    {
        if (Period < 5 || Period > 200)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 5 and 200.");

        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), $"Invalid PriceSource: {PriceSource}");

        if (!Enum.IsDefined(typeof(PriceType), ComparisonPriceSource))
            throw new ArgumentOutOfRangeException(nameof(ComparisonPriceSource), $"Invalid ComparisonPriceSource: {ComparisonPriceSource}");

        if (!Enum.IsDefined(typeof(RankCorrelationMethod), Method))
            throw new ArgumentOutOfRangeException(nameof(Method), $"Invalid Method: {Method}");
    }
}
