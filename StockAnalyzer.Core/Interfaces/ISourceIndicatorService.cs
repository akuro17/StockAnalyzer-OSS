using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Service interface for managing, persisting, and retrieving registered Source Indicators
/// used as chained calculation inputs (Indicator on Indicator) independently of chart display indicators.
/// </summary>
public interface ISourceIndicatorService
{
    /// <summary>
    /// Gets all registered source indicators asynchronously.
    /// </summary>
    Task<IReadOnlyList<CoreIndicatorSettings>> GetSourceIndicatorsAsync();

    /// <summary>
    /// Gets all cached registered source indicators synchronously.
    /// </summary>
    IReadOnlyList<CoreIndicatorSettings> GetSourceIndicators();

    /// <summary>
    /// Gets a specific registered source indicator by its unique identifier.
    /// </summary>
    CoreIndicatorSettings? GetSourceIndicator(string id);

    /// <summary>
    /// Saves or updates a registered source indicator.
    /// </summary>
    Task SaveSourceIndicatorAsync(CoreIndicatorSettings indicator);

    /// <summary>
    /// Deletes a registered source indicator by its unique identifier.
    /// </summary>
    Task<bool> DeleteSourceIndicatorAsync(string id);
}
