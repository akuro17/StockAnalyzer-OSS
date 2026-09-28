using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Service interface for managing, persisting, and retrieving registered Dynamic Period Drivers
/// used as modulation inputs for adaptive indicators independently of active chart display indicators.
/// </summary>
public interface IDynamicPeriodDriverService
{
    /// <summary>
    /// Gets all registered dynamic period drivers asynchronously.
    /// </summary>
    Task<IReadOnlyList<CoreIndicatorSettings>> GetDynamicPeriodDriversAsync();

    /// <summary>
    /// Gets all cached registered dynamic period drivers synchronously.
    /// </summary>
    IReadOnlyList<CoreIndicatorSettings> GetDynamicPeriodDrivers();

    /// <summary>
    /// Gets a specific registered dynamic period driver by its unique identifier.
    /// </summary>
    CoreIndicatorSettings? GetDynamicPeriodDriver(string id);

    /// <summary>
    /// Saves or updates a registered dynamic period driver.
    /// </summary>
    Task SaveDynamicPeriodDriverAsync(CoreIndicatorSettings indicator);

    /// <summary>
    /// Deletes a registered dynamic period driver by its unique identifier.
    /// </summary>
    Task<bool> DeleteDynamicPeriodDriverAsync(string id);
}
