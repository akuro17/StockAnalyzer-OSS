using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Authoritative single producer of Weekly (W1) and Monthly (MN1) Parquet files from Daily (D1) Parquet.
/// Applies canonical date stamping (PeriodStart) and guarantees atomic replacement and read-back verification.
/// </summary>
public sealed class TimeFrameParquetGenerator : ITimeFrameParquetGenerator
{
    private readonly DuckDBConnectionManager _dbManager;
    private readonly MarketDataSettings _settings;
    private readonly ILogger<TimeFrameParquetGenerator> _logger;

    public TimeFrameParquetGenerator(
        DuckDBConnectionManager dbManager,
        IOptions<MarketDataSettings> settings,
        ILogger<TimeFrameParquetGenerator>? logger = null)
    {
        _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
        _settings = settings?.Value ?? new MarketDataSettings();
        _logger = logger ?? NullLogger<TimeFrameParquetGenerator>.Instance;
    }

    /// <inheritdoc/>
    public async Task<int> GenerateAsync(string? symbol = null, CancellationToken cancellationToken = default)
    {
        var dailyFolder = ResolveBasePath(TimeFrame.D1);
        if (!Directory.Exists(dailyFolder))
        {
            _logger.LogWarning("Daily data directory not found at {DailyFolder}", dailyFolder);
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            bool ok = await GenerateSymbolFromDailyFileAsync(symbol, cancellationToken).ConfigureAwait(false);
            return ok ? 0 : 1;
        }

        var files = Directory.GetFiles(dailyFolder, "*.parquet");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        int failedCount = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sym = Path.GetFileNameWithoutExtension(file);
            bool ok = await GenerateSymbolFromDailyFileAsync(sym, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                failedCount++;
            }
        }

        if (failedCount > 0)
        {
            _logger.LogError("Weekly/monthly generation FAILED for {FailedCount} of {TotalCount} tickers.", failedCount, files.Length);
        }
        else
        {
            _logger.LogInformation("Weekly/monthly generation completed successfully for all {TotalCount} tickers.", files.Length);
        }

        return failedCount;
    }

    private async Task<bool> GenerateSymbolFromDailyFileAsync(string symbol, CancellationToken cancellationToken)
    {
        if (!AttestedFileNames.IsPlain(symbol))
        {
            _logger.LogError("Invalid symbol '{Symbol}' for timeframe generation", symbol);
            return false;
        }

        var dailyFolder = ResolveBasePath(TimeFrame.D1);
        var dailyPath = Path.Combine(dailyFolder, $"{symbol}.parquet");
        if (!File.Exists(dailyPath))
        {
            _logger.LogError("Daily data not found for {Symbol}", symbol);
            return false;
        }

        var dailyBars = new List<CandleData>();
        try
        {
            using (await _dbManager.AcquireLockAsync("TimeFrameParquetGenerator_ReadDaily", cancellationToken).ConfigureAwait(false))
            {
                if (_dbManager.GetConnection() is not DbConnection dbConn)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                using var cmd = dbConn.CreateCommand();
                cmd.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{FormatDuckDbPath(dailyPath)}') WHERE date IS NOT NULL AND close IS NOT NULL AND isfinite(close) AND close > 0 ORDER BY date ASC";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(4)) continue;

                    var dt = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
                    var closeObj = reader.GetValue(4);
                    if (closeObj is float cf && (!float.IsFinite(cf) || cf <= 0)) continue;
                    if (closeObj is double cd && (!double.IsFinite(cd) || cd <= 0)) continue;
                    var close = Convert.ToDecimal(closeObj);
                    if (close <= 0m) continue;

                    decimal open = close;
                    if (!reader.IsDBNull(1))
                    {
                        var openObj = reader.GetValue(1);
                        if (openObj is float of && float.IsFinite(of) && of > 0) open = Convert.ToDecimal(of);
                        else if (openObj is double od && double.IsFinite(od) && od > 0) open = Convert.ToDecimal(od);
                        else if (openObj is decimal om && om > 0m) open = om;
                    }

                    decimal high;
                    if (!reader.IsDBNull(2))
                    {
                        var highObj = reader.GetValue(2);
                        if (highObj is float hf && float.IsFinite(hf) && hf > 0) high = Convert.ToDecimal(hf);
                        else if (highObj is double hd && double.IsFinite(hd) && hd > 0) high = Convert.ToDecimal(hd);
                        else if (highObj is decimal hm && hm > 0m) high = hm;
                        else high = Math.Max(open, close);
                    }
                    else
                    {
                        high = Math.Max(open, close);
                    }

                    decimal low;
                    if (!reader.IsDBNull(3))
                    {
                        var lowObj = reader.GetValue(3);
                        if (lowObj is float lf && float.IsFinite(lf) && lf > 0) low = Convert.ToDecimal(lf);
                        else if (lowObj is double ld && double.IsFinite(ld) && ld > 0) low = Convert.ToDecimal(ld);
                        else if (lowObj is decimal lm && lm > 0m) low = lm;
                        else low = Math.Min(open, close);
                    }
                    else
                    {
                        low = Math.Min(open, close);
                    }

                    high = Math.Max(high, Math.Max(open, close));
                    low = Math.Min(low, Math.Min(open, close));
                    var volume = reader.IsDBNull(5) ? 0L : Math.Max(0L, Convert.ToInt64(reader.GetValue(5)));

                    dailyBars.Add(new CandleData(dt, open, high, low, close, volume));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read daily Parquet for {Symbol}", symbol);
            return false;
        }

        if (dailyBars.Count == 0)
        {
            _logger.LogError("{Symbol}: the daily file holds no usable bars; the weekly and monthly files were not changed", symbol);
            return false;
        }

        return await GenerateForSymbolAsync(symbol, dailyBars, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> GenerateForSymbolAsync(string symbol, IReadOnlyList<CandleData> dailyBars, CancellationToken cancellationToken = default)
    {
        if (!AttestedFileNames.IsPlain(symbol))
        {
            _logger.LogError("Invalid symbol '{Symbol}' for timeframe generation", symbol);
            return false;
        }

        if (dailyBars.Count == 0)
        {
            _logger.LogError("{Symbol}: daily bars collection is empty; the weekly and monthly files were not changed", symbol);
            return false;
        }

        var weeklyFolder = ResolveBasePath(TimeFrame.W1);
        var monthlyFolder = ResolveBasePath(TimeFrame.MN1);
        Directory.CreateDirectory(weeklyFolder);
        Directory.CreateDirectory(monthlyFolder);

        var weeklyPath = Path.Combine(weeklyFolder, $"{symbol}.parquet");
        var monthlyPath = Path.Combine(monthlyFolder, $"{symbol}.parquet");
        var uniqueSuffix = Guid.NewGuid().ToString("N");
        var weeklyTmp = Path.Combine(weeklyFolder, $"{symbol}.{uniqueSuffix}.tmp");
        var monthlyTmp = Path.Combine(monthlyFolder, $"{symbol}.{uniqueSuffix}.tmp");

        var weeklyBars = TimeFrameAggregator.AggregateDailyToWeekly(dailyBars);
        var monthlyBars = TimeFrameAggregator.AggregateDailyToMonthly(dailyBars);

        try
        {
            using (await _dbManager.AcquireLockAsync("TimeFrameParquetGenerator_Write", cancellationToken).ConfigureAwait(false))
            {
                if (_dbManager.GetConnection() is not DbConnection dbConn)
                {
                    throw new InvalidOperationException("Connection must be a DbConnection to support async operations.");
                }

                await WriteTimeFrameParquetAsync(dbConn, weeklyBars, TimeFrame.W1, weeklyTmp, cancellationToken).ConfigureAwait(false);
                await WriteTimeFrameParquetAsync(dbConn, monthlyBars, TimeFrame.MN1, monthlyTmp, cancellationToken).ConfigureAwait(false);

                var verifiedWeekly = await VerifyWrittenBarsAsync(dbConn, weeklyTmp, weeklyBars, TimeFrame.W1, cancellationToken).ConfigureAwait(false);
                if (!verifiedWeekly)
                {
                    _logger.LogError("{Symbol}: Weekly Parquet read-back verification failed; files not changed.", symbol);
                    return false;
                }

                var verifiedMonthly = await VerifyWrittenBarsAsync(dbConn, monthlyTmp, monthlyBars, TimeFrame.MN1, cancellationToken).ConfigureAwait(false);
                if (!verifiedMonthly)
                {
                    _logger.LogError("{Symbol}: Monthly Parquet read-back verification failed; files not changed.", symbol);
                    return false;
                }
            }

            ReplaceBothAtomically(weeklyTmp, weeklyPath, monthlyTmp, monthlyPath);

            _logger.LogInformation("{Symbol}: Generated Weekly ({WeeklyCount}) and Monthly ({MonthlyCount}) Parquet files (verified against daily bars).",
                symbol, weeklyBars.Count, monthlyBars.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate timeframes for {Symbol}", symbol);
            return false;
        }
        finally
        {
            try { if (File.Exists(weeklyTmp)) File.Delete(weeklyTmp); } catch { }
            try { if (File.Exists(monthlyTmp)) File.Delete(monthlyTmp); } catch { }
        }
    }

    private static async Task WriteTimeFrameParquetAsync(
        DbConnection connection,
        IReadOnlyList<CandleData> bars,
        TimeFrame timeFrame,
        string tempFilePath,
        CancellationToken ct)
    {
        var tableName = $"temp_gen_{Guid.NewGuid():N}";
        using var createCmd = connection.CreateCommand();
        createCmd.CommandText = $"CREATE TEMPORARY TABLE {tableName} (date DATE, open FLOAT, high FLOAT, low FLOAT, close FLOAT, volume BIGINT);";
        await createCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        try
        {
            const int batchSize = 500;
            for (int i = 0; i < bars.Count; i += batchSize)
            {
                int count = Math.Min(batchSize, bars.Count - i);
                var sb = new StringBuilder();
                sb.Append($"INSERT INTO {tableName} (date, open, high, low, close, volume) VALUES ");
                for (int j = 0; j < count; j++)
                {
                    if (j > 0) sb.Append(',');
                    var bar = bars[i + j];
                    var barDate = TimeFrameAggregator.PeriodStart(bar.Timestamp, timeFrame);
                    sb.Append(string.Create(CultureInfo.InvariantCulture,
                        $"('{barDate:yyyy-MM-dd}',{(float)bar.Open:R},{(float)bar.High:R},{(float)bar.Low:R},{(float)bar.Close:R},{bar.Volume})"));
                }
                sb.Append(';');
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = sb.ToString();
                await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var copyCmd = connection.CreateCommand();
            copyCmd.CommandText = $"COPY (SELECT date, open, high, low, close, volume FROM {tableName} ORDER BY date ASC) TO '{FormatDuckDbPath(tempFilePath)}' (FORMAT PARQUET, COMPRESSION ZSTD);";
            await copyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = $"DROP TABLE IF EXISTS {tableName};";
            await dropCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> VerifyWrittenBarsAsync(
        DbConnection connection,
        string parquetPath,
        IReadOnlyList<CandleData> expectedBars,
        TimeFrame timeFrame,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{FormatDuckDbPath(parquetPath)}') ORDER BY date ASC;";
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        int index = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (index >= expectedBars.Count) return false;
            var expected = expectedBars[index];
            var expectedDate = TimeFrameAggregator.PeriodStart(expected.Timestamp, timeFrame).Date;

            var dt = reader.GetDateTime(0).Date;
            if (dt != expectedDate) return false;

            float open = Convert.ToSingle(reader.GetValue(1));
            float high = Convert.ToSingle(reader.GetValue(2));
            float low = Convert.ToSingle(reader.GetValue(3));
            float close = Convert.ToSingle(reader.GetValue(4));
            long volume = Convert.ToInt64(reader.GetValue(5));

            if (!IsApproximatelyEqual(open, (float)expected.Open) ||
                !IsApproximatelyEqual(high, (float)expected.High) ||
                !IsApproximatelyEqual(low, (float)expected.Low) ||
                !IsApproximatelyEqual(close, (float)expected.Close) ||
                volume != expected.Volume)
            {
                return false;
            }

            index++;
        }

        return index == expectedBars.Count;
    }

    private static bool IsApproximatelyEqual(float actual, float expected)
    {
        float diff = Math.Abs(actual - expected);
        float tol = Math.Max(1e-5f, Math.Abs(expected) * 1e-6f);
        return diff <= tol;
    }

    private static void ReplaceBothAtomically(string weeklyTmp, string weeklyPath, string monthlyTmp, string monthlyPath)
    {
        string? weeklyBak = null;
        try
        {
            if (File.Exists(weeklyPath))
            {
                weeklyBak = weeklyPath + ".bak." + Guid.NewGuid().ToString("N");
                File.Copy(weeklyPath, weeklyBak, overwrite: true);
            }

            AtomicReplaceWithRetry(weeklyTmp, weeklyPath);

            try
            {
                AtomicReplaceWithRetry(monthlyTmp, monthlyPath);
            }
            catch
            {
                if (weeklyBak != null && File.Exists(weeklyBak))
                {
                    try
                    {
                        AtomicReplaceWithRetry(weeklyBak, weeklyPath);
                        weeklyBak = null;
                    }
                    catch
                    {
                        // Best effort rollback
                    }
                }
                throw;
            }
        }
        finally
        {
            if (weeklyBak != null && File.Exists(weeklyBak))
            {
                try { File.Delete(weeklyBak); } catch { }
            }
        }
    }

    private static void AtomicReplaceWithRetry(string tempPath, string targetPath, int maxRetries = 3)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, null);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private string ResolveBasePath(TimeFrame timeFrame)
    {
        var (configuredPath, relativeFallback) = timeFrame switch
        {
            TimeFrame.D1 => (_settings.DailyDataPath, MarketDataSettings.DefaultDailyDataPath),
            TimeFrame.W1 => (_settings.WeeklyDataPath, MarketDataSettings.DefaultWeeklyDataPath),
            TimeFrame.MN1 => (_settings.MonthlyDataPath, MarketDataSettings.DefaultMonthlyDataPath),
            _ => throw new NotSupportedException($"TimeFrame {timeFrame} is not currently supported for direct Parquet loading.")
        };
        return Common.PathDiscovery.ResolveDataPath(configuredPath, relativeFallback, "*.parquet");
    }

    private static string FormatDuckDbPath(string path) => path.Replace('\\', '/').Replace("'", "''");
}
