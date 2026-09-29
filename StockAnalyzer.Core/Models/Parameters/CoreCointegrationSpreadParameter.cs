using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public enum CointegrationPriceMode
{
    [Description("Price Level")]
    PriceLevel = 0,

    [Description("Log Price")]
    LogPrice = 1
}

public class CoreCointegrationSpreadParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.CointegrationSpreadPeriod;
    private int _zScorePeriod = IndicatorDefaultConstants.CointegrationSpreadZScorePeriod;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private CointegrationPriceMode _priceMode = CointegrationPriceMode.PriceLevel;

    [CoreParameterRange(5, 1000)]
    [Range(5, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Rolling OLS Cointegration regression.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [CoreParameterRange(2, 500)]
    [Range(2, 500)]
    [DisplayName("Z-Score Period")]
    [Description("Number of periods for the Spread Z-Score calculation.")]
    public int ZScorePeriod
    {
        get => _zScorePeriod;
        set => SetProperty(ref _zScorePeriod, value);
    }

    [DisplayName("Comparison Symbol")]
    [Description("Ticker symbol of the secondary asset for cointegration spread. Required.")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant());
    }

    [DisplayName("Comparison Price Type")]
    [Description("Price type to use for the comparison symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [DisplayName("Price Mode")]
    [Description("Price representation: Price Level (raw prices) or Log Price (natural logarithm ln(P)).")]
    public CointegrationPriceMode PriceMode
    {
        get => _priceMode;
        set => SetProperty(ref _priceMode, value);
    }

    public override string GetDisplayName(string type)
    {
        string modeSuffix = PriceMode == CointegrationPriceMode.LogPrice ? ", Log" : string.Empty;
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            return $"{type} ({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix}, Z:{ZScorePeriod})";
        }
        return $"{type} ({Period}{modeSuffix}, Z:{ZScorePeriod})";
    }

    public override void Validate()
    {
        if (Period < 5 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 5 and 1000.");
        if (ZScorePeriod < 2 || ZScorePeriod > 500)
            throw new ArgumentOutOfRangeException(nameof(ZScorePeriod), "ZScorePeriod must be between 2 and 500.");
        if (ZScorePeriod > Period)
            throw new ArgumentException("ZScorePeriod must not be greater than Period.", nameof(ZScorePeriod));
    }
}
