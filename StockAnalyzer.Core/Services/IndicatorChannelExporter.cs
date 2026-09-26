using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Computes <see cref="FeatureChannelKind.Indicator"/> channel values for a
/// <see cref="PredictionFeatureMode.ComposedFeatures"/> training run, using the exact same
/// <see cref="IIndicatorFactory"/> / <see cref="ICoreIndicator"/> formulas the Avalonia UI and
/// live inference already use (single formula source; Python never reimplements indicator math).
/// Each symbol's per-channel <see cref="IIndicatorResult.MainValues"/> series is exported to a
/// standalone parquet file that <c>run_training.py</c> joins to the OHLCV parquet by the shared
/// <c>date</c> column.
/// </summary>
/// <remarks>
/// Every <see cref="FeatureChannelKind.Indicator"/> channel is converted to a live
/// <see cref="CoreIndicatorParameterBase"/> via <see cref="FeatureChannelConverter.ResolveParameterObject"/>,
/// the same single conversion point the Training Wizard's parameter picker
/// (<c>FeatureChannelPickerViewModel</c>) and <see cref="Services.PredictionService"/> use, including for
/// a channel with empty <see cref="FeatureChannel.Params"/>. An empty-Params channel used to bypass this
/// conversion (passing <see langword="null"/> directly to <see cref="IIndicatorFactory.Create"/>) because
/// some indicators' registry-default baseline previously disagreed with the indicator class's own
/// hardcoded default; that gap is now closed (see <c>CoreIndicatorBase.SeedPeriodFromIndicatorDefault</c>)
/// and verified to hold for every registered indicator by
/// <c>Tests.StockAnalyzer.Core.Tests.Models.IndicatorParameterDefaultParityTests</c>, so routing empty
/// Params through the same conversion as non-empty Params no longer changes any already-shipped
/// channel's computed values.
/// </remarks>
public sealed class IndicatorChannelExporter
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly ILogger<IndicatorChannelExporter>? _logger;

    public IndicatorChannelExporter(
        IMarketDataProvider marketDataProvider,
        IIndicatorFactory indicatorFactory,
        ILogger<IndicatorChannelExporter>? logger = null)
    {
        _marketDataProvider = marketDataProvider ?? throw new ArgumentNullException(nameof(marketDataProvider));
        _indicatorFactory = indicatorFactory ?? throw new ArgumentNullException(nameof(indicatorFactory));
        _logger = logger;
    }

    /// <summary>
    /// Exports every <see cref="FeatureChannelKind.Indicator"/> channel of <paramref name="spec"/>
    /// for each of <paramref name="symbols"/> to its own parquet file. Returns an empty map (no
    /// files written) when <paramref name="spec"/> has no Indicator-kind channel. Throws
    /// <see cref="InvalidOperationException"/> when <paramref name="timeframe"/> is not
    /// <see cref="TrainingTimeframe.Daily"/> and an Indicator-kind channel is present, because
    /// <see cref="IMarketDataProvider.GetTickersDataAsync"/> only supports
    /// <see cref="TimeFrame.D1"/> today.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ExportAsync(
        FeatureSpec spec,
        IReadOnlyList<string> symbols,
        TrainingTimeframe timeframe,
        string runId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(symbols);
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("IndicatorChannelExporter: runId cannot be blank.", nameof(runId));
        }

        var indicatorChannels = spec.Channels
            .Select((channel, index) => (Index: index, Channel: channel))
            .Where(pair => pair.Channel.Kind == FeatureChannelKind.Indicator)
            .ToList();

        if (indicatorChannels.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        if (timeframe != TrainingTimeframe.Daily)
        {
            throw new InvalidOperationException(
                $"IndicatorChannelExporter: Indicator-kind feature channels require Timeframe.Daily; " +
                $"'{timeframe}' is not supported because IMarketDataProvider.GetTickersDataAsync only " +
                "reads TimeFrame.D1 in this release.");
        }

        // Built once per channel here (not per symbol below): FeatureChannelConverter.ResolveParameterObject
        // is reflection-based, and the resulting ParameterObject is safe to reuse across every symbol's
        // IIndicatorFactory.Create call because IIndicator.Configure only reads from it.
        var parameterObjects = new Dictionary<int, CoreIndicatorParameterBase?>();
        foreach (var (index, channel) in indicatorChannels)
        {
            var indicatorType = channel.Indicator!.Value;
            if (!_indicatorFactory.IsRegistered(indicatorType))
            {
                throw new InvalidOperationException(
                    $"IndicatorChannelExporter: channel [{index}] IndicatorType '{indicatorType}' is not registered in IIndicatorFactory.");
            }

            parameterObjects[index] = FeatureChannelConverter.ResolveParameterObject(channel, _indicatorFactory, out var warnings);
            foreach (var warning in warnings)
            {
                _logger?.LogWarning("IndicatorChannelExporter: channel [{Index}] {Warning}", index, warning);
            }
        }

        var result = new Dictionary<string, string>();
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();

            var candles = await _marketDataProvider.GetTickersDataAsync(symbol, TimeFrame.D1).ConfigureAwait(false);
            if (candles.Count == 0)
            {
                throw new InvalidOperationException(
                    $"IndicatorChannelExporter: no candle data available for symbol '{symbol}' (TimeFrame.D1).");
            }

            var coreCandles = candles
                .Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume))
                .ToList();

            var channelValues = new Dictionary<int, IReadOnlyList<decimal?>>();
            foreach (var (index, channel) in indicatorChannels)
            {
                var indicatorType = channel.Indicator!.Value;
                var indicator = _indicatorFactory.Create(indicatorType, parameterObjects[index])
                    ?? throw new InvalidOperationException(
                        $"IndicatorChannelExporter: IIndicatorFactory.Create returned null for registered IndicatorType '{indicatorType}'.");

                var calcResult = indicator.Calculate(coreCandles);
                if (!calcResult.IsSuccessful)
                {
                    throw new InvalidOperationException(
                        $"IndicatorChannelExporter: indicator '{indicatorType}' calculation failed for symbol '{symbol}': {calcResult.ErrorMessage}");
                }

                if (calcResult.MainValues.Count != coreCandles.Count)
                {
                    throw new InvalidOperationException(
                        $"IndicatorChannelExporter: indicator '{indicatorType}' returned {calcResult.MainValues.Count} values " +
                        $"for {coreCandles.Count} candles of symbol '{symbol}' (expected one value per candle).");
                }

                channelValues[index] = calcResult.MainValues;
            }

            var exportPath = WriteChannelParquet(symbol, runId, coreCandles, channelValues);
            result[symbol] = exportPath;
            _logger?.LogInformation(
                "IndicatorChannelExporter: exported {ChannelCount} indicator channel(s) for {Symbol} to {Path}",
                channelValues.Count, symbol, exportPath);
        }

        return result;
    }

    /// <summary>
    /// Writes one parquet file with a <c>date</c> column plus one <c>channel_{index}</c> column
    /// per exported channel, using the same DuckDB <c>COPY (...) TO ... (FORMAT PARQUET)</c>
    /// approach as <see cref="ParquetMarketDataProvider"/>. Uses a private in-memory DuckDB
    /// connection (mirroring the test-only <c>ParquetFixtureBuilder</c> pattern) rather than the
    /// shared <see cref="DuckDBConnectionManager"/>, because this write is a self-contained
    /// value-to-parquet dump unrelated to the shared reader's persisted data store and must not
    /// contend for that store's single global lock while a training run is in flight.
    /// </summary>
    private static string WriteChannelParquet(
        string symbol,
        string runId,
        IReadOnlyList<CoreCandleData> candles,
        IReadOnlyDictionary<int, IReadOnlyList<decimal?>> channelValues)
    {
        var exportPath = Path.Combine(Path.GetTempPath(), $"sa_indicator_channels_{runId}_{symbol}.parquet")
            .Replace("\\", "/");
        var indices = channelValues.Keys.OrderBy(i => i).ToList();

        var rows = new List<string>(candles.Count);
        for (int r = 0; r < candles.Count; r++)
        {
            var cols = new List<string>(indices.Count + 1)
            {
                $"DATE '{candles[r].Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
            };

            foreach (var index in indices)
            {
                var value = channelValues[index][r];
                cols.Add(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "NULL");
            }

            rows.Add($"({string.Join(", ", cols)})");
        }

        var valueColumnNames = string.Join(", ", indices.Select(i => $"channel_{i}"));
        var selectColumns = string.Join(", ", indices.Select(i => $"CAST(channel_{i} AS DOUBLE) AS channel_{i}"));

        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            COPY (
                SELECT date, {selectColumns}
                FROM (VALUES
                    {string.Join(",\n                    ", rows)}
                ) AS t(date, {valueColumnNames})
                ORDER BY date ASC
            ) TO '{ParquetMarketDataProvider.EscapeDuckDbPath(exportPath)}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();

        return exportPath;
    }
}
