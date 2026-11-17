using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Read-only access to the registered indicators that other indicators read as their input (source indicators) or as their adaptive period (dynamic period drivers).
/// Nothing is created, quarantined or written; a missing file is an empty list.
/// </summary>
public interface IReadOnlyRegisteredIndicatorSource
{
    /// <exception cref="DataUnavailableException">The file exists but cannot be read or parsed.</exception>
    Task<IReadOnlyList<CoreIndicatorSettings>> GetSourceIndicatorsAsync(CancellationToken cancellationToken = default);

    /// <exception cref="DataUnavailableException">The file exists but cannot be read or parsed.</exception>
    Task<IReadOnlyList<CoreIndicatorSettings>> GetDynamicPeriodDriversAsync(CancellationToken cancellationToken = default);
}
