using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.Services;

/// <summary>
/// Decides, for one accepted chart refresh, whether a producer-attested snapshot governs the forecasts of that symbol, and
/// applies it. Nothing happens unless the symbol and timeframe are explicitly enrolled in the settings (the default is no
/// enrollment), the SQLite backend provides the capability, and the data source holds an attestation for the instrument.
/// </summary>
public interface IAttestedMonitoringRefresher
{
    /// <summary>True when an attested snapshot was applied, so the legacy observation refresh must not run for this symbol.</summary>
    Task<bool> TryRefreshAsync(string symbol, TimeframeType timeframe, IReadOnlyList<CandleData> acceptedChartBars, CancellationToken ct);
}

/// <inheritdoc />
public sealed class AttestedMonitoringRefresher(PredictionMonitoringBackendFactory backend, IDataService data,
    IOptions<PredictionMonitoringSettings> settings, ILogger<AttestedMonitoringRefresher>? logger = null) : IAttestedMonitoringRefresher
{
    private readonly ILogger _logger = logger ?? NullLogger<AttestedMonitoringRefresher>.Instance;

    public async Task<bool> TryRefreshAsync(string symbol, TimeframeType timeframe, IReadOnlyList<CandleData> acceptedChartBars, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(acceptedChartBars);
        var enrollment = settings.Value.AttestedSources.FirstOrDefault(s => s.Symbol == symbol && s.Timeframe == timeframe);
        if (enrollment is null) return false;
        if (backend.Attested is not { } monitoring || data is not IAttestedMonitoringDataSource source) return false;
        var snapshot = await source.LoadAttestedObservationAsync(enrollment.InstrumentId, timeframe.ToCoreTimeFrame(), ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            _logger.LogDebug("No attestation exists for the enrolled instrument {Instrument}; the legacy observation applies.", enrollment.InstrumentId);
            return false;
        }
        if (snapshot.ProviderId != enrollment.ProviderId || snapshot.InstrumentId != enrollment.InstrumentId || snapshot.Timeframe != timeframe)
            throw new InvalidDataException("The attestation does not come from the enrolled provider, instrument and timeframe.");
        // The evidence must describe exactly the bars the chart accepted; a period snapshot of another timeframe never attests these.
        if (!MonitoringAlignment.Matches(acceptedChartBars, snapshot.Candles, timeframe.ToCoreTimeFrame()))
        {
            _logger.LogInformation("The attestation of {Instrument} does not describe the bars the chart accepted for {Symbol} {Timeframe}; it is not applied.",
                enrollment.InstrumentId, symbol, timeframe);
            return false;
        }
        await monitoring.RefreshAttestedAsync(snapshot, ct).ConfigureAwait(false);
        _logger.LogDebug("Applied the attestation of {Instrument} (epoch {Epoch}, sequence {Sequence}) for {Symbol} {Timeframe}.",
            enrollment.InstrumentId, snapshot.RevisionEpoch, snapshot.ProviderSequence, symbol, timeframe);
        return true;
    }
}
