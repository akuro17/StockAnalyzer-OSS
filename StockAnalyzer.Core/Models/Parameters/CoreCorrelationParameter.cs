using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public enum CorrelationCalculationMode
{
    [Description("Price Level")]
    PriceLevel = 0,

    [Description("Log Return")]
    LogReturn = 1
}

public class CoreCorrelationParameter : CoreIndicatorParameterBase
{
    private int _period = 20;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private CorrelationCalculationMode _calculationMode = CorrelationCalculationMode.PriceLevel;

    [CoreParameterRange(2, 1000)]
    [Range(2, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Pearson Correlation calculation.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Comparison Symbol")]
    [Description("Secondary ticker symbol for cross-ticker correlation (leave empty for Price vs Volume).")]
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

    [DisplayName("Calculation Mode")]
    [Description("Calculation basis: Price Level (raw prices) or Log Return (logarithmic returns).")]
    public CorrelationCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    public override string GetDisplayName(string type)
    {
        string modeSuffix = CalculationMode == CorrelationCalculationMode.LogReturn ? ", Return" : string.Empty;
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            return $"{type} ({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix})";
        }
        return CalculationMode == CorrelationCalculationMode.LogReturn
            ? $"{type} ({Period}, Return)"
            : $"{type} ({Period})";
    }

    public override void Validate()
    {
        if (Period < 2 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 2 and 1000");
    }
}


