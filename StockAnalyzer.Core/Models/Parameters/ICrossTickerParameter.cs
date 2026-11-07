namespace StockAnalyzer.Core.Models.Parameters;

/// <summary>
/// Defines parameter contracts for indicators that compare against a secondary benchmark ticker.
/// </summary>
public interface ICrossTickerParameter
{
    /// <summary>
    /// Gets or sets the ticker symbol of the comparison benchmark.
    /// </summary>
    string ComparisonSymbol { get; set; }

    /// <summary>
    /// Gets or sets the price source type used for the comparison benchmark.
    /// </summary>
    PriceType ComparisonPriceSource { get; set; }

    /// <summary>
    /// True when a blank <see cref="ComparisonSymbol"/> is a valid setting (Correlation and CrossCorrelation then compare Price against Volume of the same ticker),
    /// false when the indicator cannot give a value without a comparison symbol. Default: false.
    /// A type that returns true MUST implement this member EXPLICITLY (<c>bool ICrossTickerParameter.ComparisonSymbolOptional =&gt; true;</c>), never as a public
    /// property: the parameter readers (<c>IndicatorRegistrationViewModel.BuildParameterDictionary</c>, <c>ParameterViewBuilder</c>, the MCP settings binder) take every
    /// public property for a user parameter, so a public one would change saved templates, scatter files, the parameter form and the MCP schema.
    /// </summary>
    bool ComparisonSymbolOptional => false;
}
