using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Engine;

namespace StockAnalyzer.McpServer.Catalog;

/// <summary>
/// What the server can say about one indicator type. An unregistered type (defined in <see cref="IndicatorType"/> but without an
/// implementation) has <see cref="IsRegistered"/> false and nothing else.
/// </summary>
internal sealed record IndicatorCatalogEntry(
    string Type,
    bool IsRegistered,
    string DisplayName,
    string? Category,
    string? BacktestEligibility,
    IReadOnlyList<string> OutputSeriesNames,
    IReadOnlyList<IndicatorParameterDescriptor> Parameters,
    bool IsCrossTicker,
    IReadOnlyList<string> NonCausalSeries,
    string? Unit);

/// <summary>
/// The indicator catalogue, built once from the indicator registry, the default settings the Screener uses, the output-series
/// resolver and the backtest eligibility rules, so no list of indicators is kept here.
/// </summary>
internal sealed class IndicatorCatalog
{
    private readonly Lazy<IReadOnlyList<IndicatorCatalogEntry>> _entries;

    public IndicatorCatalog(IIndicatorFactory factory, IScreenerCatalogProvider catalogProvider)
    {
        _entries = new Lazy<IReadOnlyList<IndicatorCatalogEntry>>(() => Build(factory, catalogProvider));
    }

    /// <summary>Every defined indicator type, ordered by name (ordinal).</summary>
    public IReadOnlyList<IndicatorCatalogEntry> Entries => _entries.Value;

    /// <summary>The entry of a type name (case-insensitive, surrounding blanks ignored); numbers and unknown names find nothing.</summary>
    public IndicatorCatalogEntry? Find(string name)
        => Entries.FirstOrDefault(e => string.Equals(e.Type, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The value of backtestEligibility when the backtest accepts the indicator (the rule's own name for it is "None").</summary>
    public const string AllowedEligibility = "Allowed";

    private static string EligibilityName(BacktestIndicatorViolationReason reason)
        => reason == BacktestIndicatorViolationReason.None ? AllowedEligibility : reason.ToString();

    private static IReadOnlyList<IndicatorCatalogEntry> Build(IIndicatorFactory factory, IScreenerCatalogProvider catalogProvider)
    {
        var entries = new List<IndicatorCatalogEntry>();
        foreach (IndicatorType type in Enum.GetValues<IndicatorType>().Distinct().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            if (!factory.IsRegistered(type))
            {
                entries.Add(new IndicatorCatalogEntry(
                    type.ToString(), false, type.GetDescription(), null, null,
                    Array.Empty<string>(), Array.Empty<IndicatorParameterDescriptor>(), false, Array.Empty<string>(), null));
                continue;
            }

            CoreIndicatorSettings? settings = catalogProvider.GetDefaultSettings(type, factory);
            IReadOnlyList<string> series = IndicatorOutputSeriesResolver.GetDisplayNames(type, factory);
            entries.Add(new IndicatorCatalogEntry(
                type.ToString(),
                true,
                type.GetDescription(),
                settings?.Category.ToString(),
                EligibilityName(BacktestIndicatorEligibility.Check(type, null)),
                series,
                settings?.ParameterObject is { } parameters ? IndicatorParameterReader.Read(parameters) : Array.Empty<IndicatorParameterDescriptor>(),
                factory.Create(type) is ICrossTickerIndicator,
                series.Where(name => BacktestIndicatorEligibility.Check(type, name) == BacktestIndicatorViolationReason.NonCausal).ToArray(),
                null)); // The codebase holds no machine-readable output unit per indicator; the unit is not invented (D15-A).
        }

        return entries;
    }
}
