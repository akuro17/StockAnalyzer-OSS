using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
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

    private readonly ConcurrentDictionary<string, UserStrategyItem> _cache = new(StringComparer.OrdinalIgnoreCase);

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
            var provider = AppDomain.CurrentDomain.GetData("MarketDataProvider") as ParquetMarketDataProvider;
            if (provider != null)
            {
                var metaTask = provider.GetMetadataAsync(ticker);
                if (metaTask.IsCompleted)
                {
                    var meta = metaTask.Result;
                    if (meta.Long != null || meta.ExitLong != null || meta.StopLossLong != null ||
                        meta.Short != null || meta.ExitShort != null || meta.StopLossShort != null || meta.Notes != null ||
                        meta.IsLong != null || meta.IsTPLong != null || meta.IsSLLong != null ||
                        meta.IsShort != null || meta.IsTPShort != null || meta.IsSLShort != null)
                    {
                        var item = new UserStrategyItem
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
                            Notes = meta.Notes
                        };
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

    public void SaveStrategy(string ticker, decimal? longVal, decimal? exitLong, decimal? stopLossLong, decimal? shortVal, decimal? exitShort, decimal? stopLossShort, string? notes,
        bool? isLong = null, bool? isTPLong = null, bool? isSLLong = null, bool? isShort = null, bool? isTPShort = null, bool? isSLShort = null)
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
            Notes = notes
        };
        _cache[ticker] = item;

        // Persist into Data/Metadata/{ticker}.meta.parquet
        Task.Run(async () =>
        {
            try
            {
                var provider = AppDomain.CurrentDomain.GetData("MarketDataProvider") as ParquetMarketDataProvider;
                if (provider != null)
                {
                    await provider.SaveStrategyMetadataAsync(ticker, longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, notes, isLong, isTPLong, isSLLong, isShort, isTPShort, isSLShort);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist strategy to Parquet for {ticker}: {ex.Message}");
            }
        });
    }

    public void SaveStrategy(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? notes)
    {
        SaveStrategy(ticker, entryPrice, targetPrice, stopLoss, null, null, null, notes);
    }

    public void RegisterLoadedStrategy(string ticker, decimal? longVal, decimal? exitLong, decimal? stopLossLong, decimal? shortVal, decimal? exitShort, decimal? stopLossShort, string? notes,
        bool? isLong = null, bool? isTPLong = null, bool? isSLLong = null, bool? isShort = null, bool? isTPShort = null, bool? isSLShort = null)
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
                Notes = notes
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
                Notes = notes ?? existing.Notes
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
