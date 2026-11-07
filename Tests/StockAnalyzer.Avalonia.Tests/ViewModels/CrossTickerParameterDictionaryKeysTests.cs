using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>
/// S-1 guard: <see cref="ICrossTickerParameter.ComparisonSymbolOptional"/> is an explicit interface member, so the registration screen's
/// <see cref="IndicatorRegistrationViewModel.BuildParameterDictionary(CoreIndicatorParameterBase?)"/> (which stores EVERY public readable property) must keep producing
/// exactly the keys it produced before the member existed: a new key would change saved templates, scatter files, the parameter form and the MCP schema.
/// </summary>
public class CrossTickerParameterDictionaryKeysTests
{
    [Fact]
    public void BuildParameterDictionary_OfCorrelation_HasNoOptionalKey_AndKeepsItsParameters()
    {
        var keys = IndicatorRegistrationViewModel.BuildParameterDictionary(new CoreCorrelationParameter()).Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        Assert.DoesNotContain(keys, key => key.Contains("Optional", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("ComparisonSymbol", keys);
        Assert.Contains("ComparisonPriceSource", keys);
        Assert.Contains("CalculationMode", keys);
        Assert.Contains("Period", keys);
    }

    [Fact]
    public void BuildParameterDictionary_OfCrossCorrelation_HasNoOptionalKey_AndKeepsItsParameters()
    {
        var keys = IndicatorRegistrationViewModel.BuildParameterDictionary(new CoreCrossCorrelationParameter()).Keys.ToArray();

        Assert.DoesNotContain(keys, key => key.Contains("Optional", StringComparison.OrdinalIgnoreCase));
        foreach (string expected in new[]
                 {
                     "PriceSource", "Period", "Lag", "EnableDynamicLag", "MinLag", "MaxLag", "Threshold", "LagSmoothingPeriod", "PeakSelectionMode",
                     "ComparisonSymbol", "ComparisonPriceSource", "CalculationMode",
                 })
        {
            Assert.Contains(expected, keys);
        }
    }
}
