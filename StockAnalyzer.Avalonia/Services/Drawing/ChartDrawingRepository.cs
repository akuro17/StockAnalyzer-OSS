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
using StockAnalyzer.Core.Models.Drawing;

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

    /// <summary>
    /// Per-file save bookkeeping. Revision is bumped under Gate when a save is accepted, and LastWrite chains
    /// the background writes so they reach disk in acceptance order and a Load can wait for them.
    /// </summary>
    private sealed class PathState
    {
        public readonly object Gate = new();
        public long Revision;
        public Task LastWrite = Task.CompletedTask;
    }

    /// <summary>Suffix of the sibling file that preserves a save payload rejected because its base revision was stale.</summary>
    public const string ConflictFileSuffix = ".conflict";

    private readonly ConcurrentDictionary<string, PathState> _pathStates = new();
    private readonly ConcurrentDictionary<(string Ticker, TimeframeType Timeframe), string> _resolvedPaths = new();

    private PathState GetState(string path) => _pathStates.GetOrAdd(path, static _ => new PathState());

    public long GetRevision(string ticker, TimeframeType timeframe)
    {
        if (!TryResolvePath(ticker, timeframe, out var path)) return 0;
        var state = GetState(path);
        lock (state.Gate) return state.Revision;
    }

    public ChartDrawingPayload? LoadPayload(string ticker, TimeframeType timeframe, out long revision)
    {
        revision = 0;
        if (!TryResolvePath(ticker, timeframe, out var path)) return null;

        var state = GetState(path);
        Task pendingWrites;
        lock (state.Gate)
        {
            revision = state.Revision;
            pendingWrites = state.LastWrite;
        }

        // Accepted saves are written by background tasks; wait for them so the read reflects revision.
        // Write failures are logged inside the write and never fault this task. The chain is built with
        // TaskScheduler.Default and plain synchronous actions (QueueWrite), so it captures no SynchronizationContext
        // and blocking a UI thread here cannot deadlock.
        pendingWrites.GetAwaiter().GetResult();
        return ReadPayloadCore(ticker, timeframe);
    }

    public ChartDrawingPayload? LoadPayload(string ticker, TimeframeType timeframe) =>
        LoadPayload(ticker, timeframe, out _);

    private ChartDrawingPayload? ReadPayloadCore(string ticker, TimeframeType timeframe)
    {
        if (!TryResolvePath(ticker, timeframe, out var path)) return null;

        var fileLock = GetLock(path);
        fileLock.Wait();
        try
        {
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 1. V2 DrawingDocumentState schema check
                if (root.TryGetProperty("version", out var verEl) && verEl.GetInt32() == DrawingDocumentState.CurrentSchemaVersion)
                {
                    var docState = DrawingDocumentCodec.DeserializeFromJson(json);
                    var mat = DrawingDocumentCodec.Materialize(docState);
                    mat.DetachObjects();

                    // The payload addresses link members by position because runtime ids are regenerated on every load.
                    var linkGroups = new Dictionary<ChartDrawingContextType, List<DrawingLinkGroupIndexRecord>>();
                    foreach (var (context, records) in mat.LinkGroups)
                    {
                        if (!mat.Objects.TryGetValue(context, out var contextObjects)) continue;
                        var indexRecords = ObjectLinkGroupRegistry.ToIndexRecords(records, mat.PersistentToRuntimeIds, contextObjects);
                        if (indexRecords.Count > 0) linkGroups[context] = new List<DrawingLinkGroupIndexRecord>(indexRecords);
                    }

                    return new ChartDrawingPayload
                    {
                        SchemaVersion = DrawingDocumentState.CurrentSchemaVersion,
                        Objects = new Dictionary<ChartDrawingContextType, List<IChartObject>>(mat.Objects),
                        Layers = new Dictionary<ChartDrawingContextType, List<DrawingLayerRecord>>(mat.Layers),
                        LinkGroups = linkGroups
                    };
                }

                // 2. ChartDrawingPayload container format check
                if (root.TryGetProperty("objects", out _) || root.TryGetProperty("Objects", out _))
                {
                    var payload = JsonSerializer.Deserialize<ChartDrawingPayload>(json, SerializerOptions);
                    return payload;
                }

                // 3. Legacy Dictionary<ChartDrawingContextType, List<IChartObject>> format
                var dict = JsonSerializer.Deserialize<Dictionary<ChartDrawingContextType, List<IChartObject>>>(json, SerializerOptions);
                if (dict != null)
                {
                    return new ChartDrawingPayload
                    {
                        SchemaVersion = 0,
                        Objects = dict,
                        Layers = new()
                    };
                }

                return null;
            }
            catch (JsonException)
            {
                // Fallback attempt as legacy dictionary
                var dict = JsonSerializer.Deserialize<Dictionary<ChartDrawingContextType, List<IChartObject>>>(json, SerializerOptions);
                return dict != null ? new ChartDrawingPayload { SchemaVersion = 0, Objects = dict, Layers = new() } : null;
            }
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

    public Dictionary<ChartDrawingContextType, List<IChartObject>>? Load(string ticker, TimeframeType timeframe)
    {
        var payload = LoadPayload(ticker, timeframe);
        return payload?.Objects;
    }

    public void Save(string ticker, TimeframeType timeframe, Dictionary<ChartDrawingContextType, List<IChartObject>> data)
    {
        SavePayload(ticker, timeframe, new ChartDrawingPayload { Objects = data });
    }

    public Task SaveAsync(string ticker, TimeframeType timeframe, Dictionary<ChartDrawingContextType, List<IChartObject>> data)
    {
        return SavePayloadAsync(ticker, timeframe, new ChartDrawingPayload { Objects = data });
    }

    public void SavePayload(string ticker, TimeframeType timeframe, ChartDrawingPayload payload)
    {
        _ = SavePayloadAsync(ticker, timeframe, payload);
    }

    public Task SavePayloadAsync(string ticker, TimeframeType timeframe, ChartDrawingPayload payload)
    {
        if (!TryResolvePath(ticker, timeframe, out var path)) return Task.CompletedTask;

        var state = GetState(path);
        lock (state.Gate)
        {
            state.Revision++;
            return state.LastWrite = QueueWrite(state.LastWrite, () => WritePayload(path, ticker, timeframe, payload));
        }
    }

    public DrawingSaveOutcome SavePayloadIfCurrent(string ticker, TimeframeType timeframe, ChartDrawingPayload payload, long expectedRevision)
    {
        if (!TryResolvePath(ticker, timeframe, out var path))
            return new DrawingSaveOutcome(false, expectedRevision, Task.CompletedTask);

        var state = GetState(path);
        lock (state.Gate)
        {
            if (state.Revision != expectedRevision)
            {
                _logger.LogWarning(
                    "Rejected stale chart drawings save for {Ticker}/{Timeframe}: expected revision {Expected}, current {Current}. Preserving rejected payload as {ConflictSuffix}.",
                    ticker, timeframe, expectedRevision, state.Revision, ConflictFileSuffix);
                var backup = QueueWrite(state.LastWrite, () => WritePayload(path + ConflictFileSuffix, ticker, timeframe, payload, skipEmptyGuard: true));
                state.LastWrite = backup;
                return new DrawingSaveOutcome(false, state.Revision, backup);
            }

            state.Revision++;
            var write = QueueWrite(state.LastWrite, () => WritePayload(path, ticker, timeframe, payload));
            state.LastWrite = write;
            return new DrawingSaveOutcome(true, state.Revision, write);
        }
    }

    private static Task QueueWrite(Task previous, Action write) =>
        previous.ContinueWith(_ => write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void WritePayload(string path, string ticker, TimeframeType timeframe, ChartDrawingPayload payload, bool skipEmptyGuard = false)
    {
        var fileLock = GetLock(path);
        fileLock.Wait();
        try
        {
            // Don't create a file for a ticker/timeframe that has never had anything drawn on
            // it (e.g. the app's default startup symbol, or a symbol briefly passed through).
            // If a file already exists, still overwrite it so a user clearing their last
            // drawing is correctly reflected rather than resurrecting stale data on next load.
            bool isObjectsEmpty = payload.Objects == null || payload.Objects.Values.All(list => list.Count == 0);
            bool isLayersEmpty = payload.Layers == null || payload.Layers.Values.All(list => list.Count == 0 || (list.Count == 1 && list[0].Name == "Layer 1" && list[0].IsVisible && !list[0].IsEditLocked));
            if (!skipEmptyGuard && isObjectsEmpty && isLayersEmpty && !File.Exists(path)) return;

            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(payload, SerializerOptions);
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
            _logger.LogError(ex, "Failed to save chart drawings payload for {Ticker}/{Timeframe}", ticker, timeframe);
        }
        finally
        {
            fileLock.Release();
        }
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
        // Keyed by the normalized ticker so spelling variants share one entry; the checks below run once per key
        // (GetRevision is called on every pointer press).
        if (_resolvedPaths.TryGetValue((normalized, timeframe), out var cached))
        {
            path = cached;
            return true;
        }

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
        _resolvedPaths[(normalized, timeframe)] = path;
        return true;
    }

    /// <summary>
    /// Internal accessor for V2 DrawingDocumentRepository to resolve drawing document file paths
    /// using the single source of truth path resolution rules without duplication.
    /// </summary>
    internal bool TryResolveDocumentPath(string ticker, TimeframeType timeframe, out string path)
        => TryResolvePath(ticker, timeframe, out path);
}
