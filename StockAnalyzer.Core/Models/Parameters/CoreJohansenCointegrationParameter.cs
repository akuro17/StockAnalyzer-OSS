using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter configuration for bivariate Johansen (1988) VECM Cointegration test indicator.
/// </summary>
public class CoreJohansenCointegrationParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.JohansenDefaultPeriod;
    private bool _useLogPrices = true;
    private string _comparisonSymbol = string.Empty;
    private PriceType _priceSource = PriceType.Close;
    private PriceType _comparisonPriceSource = PriceType.Close;

    [Category("1. Basic Settings")]
    [DisplayName("Period")]
    [Description("Rolling window length (bars) for Johansen VECM moment estimation.")]
    [CoreParameterRange(IndicatorDefaultConstants.JohansenMinPeriod, IndicatorDefaultConstants.JohansenMaxPeriod)]
    [Range(IndicatorDefaultConstants.JohansenMinPeriod, IndicatorDefaultConstants.JohansenMaxPeriod)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Use Log Prices")]
    [Description("When enabled, transform both series with natural logarithm prior to VECM estimation.")]
    public bool UseLogPrices
    {
        get => _useLogPrices;
        set => SetProperty(ref _useLogPrices, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Comparison Symbol")]
    [Description("Second cointegration pair symbol.")]
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
    [Description("Price field of the comparison symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    bool ICrossTickerParameter.ComparisonSymbolOptional => false;

    public override string GetDisplayName(string type)
    {
        string sym = !string.IsNullOrWhiteSpace(ComparisonSymbol)
            ? ComparisonSymbol.Trim().ToUpperInvariant()
            : "Benchmark";
        return $"{type} ({Period}, {sym})";
    }

    public override int GetRequiredWarmupBars() => Period;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.JohansenMinPeriod, IndicatorDefaultConstants.JohansenMaxPeriod, nameof(Period));

        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), $"Invalid PriceSource: {PriceSource}");

        if (!Enum.IsDefined(typeof(PriceType), ComparisonPriceSource))
            throw new ArgumentOutOfRangeException(nameof(ComparisonPriceSource), $"Invalid ComparisonPriceSource: {ComparisonPriceSource}");
    }
}
