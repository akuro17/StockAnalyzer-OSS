using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// The comparison settings shared by the two-symbol statistics of the returns of the chart symbol and a comparison symbol (Treynor, Information Ratio, Tracking Error, Rolling Covariance):
/// <see cref="ComparisonSymbol"/>, <see cref="ComparisonPriceSource"/> and <see cref="CalculationMode"/> (simple or log returns, the enum of Rolling Beta).
/// A blank symbol is not a valid setting: without a comparison symbol the indicator has no value on any bar.
/// </summary>
public abstract class CoreCrossTickerReturnParameterBase : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;
    private BetaCalculationMode _calculationMode = BetaCalculationMode.SimpleReturn;

    [DisplayName("Benchmark Symbol")]
    [Description("Ticker symbol of the benchmark (e.g. SPY). Required: without it the indicator has no value.")]
    [Category("Comparison")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant());
    }

    [DisplayName("Benchmark Price Type")]
    [Description("Price type to use for the benchmark symbol.")]
    [Category("Comparison")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    [DisplayName("Calculation Mode")]
    [Description("Return basis of both series: Simple Return ((P - P_prev)/P_prev) or Log Return (ln(P/P_prev)).")]
    [Category("Estimation")]
    public BetaCalculationMode CalculationMode
    {
        get => _calculationMode;
        set => SetProperty(ref _calculationMode, value);
    }

    /// <summary>The label suffix of the comparison: the symbol and, for log returns, ", Log".</summary>
    protected string ComparisonSuffix()
    {
        string mode = CalculationMode == BetaCalculationMode.LogReturn ? ", Log" : string.Empty;
        return string.IsNullOrWhiteSpace(ComparisonSymbol) ? mode : $", {ComparisonSymbol}{mode}";
    }
}
