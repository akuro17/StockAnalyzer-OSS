using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace StockAnalyzer.Core.Models.Parameters;

public class CoreRollingAlphaParameter : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private int _period = IndicatorDefaultConstants.RollingAlphaPeriod;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private BetaCalculationMode _calculationMode = BetaCalculationMode.SimpleReturn;
    private bool _annualize = IndicatorDefaultConstants.RollingAlphaAnnualizeDefault;
    private int _annualizationFactor = IndicatorDefaultConstants.RollingAlphaAnnualizationFactor;

    [CoreParameterRange(5, 1000)]
    [Range(5, 1000)]
    [DisplayName("Period")]
    [Description("Number of periods for the Rolling Alpha calculation.")]
    public int Period
    {
        get => _period;
        set => SetProperty(ref _period, value);
    }

    [DisplayName("Benchmark Symbol")]
    [Description("Ticker symbol of the benchmark (e.g. SPY). Required for Alpha calculation.")]
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

    [DisplayName("Annualize")]
    [Description("Annualize Alpha via linear scaling (α × Factor). Represents annualized market model intercept (Rf = 0).")]
    public bool Annualize
    {
        get => _annualize;
        set => SetProperty(ref _annualize, value);
    }

    [CoreParameterRange(1, 365)]
    [Range(1, 365)]
    [DisplayName("Annualization Factor")]
    [Description("Trading days per year for annualizing alpha (typically 252 for US stocks).")]
    public int AnnualizationFactor
    {
        get => _annualizationFactor;
        set => SetProperty(ref _annualizationFactor, value);
    }

    public override string GetDisplayName(string type)
    {
        string modeSuffix = CalculationMode == BetaCalculationMode.LogReturn ? ", Log" : string.Empty;
        string annSuffix = Annualize ? ", Ann" : string.Empty;
        if (!string.IsNullOrWhiteSpace(ComparisonSymbol))
        {
            return $"{type} ({Period}, {ComparisonSymbol.Trim().ToUpperInvariant()}{modeSuffix}{annSuffix})";
        }
        return $"{type} ({Period}{modeSuffix}{annSuffix})";
    }

    public override void Validate()
    {
        if (Period < 5 || Period > 1000)
            throw new ArgumentOutOfRangeException(nameof(Period), "Period must be between 5 and 1000.");
        if (AnnualizationFactor < 1 || AnnualizationFactor > 365)
            throw new ArgumentOutOfRangeException(nameof(AnnualizationFactor), "AnnualizationFactor must be between 1 and 365.");
    }
}
