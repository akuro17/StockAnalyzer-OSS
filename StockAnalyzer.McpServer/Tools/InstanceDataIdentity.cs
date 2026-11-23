using System.Globalization;
using System.Text.Json;
using StockAnalyzer.Core.Discussion;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

internal sealed record InstanceIdentitySnapshot(
    string DataSetId,
    int TickerCount,
    DateOnly? LatestBarDate);

/// <summary>
/// Computes dataset identity (<c>dataSetId</c>), registered <c>tickerCount</c>, and <c>latestBarDate</c>
/// for <c>sa_get_settings</c> and <c>sa_get_symbol_coverage</c> without exposing filesystem paths.
/// </summary>
internal sealed class InstanceDataIdentity
{
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly string? _configuredDailyDataPath;

    public InstanceDataIdentity(IReadOnlyStockAnalyzerGateway gateway, string? configuredDailyDataPath)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _configuredDailyDataPath = configuredDailyDataPath;
    }

    public string ComputeDataSetId()
        => DiscussionSharingKey.DataHash8(DiscussionSharingKey.ResolveDataFolder(_configuredDailyDataPath));

    public async Task<InstanceIdentitySnapshot> GetForSettingsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tickers = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        DateOnly? latest = null;
        for (int i = 0; i < tickers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset? updated = await _gateway.GetTimeSeriesLastUpdatedAsync(tickers[i], cancellationToken).ConfigureAwait(false);
            if (updated is { } ts)
            {
                DateOnly candidate = DateOnly.FromDateTime(ts.DateTime);
                if (latest is null || candidate > latest.Value)
                {
                    latest = candidate;
                }
            }
        }

        return new InstanceIdentitySnapshot(ComputeDataSetId(), tickers.Count, latest);
    }

    public async Task<InstanceIdentitySnapshot> GetForCoverageAsync(
        IReadOnlyList<SymbolCoverage> results,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tickers = await _gateway.GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        DateOnly? latest = null;
        for (int i = 0; i < results.Count; i++)
        {
            SymbolCoverage coverage = results[i];
            if (!coverage.Found)
            {
                continue;
            }

            DateOnly? candidate = null;
            for (int j = 0; j < coverage.TimeFrames.Count; j++)
            {
                TimeFrameCoverage tf = coverage.TimeFrames[j];
                if (tf.TimeFrame == TimeFrame.D1)
                {
                    candidate = DateOnly.FromDateTime(tf.LatestTimestamp);
                    break;
                }
            }

            if (candidate is null && coverage.TimeSeriesLastUpdated is { } updated)
            {
                candidate = DateOnly.FromDateTime(updated.DateTime);
            }

            if (candidate is { } date && (latest is null || date > latest.Value))
            {
                latest = date;
            }
        }

        return new InstanceIdentitySnapshot(ComputeDataSetId(), tickers.Count, latest);
    }

    public static void Write(Utf8JsonWriter writer, InstanceIdentitySnapshot snapshot)
    {
        writer.WriteString("dataSetId", snapshot.DataSetId);
        writer.WriteNumber("tickerCount", snapshot.TickerCount);
        if (snapshot.LatestBarDate is { } date)
        {
            writer.WriteString("latestBarDate", date.ToString(ToolJson.DateFormat, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull("latestBarDate");
        }
    }
}
