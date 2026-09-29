using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Interfaces;

/// <summary>
/// Service interface responsible for loading, persisting, and resetting user-customized indicator default settings.
/// </summary>
public interface IIndicatorUserDefaultService
{
    /// <summary>
    /// Asynchronously loads all persisted user default indicator settings from disk.
    /// Returns an empty dictionary if no user defaults exist.
    /// </summary>
    Task<Dictionary<IndicatorType, CoreIndicatorSettings>> LoadUserDefaultsAsync();

    /// <summary>
    /// Synchronously loads all persisted user default indicator settings from disk.
    /// </summary>
    Dictionary<IndicatorType, CoreIndicatorSettings> LoadUserDefaults();

    /// <summary>
    /// Saves or updates the user default settings for a specific indicator type.
    /// </summary>
    Task SaveUserDefaultAsync(CoreIndicatorSettings settings);

    /// <summary>
    /// Resets the user default for a specific indicator type back to factory system default.
    /// </summary>
    Task ResetToSystemDefaultAsync(IndicatorType type);

    /// <summary>
    /// Resets all user defaults back to factory system defaults.
    /// </summary>
    Task ResetAllToSystemDefaultAsync();
}
