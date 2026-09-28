using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRollingRSquaredParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.RollingRSquaredPeriod;
    private PriceType _priceSource = PriceType.Close;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private BetaCalculationMode _calculationMode = BetaCalculationMode.SimpleReturn;

    [CoreParameterRange(5, 1000)]
    [Range(5, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Rolling R-Squared calculation. Note: Period >= 20 (recommended: 60 to 120) is advised for statistical robustness.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Price Type")]
    [Description("Price type to use for the target asset.")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    [DisplayName("Benchmark Symbol")]
    [Description("Ticker symbol of the benchmark (e.g. SPY). Required for R-Squared calculation.")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant());
    }

    [DisplayName("Benchmark Price Type")]
    [Description("Price type to use for the benchmark symbol.")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [DisplayName("Calculation Mode")]
    [Description("Calculation basis: Simple Return ((P - P_prev)/P_prev) or Log Return (ln(P/P_prev)).")]
    public BetaCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    public override string GetDisplayName(string type)
    {
        string modeSuffix = CalculationMode == BetaCalculationMode.LogReturn ? ", Log" : string.Empty;
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            return $"{type} ({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix})";
        }
        return $"{type} ({Period}{modeSuffix})";
    }

    public override void Validate()
    {
        if (Period < 5 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 5 and 1000.");
    }
}
