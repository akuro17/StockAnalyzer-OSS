using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Helpers;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// File-backed IChartDrawingRepository. One JSON file per ticker+timeframe under
/// Data\Drawings\{ticker}.{timeframe}.json, mirroring the atomic-write pattern used by
/// StockAnalyzer.Core.Services.UserStrategyMetadataRepository (.tmp write + File.Replace/Move).
///
/// This repository is registered as a DI singleton and is shared by every ChartViewModel
/// instance (e.g. multiple open chart tabs). Two tabs can independently target the same
/// ticker+timeframe file at the same time, so all file access for a given path is serialized
/// via a per-path lock to prevent concurrent-write IOExceptions ("file is being used by another
/// process") that were observed to silently drop saves.
/// </summary>
public class ChartDrawingRepository : IChartDrawingRepository
{
    // Windows reserved device names (case-insensitive); a ticker like "CON" or "NUL" would pass
    // ticker-format validation but produce an unusable/reserved file name.
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private readonly ILogger<ChartDrawingRepository> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    public ChartDrawingRepository(ILogger<ChartDrawingRepository>? logger = null)
    {
        _logger = logger ?? NullLogger<ChartDrawingRepository>.Instance;
    }

    private SemaphoreSlim GetLock(string path) => _fileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    public Dictionary<ChartDrawingContextType, List<IChartObject>>? Load(string ticker, TimeframeType timeframe)
    {
        if (!TryResolvePath(ticker, timeframe, out var path)) return null;

        var fileLock = GetLock(path);
        fileLock.Wait();
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<ChartDrawingContextType, List<IChartObject>>>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chart drawings file corrupted or unreadable for {Ticker}/{Timeframe}, falling back to empty", ticker, timeframe);

            // Preserve the corrupted file for diagnosis (mirrors ChartSettingsManager's
            // corruption-recovery pattern) instead of silently discarding it on next save.
            try
            {
                var backupPath = path + ".corrupted";
                File.Copy(path, backupPath, overwrite: true);
            }
            catch { /* best-effort; a failed backup must not prevent falling back to empty */ }

            return null;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public void Save(string ticker, TimeframeType timeframe, Dictionary<ChartDrawingContextType, List<IChartObject>> data)
    {
        _ = SaveAsync(ticker, timeframe, data);
    }

    public Task SaveAsync(string ticker, TimeframeType timeframe, Dictionary<ChartDrawingContextType, List<IChartObject>> data)
    {
        if (!TryResolvePath(ticker, timeframe, out var path)) return Task.CompletedTask;

        return Task.Run(() =>
        {
            var fileLock = GetLock(path);
            fileLock.Wait();
            try
            {
                // Don't create a file for a ticker/timeframe that has never had anything drawn on
                // it (e.g. the app's default startup symbol, or a symbol briefly passed through).
                // If a file already exists, still overwrite it so a user clearing their last
                // drawing is correctly reflected rather than resurrecting stale data on next load.
                bool isEmpty = data.Values.All(list => list.Count == 0);
                if (isEmpty && !File.Exists(path)) return;

                var dir = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(data, SerializerOptions);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tempPath, path, null);
                    }
                    catch (IOException)
                    {
                        // File.Replace can fail on drive roots / virtual volume boundaries
                        // (e.g. Y:\); fall back to File.Move so the save is never silently lost.
                        File.Move(tempPath, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(tempPath, path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save chart drawings for {Ticker}/{Timeframe}", ticker, timeframe);
            }
            finally
            {
                fileLock.Release();
            }
        });
    }

    /// <summary>
    /// Resolves the file path for a ticker+timeframe, rejecting tickers that would produce an
    /// unsafe/invalid file name. Defense-in-depth: ChartViewModel.OnSymbolChanged already
    /// validates Symbol against a strict regex before it ever reaches this repository, but this
    /// repository has its own public interface and must not assume every caller does the same.
    /// </summary>
    private bool TryResolvePath(string ticker, TimeframeType timeframe, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(ticker)) return false;

        var normalized = TickerHelper.NormalizeTicker(ticker);
        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            _logger.LogWarning("Refusing to resolve chart drawings path: ticker '{Ticker}' normalizes to an invalid file name", ticker);
            return false;
        }

        if (ReservedFileNames.Contains(normalized))
        {
            _logger.LogWarning("Refusing to resolve chart drawings path: ticker '{Ticker}' normalizes to a Windows-reserved device name", ticker);
            return false;
        }

        var dir = PathDiscovery.ResolveDataPath(null, "Data/Drawings");
        path = Path.Combine(dir, $"{normalized}.{timeframe}.json");
        return true;
    }
}
