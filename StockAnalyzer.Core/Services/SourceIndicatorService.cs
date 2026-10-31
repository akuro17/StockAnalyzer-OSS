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
/// Thread-safe service responsible for persisting and managing registered Source Indicators
/// independently of active chart indicators. Persists to Data/SourceIndicators/source_indicators.json.
/// The load/save rule (Unloaded state, <c>.corrupted</c> copy, one lock) is that of <see cref="IndicatorListStore"/>.
/// </summary>
public class SourceIndicatorService : ISourceIndicatorService, IDisposable
{
    internal const string FileName = "source_indicators.json";
    private readonly IndicatorListStore _store;

    public SourceIndicatorService(string? customFilePath = null, ILogger<SourceIndicatorService>? logger = null)
    {
        _store = new IndicatorListStore(
            customFilePath ?? PathDiscovery.ResolveSourceIndicatorsPath(FileName),
            "source indicators",
            logger ?? NullLogger<SourceIndicatorService>.Instance);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CoreIndicatorSettings>> GetSourceIndicatorsAsync() => _store.GetAllAsync();

    /// <inheritdoc />
    public IReadOnlyList<CoreIndicatorSettings> GetSourceIndicators() => _store.GetAll();

    /// <inheritdoc />
    public CoreIndicatorSettings? GetSourceIndicator(string id) => _store.Get(id);

    /// <inheritdoc />
    public Task SaveSourceIndicatorAsync(CoreIndicatorSettings indicator) => _store.SaveAsync(indicator);

    /// <inheritdoc />
    public Task<bool> DeleteSourceIndicatorAsync(string id) => _store.DeleteAsync(id);

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }
}
