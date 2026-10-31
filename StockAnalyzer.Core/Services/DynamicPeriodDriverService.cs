using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Thread-safe service responsible for persisting and managing registered Dynamic Period Drivers
/// independently of active chart indicators. Persists to Data/DynamicPeriodDrivers/dynamic_period_drivers.json.
/// The load/save rule (Unloaded state, <c>.corrupted</c> copy, one lock) is that of <see cref="IndicatorListStore"/>.
/// </summary>
public class DynamicPeriodDriverService : IDynamicPeriodDriverService, IDisposable
{
    internal const string FileName = "dynamic_period_drivers.json";
    private readonly IndicatorListStore _store;

    public DynamicPeriodDriverService(string? customFilePath = null, ILogger<DynamicPeriodDriverService>? logger = null)
    {
        _store = new IndicatorListStore(
            customFilePath ?? PathDiscovery.ResolveDynamicPeriodDriversPath(FileName),
            "dynamic period drivers",
            logger ?? NullLogger<DynamicPeriodDriverService>.Instance);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CoreIndicatorSettings>> GetDynamicPeriodDriversAsync() => _store.GetAllAsync();

    /// <inheritdoc />
    public IReadOnlyList<CoreIndicatorSettings> GetDynamicPeriodDrivers() => _store.GetAll();

    /// <inheritdoc />
    public CoreIndicatorSettings? GetDynamicPeriodDriver(string id) => _store.Get(id);

    /// <inheritdoc />
    public Task SaveDynamicPeriodDriverAsync(CoreIndicatorSettings indicator) => _store.SaveAsync(indicator);

    /// <inheritdoc />
    public Task<bool> DeleteDynamicPeriodDriverAsync(string id) => _store.DeleteAsync(id);

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }
}
