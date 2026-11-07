using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Rendering;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Catalog;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan 15, rule R-2: the moving-average set of the AI chart image is checked against the real indicator catalogue the tools use, so a type that is not
/// registered, cannot be bound, or has lost its period parameter is found here and not by a wrong colour in an image.
/// </summary>
public sealed class AiChartMovingAverageSetCatalogTests
{
    [Fact]
    public void EveryTypeOfTheSetIsRegisteredBindableAndHasItsRepresentativeParameter()
    {
        var factory = new IndicatorFactory();
        var provider = new ScreenerCatalogProvider();
        var catalog = new IndicatorCatalog(factory, provider);
        var problems = new List<string>();

        foreach (IndicatorType type in AiChartMovingAverageSet.Types)
        {
            IndicatorCatalogEntry? entry = catalog.Find(type.ToString());
            if (entry is null || !entry.IsRegistered)
            {
                problems.Add($"{type}: not registered");
                continue;
            }

            if (provider.GetDefaultSettings(type, factory) is null)
            {
                problems.Add($"{type}: no default settings (a request cannot be bound)");
            }

            string? parameter = AiChartMovingAverageSet.RepresentativeParameter(type);
            if (parameter is not null && !entry.Parameters.Any(p => string.Equals(p.Name, parameter, StringComparison.Ordinal)))
            {
                problems.Add($"{type}: no parameter named {parameter} (has {string.Join(", ", entry.Parameters.Select(p => p.Name))})");
            }

            if (parameter is null && entry.Parameters.Any(p => string.Equals(p.Name, "Period", StringComparison.Ordinal)))
            {
                problems.Add($"{type}: has a Period parameter but the table says it has none");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void TheRepresentativePeriodOfTheDefaultSettingsIsReadForEveryTypeWithAPeriodParameter()
    {
        var factory = new IndicatorFactory();
        var provider = new ScreenerCatalogProvider();
        var withoutPeriod = new List<string>();

        foreach (IndicatorType type in AiChartMovingAverageSet.Types.Where(t => AiChartMovingAverageSet.RepresentativeParameter(t) is not null))
        {
            CoreIndicatorSettings settings = provider.GetDefaultSettings(type, factory)!;
            int? period = AiChartMovingAverageSet.RepresentativePeriod(type, settings.ParameterObject);
            if (period is null or <= 0)
            {
                withoutPeriod.Add($"{type} ({AiChartMovingAverageSet.RepresentativeParameter(type)})");
            }
        }

        Assert.True(withoutPeriod.Count == 0, "No period read for: " + string.Join(", ", withoutPeriod));
    }
}
