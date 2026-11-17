using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameter configuration for bivariate Granger Causality &amp; Engle (2002) DCC(1,1) indicator.
/// </summary>
public class CoreGrangerCausalityParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.GrangerCausalityDefaultPeriod;
    private int _lagOrder = IndicatorDefaultConstants.GrangerCausalityDefaultLagOrder;
    private string _comparisonSymbol = string.Empty;
    private PriceType _priceSource = PriceType.Close;
    private PriceType _comparisonPriceSource = PriceType.Close;

    [Category("1. Basic Settings")]
    [DisplayName("Period")]
    [Description("Rolling estimation window length (bars) for VAR(p) and DCC(1,1).")]
    [CoreParameterRange(IndicatorDefaultConstants.GrangerCausalityMinPeriod, IndicatorDefaultConstants.GrangerCausalityMaxPeriod)]
    [Range(IndicatorDefaultConstants.GrangerCausalityMinPeriod, IndicatorDefaultConstants.GrangerCausalityMaxPeriod)]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Lag Order (p)")]
    [Description("VAR(p) lag order included in restricted and unrestricted regressions.")]
    [CoreParameterRange(IndicatorDefaultConstants.GrangerCausalityMinLagOrder, IndicatorDefaultConstants.GrangerCausalityMaxLagOrder)]
    [Range(IndicatorDefaultConstants.GrangerCausalityMinLagOrder, IndicatorDefaultConstants.GrangerCausalityMaxLagOrder)]
    public int LagOrder
    {
        get => _lagOrder;
        set => SetProperty(ref _lagOrder, value);
    }

    [Category("1. Basic Settings")]
    [DisplayName("Comparison Symbol")]
    [Description("Comparison symbol tested as Granger cause of the primary symbol.")]
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
        return $"{type} ({Period}, L{LagOrder}, {sym})";
    }

    public override int GetRequiredWarmupBars() => Period + 1;

    public override void Validate()
    {
        ThrowIfOutOfRange(Period, IndicatorDefaultConstants.GrangerCausalityMinPeriod, IndicatorDefaultConstants.GrangerCausalityMaxPeriod, nameof(Period));
        ThrowIfOutOfRange(LagOrder, IndicatorDefaultConstants.GrangerCausalityMinLagOrder, IndicatorDefaultConstants.GrangerCausalityMaxLagOrder, nameof(LagOrder));

        int df2 = (Period - LagOrder) - 2 * LagOrder - 1;
        if (df2 < IndicatorDefaultConstants.GrangerCausalityMinDf2)
        {
            throw new ArgumentOutOfRangeException(nameof(Period), $"Period ({Period}) minus 3*LagOrder - 1 must be >= {IndicatorDefaultConstants.GrangerCausalityMinDf2}.");
        }

        if (!Enum.IsDefined(typeof(PriceType), PriceSource))
            throw new ArgumentOutOfRangeException(nameof(PriceSource), $"Invalid PriceSource: {PriceSource}");

        if (!Enum.IsDefined(typeof(PriceType), ComparisonPriceSource))
            throw new ArgumentOutOfRangeException(nameof(ComparisonPriceSource), $"Invalid ComparisonPriceSource: {ComparisonPriceSource}");
    }
}
