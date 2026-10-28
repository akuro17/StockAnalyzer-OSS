using System.ComponentModel;

namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Parameters shared by the two-symbol Pair Ratio (A / B) and Pair Spread (A - B) indicators:
/// A is the chart symbol read at <see cref="PriceSource"/>, B is <see cref="ComparisonSymbol"/> read at <see cref="ComparisonPriceSource"/>.
/// There is no period: both values are per bar.
/// </summary>
public abstract class CorePairParameterBase : CoreIndicatorParameterBase, ICrossTickerParameter
{
    private PriceType _priceSource = PriceType.Close;
    private string _comparisonSymbol = string.Empty;
    private PriceType _comparisonPriceSource = PriceType.Close;

    [DisplayName("Price Type")]
    [Description("Price type of the chart symbol (A).")]
    public PriceType PriceSource
    {
        get => _priceSource;
        set => SetProperty(ref _priceSource, value);
    }

    [DisplayName("Comparison Symbol")]
    [Description("Ticker symbol of B. Required; with no symbol the indicator has no value.")]
    public string ComparisonSymbol
    {
        get => _comparisonSymbol;
        set => SetProperty(ref _comparisonSymbol, string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant());
    }

    [DisplayName("Comparison Price Type")]
    [Description("Price type of the comparison symbol (B).")]
    public PriceType ComparisonPriceSource
    {
        get => _comparisonPriceSource;
        set => SetProperty(ref _comparisonPriceSource, value);
    }

    public sealed override string GetDisplayName(string indicatorType)
        => PairIndicatorLabel.Format(indicatorType, ComparisonSymbol, PriceSource, ComparisonPriceSource);

    /// <summary>There is no numeric parameter and every <see cref="PriceType"/> value is valid.</summary>
    public sealed override void Validate()
    {
    }
}
