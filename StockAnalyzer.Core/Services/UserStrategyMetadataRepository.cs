using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services;

public sealed record UserStrategyItem
{
    public decimal? Long { get; init; }
    public decimal? ExitLong { get; init; }
    public decimal? StopLossLong { get; init; }
    public decimal? Short { get; init; }
    public decimal? ExitShort { get; init; }
    public decimal? StopLossShort { get; init; }

    // Signal Flags
    public bool? IsLong { get; init; }
    public bool? IsTPLong { get; init; }
    public bool? IsSLLong { get; init; }
    public bool? IsShort { get; init; }
    public bool? IsTPShort { get; init; }
    public bool? IsSLShort { get; init; }

    // Legacy support for older JSON persistence
    public decimal? EntryPrice { get; init; }
    public decimal? TargetPrice { get; init; }
    public decimal? StopLoss { get; init; }

    public string? Notes { get; init; }
    public string? Reminder { get; init; }

    public decimal? EffectiveLong => Long ?? EntryPrice;
    public decimal? EffectiveExitLong => ExitLong ?? TargetPrice;
    public decimal? EffectiveStopLossLong => StopLossLong ?? StopLoss;
}

/// <summary>
/// Manages user strategy metadata (Long, ExitLong, StopLossLong, Short, ExitShort, StopLossShort, Notes, Signal Flags)
/// persisting directly to Data/Metadata/{ticker}.meta.parquet.
/// </summary>
public class UserStrategyMetadataRepository
{
    private static readonly Lazy<UserStrategyMetadataRepository> _instance = new(() => new UserStrategyMetadataRepository());
    public static UserStrategyMetadataRepository Instance => _instance.Value;

    /// <summary>
    /// The app's real <see cref="ParquetMarketDataProvider"/>, explicitly wired once at composition-root
    /// startup (see App.axaml.cs) - the same "static field set from Services.GetRequiredService" pattern
    /// already used for <see cref="StockAnalyzer.Core.Services.IDispatcherService"/> elsewhere. Replaces
    /// the previous <c>AppDomain.CurrentDomain.SetData/GetData("MarketDataProvider")</c> self-registration,
    /// which let ANY <see cref="ParquetMarketDataProvider"/> constructed anywhere (including in unrelated
    /// unit tests) silently become reachable here, since that registration happened as a side effect of
    /// construction rather than an explicit app-startup wiring step.
    /// </summary>
    public static ParquetMarketDataProvider? MarketDataProvider { get; set; }

    private readonly ConcurrentDictionary<string, UserStrategyItem> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after <see cref="SaveStrategy(string, decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?, bool?, bool?, bool?, bool?, bool?, bool?, string?)"/>
    /// updates the in-memory cache for a ticker, so UI already displaying that ticker (e.g. the
    /// Tickers grid's Notes column) can refresh without waiting for a restart. May fire on a
    /// background thread - subscribers must marshal to the UI thread themselves.</summary>
    public event Action<string>? StrategyChanged;

    public UserStrategyItem? GetCachedStrategy(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return null;
        return _cache.TryGetValue(ticker, out var item) ? item : null;
    }

    public UserStrategyItem? GetStrategy(string ticker)
    {
        var cached = GetCachedStrategy(ticker);
        if (cached != null) return cached;

        try
        {
            var provider = MarketDataProvider;
            if (provider != null)
            {
                var metaTask = provider.GetMetadataAsync(ticker);
                if (metaTask.IsCompleted)
                {
                    if (TryCreateItemFromMetadata(metaTask.Result, out var item))
                    {
                        _cache[ticker] = item;
                        return item;
                    }
                }
                else
                {
                    // Asynchronously fetch in background to avoid blocking the calling UI thread
                    _ = Task.Run(async () => await provider.GetMetadataAsync(ticker));
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load strategy from Parquet for {ticker}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Like <see cref="GetStrategy"/>, but waits for the persisted metadata to finish loading when the ticker is
    /// not in the in-memory cache yet, so a null result means "this ticker has no stored strategy data" rather than
    /// "not loaded yet". A caller that read-modifies-writes the strategy (<see cref="SaveStrategy(string, decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?, bool?, bool?, bool?, bool?, bool?, bool?, string?)"/>
    /// replaces every field it is given) must use this instead of <see cref="GetStrategy"/>: on an unloaded ticker
    /// <see cref="GetStrategy"/> returns null immediately, and writing that back would erase the stored values.
    /// Without a <see cref="MarketDataProvider"/> it behaves exactly like <see cref="GetStrategy"/>.
    /// </summary>
    /// <exception cref="StrategyLoadFailedException">The stored data exists but could not be read, so "no data" would be
    /// a false answer; the caller must not write.</exception>
    public async Task<UserStrategyItem?> GetStrategyAsync(string ticker, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return null;

        var cached = GetCachedStrategy(ticker);
        if (cached != null) return cached;

        var provider = MarketDataProvider;
        if (provider == null) return GetStrategy(ticker);

        ct.ThrowIfCancellationRequested();
        var load = await provider.TryLoadStoredMetadataAsync(ticker).ConfigureAwait(false);
        if (load.ReadFailed) throw new StrategyLoadFailedException(ticker);

        // The load registers what it found with UserStrategyMetadataRepository.Instance; this instance may be another one.
        cached = GetCachedStrategy(ticker);
        if (cached != null) return cached;
        if (!TryCreateItemFromMetadata(load.Meta, out var item)) return null;

        return _cache.GetOrAdd(ticker, item);
    }

    /// <summary>
    /// The strategy item a persisted metadata row stands for, or false when the row holds no strategy data at all
    /// (every strategy field null). The single field mapping shared by <see cref="GetStrategy"/> and
    /// <see cref="GetStrategyAsync"/>.
    /// </summary>
    private static bool TryCreateItemFromMetadata(StockAnalyzer.Core.Models.Portfolio.TickerMetadata meta, out UserStrategyItem item)
    {
        if (meta.Long != null || meta.ExitLong != null || meta.StopLossLong != null ||
            meta.Short != null || meta.ExitShort != null || meta.StopLossShort != null || meta.Notes != null || meta.Reminder != null ||
            meta.IsLong != null || meta.IsTPLong != null || meta.IsSLLong != null ||
            meta.IsShort != null || meta.IsTPShort != null || meta.IsSLShort != null)
        {
            item = new UserStrategyItem
            {
                Long = meta.Long,
                ExitLong = meta.ExitLong,
                StopLossLong = meta.StopLossLong,
                Short = meta.Short,
                ExitShort = meta.ExitShort,
                StopLossShort = meta.StopLossShort,
                IsLong = meta.IsLong,
                IsTPLong = meta.IsTPLong,
                IsSLLong = meta.IsSLLong,
                IsShort = meta.IsShort,
                IsTPShort = meta.IsTPShort,
                IsSLShort = meta.IsSLShort,
                EntryPrice = meta.Long ?? meta.EntryPrice,
                TargetPrice = meta.ExitLong ?? meta.TargetPrice,
                StopLoss = meta.StopLossLong ?? meta.StopLoss,
                Notes = meta.Notes,
                Reminder = meta.Reminder
            };
            return true;
        }

        item = null!;
        return false;
    }

    /// <summary>
    /// Sets only <see cref="UserStrategyItem.Notes"/> of the ticker's strategy and leaves every other field as it is:
    /// the stored strategy is loaded first (<see cref="GetStrategyAsync"/>), the in-memory update is one atomic
    /// AddOrUpdate on the cache (a concurrent edit of another field is merged, never overwritten) and the persisted
    /// write goes through the same ordered, latest-wins queue as <c>SaveStrategy</c>. Raises <see cref="StrategyChanged"/>.
    /// </summary>
    /// <exception cref="StrategyLoadFailedException">The stored strategy could not be read; nothing is changed.</exception>
    public async Task SetNotesAsync(string ticker, string? notes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;

        await GetStrategyAsync(ticker, ct).ConfigureAwait(false);

        var updated = _cache.AddOrUpdate(
            ticker,
            _ => new UserStrategyItem { Notes = notes },
            (_, current) => current with { Notes = notes });
        // Persist before notifying: a subscriber that throws must not leave the in-memory value ahead of the disk.
        SchedulePersist(ticker, updated);
        StrategyChanged?.Invoke(ticker);
    }

    /// <summary>Latest-wins persistence slot of one ticker (see <see cref="SchedulePersist"/>).</summary>
    private sealed class PersistSlot
    {
        public UserStrategyItem? Pending;
        public bool Running;
    }

    private readonly ConcurrentDictionary<string, PersistSlot> _persistSlots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Persists <paramref name="item"/> in the background, one write at a time per ticker and in call order: while a
    /// write runs, newer items replace the pending one, so the file always ends with the newest item. Independent
    /// fire-and-forget writes used to race for the DuckDB lock, so an older item could land after a newer one.
    /// </summary>
    private void SchedulePersist(string ticker, UserStrategyItem item)
    {
        var slot = _persistSlots.GetOrAdd(ticker, _ => new PersistSlot());
        bool startDrain;
        lock (slot)
        {
            slot.Pending = item;
            startDrain = !slot.Running;
            slot.Running = true;
        }

        if (startDrain)
        {
            _ = Task.Run(() => DrainPersistAsync(ticker, slot));
        }
    }

    private async Task DrainPersistAsync(string ticker, PersistSlot slot)
    {
        while (true)
        {
            UserStrategyItem? item;
            lock (slot)
            {
                item = slot.Pending;
                slot.Pending = null;
                if (item is null)
                {
                    slot.Running = false;
                    return;
                }
            }

            try
            {
                var provider = MarketDataProvider;
                if (provider != null)
                {
                    await provider.SaveStrategyMetadataAsync(ticker, item.Long, item.ExitLong, item.StopLossLong, item.Short, item.ExitShort, item.StopLossShort, item.Notes,
                        item.IsLong, item.IsTPLong, item.IsSLLong, item.IsShort, item.IsTPShort, item.IsSLShort, item.Reminder);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist strategy to Parquet for {ticker}: {ex.Message}");
            }
        }
    }

    public void SaveStrategy(string ticker, decimal? longVal, decimal? exitLong, decimal? stopLossLong, decimal? shortVal, decimal? exitShort, decimal? stopLossShort, string? notes,
        bool? isLong = null, bool? isTPLong = null, bool? isSLLong = null, bool? isShort = null, bool? isTPShort = null, bool? isSLShort = null, string? reminder = null)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;

        var item = new UserStrategyItem
        {
            Long = longVal,
            ExitLong = exitLong,
            StopLossLong = stopLossLong,
            Short = shortVal,
            ExitShort = exitShort,
            StopLossShort = stopLossShort,
            IsLong = isLong,
            IsTPLong = isTPLong,
            IsSLLong = isSLLong,
            IsShort = isShort,
            IsTPShort = isTPShort,
            IsSLShort = isSLShort,
            EntryPrice = longVal,
            TargetPrice = exitLong,
            StopLoss = stopLossLong,
            Notes = notes,
            Reminder = reminder
        };
        _cache[ticker] = item;
        StrategyChanged?.Invoke(ticker);

        // Persist into Data/Metadata/{ticker}.meta.parquet (ordered per ticker, latest item wins)
        SchedulePersist(ticker, item);
    }

    public void SaveStrategy(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? notes)
    {
        SaveStrategy(ticker, entryPrice, targetPrice, stopLoss, null, null, null, notes);
    }

    public void RegisterLoadedStrategy(string ticker, decimal? longVal, decimal? exitLong, decimal? stopLossLong, decimal? shortVal, decimal? exitShort, decimal? stopLossShort, string? notes,
        bool? isLong = null, bool? isTPLong = null, bool? isSLLong = null, bool? isShort = null, bool? isTPShort = null, bool? isSLShort = null, string? reminder = null)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;
        _cache.AddOrUpdate(ticker,
            _ => new UserStrategyItem
            {
                Long = longVal,
                ExitLong = exitLong,
                StopLossLong = stopLossLong,
                Short = shortVal,
                ExitShort = exitShort,
                StopLossShort = stopLossShort,
                IsLong = isLong,
                IsTPLong = isTPLong,
                IsSLLong = isSLLong,
                IsShort = isShort,
                IsTPShort = isTPShort,
                IsSLShort = isSLShort,
                EntryPrice = longVal,
                TargetPrice = exitLong,
                StopLoss = stopLossLong,
                Notes = notes,
                Reminder = reminder
            },
            (_, existing) => new UserStrategyItem
            {
                Long = longVal ?? existing.Long ?? existing.EntryPrice,
                ExitLong = exitLong ?? existing.ExitLong ?? existing.TargetPrice,
                StopLossLong = stopLossLong ?? existing.StopLossLong ?? existing.StopLoss,
                Short = shortVal ?? existing.Short,
                ExitShort = exitShort ?? existing.ExitShort,
                StopLossShort = stopLossShort ?? existing.StopLossShort,
                IsLong = isLong ?? existing.IsLong,
                IsTPLong = isTPLong ?? existing.IsTPLong,
                IsSLLong = isSLLong ?? existing.IsSLLong,
                IsShort = isShort ?? existing.IsShort,
                IsTPShort = isTPShort ?? existing.IsTPShort,
                IsSLShort = isSLShort ?? existing.IsSLShort,
                EntryPrice = longVal ?? existing.EntryPrice ?? existing.Long,
                TargetPrice = exitLong ?? existing.TargetPrice ?? existing.ExitLong,
                StopLoss = stopLossLong ?? existing.StopLoss ?? existing.StopLossLong,
                Notes = notes ?? existing.Notes,
                Reminder = reminder ?? existing.Reminder
            });
    }

    public void RegisterLoadedStrategy(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? notes)
    {
        RegisterLoadedStrategy(ticker, entryPrice, targetPrice, stopLoss, null, null, null, notes);
    }

    private readonly ConcurrentDictionary<string, List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>> _signalBundlesCache = new(StringComparer.OrdinalIgnoreCase);

    public List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition> GetSignalBundles(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return new List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>();
        if (_signalBundlesCache.TryGetValue(ticker, out var cached)) return cached;

        try
        {
            string dir = Common.PathDiscovery.ResolveDataPath(null, "Data/Metadata");
            string path = Path.Combine(dir, $"{ticker}.signals.json");
            if (!File.Exists(path))
            {
                string fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Metadata", $"{ticker}.signals.json");
                if (File.Exists(fallbackPath)) path = fallbackPath;
            }
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>>(json);
                if (list != null)
                {
                    foreach (var bundle in list)
                    {
                        if (bundle.Conditions != null)
                        {
                            foreach (var cond in bundle.Conditions)
                            {
                                if (cond.LeftHand != null) NormalizeParameters(cond.LeftHand.Parameters);
                                if (cond.RightHand != null) NormalizeParameters(cond.RightHand.Parameters);
                            }
                        }
                    }
                    _signalBundlesCache[ticker] = list;
                    return list;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load signal bundles for {ticker}: {ex.Message}");
        }

        return new List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>();
    }

    private static void NormalizeParameters(Dictionary<string, object>? parameters)
    {
        if (parameters == null) return;
        var keys = parameters.Keys.ToList();
        foreach (var key in keys)
        {
            if (parameters[key] is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    if (element.TryGetInt32(out int intVal))
                        parameters[key] = intVal;
                    else if (element.TryGetInt64(out long longVal))
                        parameters[key] = longVal;
                    else if (element.TryGetDouble(out double dblVal))
                        parameters[key] = dblVal;
                    else if (element.TryGetDecimal(out decimal decVal))
                        parameters[key] = decVal;
                }
                else if (element.ValueKind == JsonValueKind.String)
                {
                    parameters[key] = element.GetString() ?? string.Empty;
                }
                else if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
                {
                    parameters[key] = element.GetBoolean();
                }
            }
        }
    }

    public void SaveSignalBundles(string ticker, IEnumerable<StockAnalyzer.Core.Models.Screener.BundledSignalCondition> bundles)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return;
        var list = bundles?.ToList() ?? new List<StockAnalyzer.Core.Models.Screener.BundledSignalCondition>();
        _signalBundlesCache[ticker] = list;

        Task.Run(() =>
        {
            try
            {
                string dir = Common.PathDiscovery.ResolveDataPath(null, "Data/Metadata");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"{ticker}.signals.json");
                string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = path + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save signal bundles for {ticker}: {ex.Message}");
            }
        });
    }
}
