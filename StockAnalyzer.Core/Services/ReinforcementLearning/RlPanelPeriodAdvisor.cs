using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Why a start date could or could not be suggested.</summary>
public enum RlStartSuggestionStatus
{
    /// <summary>The suggested start meets the catalog rule on the union calendar of the usable symbols.</summary>
    Found = 0,
    /// <summary>The stored history is too short (or the rule is never met); <c>Start</c> is the earliest date that can still be used.</summary>
    InsufficientHistory = 1,
    /// <summary>Fewer than <see cref="RlPortfolioContract.MinimumBlockSymbols"/> symbols have usable stored dates; no start is suggested.</summary>
    NoUsableData = 2,
}

/// <param name="Start">Null only for <see cref="RlStartSuggestionStatus.NoUsableData"/>.</param>
/// <param name="RequiredBars">The minimum calendar length of the evaluation mode.</param>
/// <param name="AvailableBars">Bars of the suggested period on the union calendar (the whole searchable history for <see cref="RlStartSuggestionStatus.InsufficientHistory"/>).</param>
public sealed record RlStartSuggestion(RlStartSuggestionStatus Status, DateOnly? Start, int RequiredBars, int AvailableBars);

/// <summary>
/// Suggests the LATEST start date for which the union calendar of [start, end) satisfies the SAME catalog rule as the engine and the preflight
/// (<see cref="RlPanelCatalogBuilder.Evaluate"/>) with at least <see cref="RlPanelCatalogBuilder.MinimumCalendarLength"/> bars. Read-only: only stored dates
/// before the end are read (never a price, never a bar on or after the end), nothing is written. The preflight stays the authority on feasibility; this is
/// a proposal derived from the stored calendars. A partition plan (short-history only) replaces the minimum length and the catalog function by the plan's. Its symbol rules mirror the preflight's classification (equity, same currency and time zone as the first
/// equity symbol, last stored bar before the exchange date of now); a cross-check test bounds drift.
/// </summary>
public static class RlPanelPeriodAdvisor
{
    private sealed record Candidate(string Symbol, ImmutableArray<DateOnly> Dates);

    public static Task<RlStartSuggestion> SuggestAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode,
        TrainingResourceLimits limits, CancellationToken cancellationToken = default, RlPanelPartitionPlan? plan = null)
    {
        string daily = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultDailyDataPath));
        string metadata = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath));
        return Task.Run(() => Suggest(symbols, endExclusive, mode, limits, daily, metadata, cancellationToken, plan), cancellationToken);
    }

    internal static RlStartSuggestion Suggest(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode,
        TrainingResourceLimits limits, string dailyFolder, string metadataFolder, CancellationToken ct, RlPanelPartitionPlan? plan = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(limits);
        if (plan is not null && mode != RlPanelEvaluationMode.ShortHistory)
            throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} partition plans apply to the short-history evaluation only.", nameof(plan));
        limits.Validate();
        int required = plan is null ? RlPanelCatalogBuilder.MinimumCalendarLength(mode) : RlPanelCatalogBuilder.MinimumCalendarLength(mode, plan);
        var kept = new List<Candidate>();
        string? currency = null, timezone = null;
        foreach (string symbol in symbols.Select(s => s?.Trim() ?? "").Where(s => s.Length > 0).DistinctBy(s => s, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(s => s, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (symbol.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || symbol.Contains('/') || symbol.Contains('\\')) continue;
                string? source = ParquetMarketDataProvider.ResolveFilePath(dailyFolder, symbol);
                string? metadataFile = source is null ? null : Path.Combine(metadataFolder, Path.GetFileNameWithoutExtension(source) + ".meta.parquet");
                if (source is null || !File.Exists(metadataFile)) continue;
                RlLocalSourceFiles.ValidatePath(dailyFolder, source);
                RlLocalSourceFiles.ValidatePath(metadataFolder, metadataFile!);
                var meta = RlLocalParquetReader.ReadMetadata(metadataFile!);
                string Required(string name) => meta.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value : throw new InvalidDataException($"The local metadata {name} is missing.");
                string symbolCurrency = Required("currency"), type = Required("quote_type"), symbolTimezone = Required("exchange_timezone_name");
                if (type != "EQUITY") continue;
                currency ??= symbolCurrency; timezone ??= symbolTimezone;
                if (symbolCurrency != currency || symbolTimezone != timezone) continue;
                var zone = TimeZoneInfo.FindSystemTimeZoneById(symbolTimezone);
                var dates = RlLocalParquetReader.ReadDates(source, endExclusive, limits, ct);
                if (dates.IsEmpty) continue;
                var captured = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
                if (dates[^1] >= captured) continue;
                kept.Add(new Candidate(symbol, dates));
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }

        var calendar = kept.SelectMany(c => c.Dates).Distinct().OrderBy(d => d).ToArray();
        if (kept.Count < RlPortfolioContract.MinimumBlockSymbols || calendar.Length == 0)
            return new RlStartSuggestion(RlStartSuggestionStatus.NoUsableData, null, required, 0);

        int length = calendar.Length;
        var index = new Dictionary<DateOnly, int>(length);
        for (int t = 0; t < length; t++) index[calendar[t]] = t;
        var prefixes = new int[kept.Count][];
        for (int i = 0; i < kept.Count; i++)
        {
            var present = new bool[length];
            foreach (var date in kept[i].Dates) present[index[date]] = true;
            var prefix = new int[length + 1];
            for (int t = 0; t < length; t++) prefix[t + 1] = prefix[t] + (present[t] ? 1 : 0);
            prefixes[i] = prefix;
        }

        // Evaluate reads prefix differences only, so the last k calendar entries are the slice [length - k, length) of the same arrays.
        int maximum = Math.Min(length, limits.MaxSamples);
        var holdout = Enumerable.Repeat(false, kept.Count).ToImmutableArray();
        var slices = new ReadOnlyMemory<int>[kept.Count];
        for (int k = required; k <= maximum; k++)
        {
            ct.ThrowIfCancellationRequested();
            int offset = length - k;
            for (int i = 0; i < slices.Length; i++) slices[i] = new ReadOnlyMemory<int>(prefixes[i], offset, k + 1);
            ImmutableArray<RlPanelCellCount> shortCells;
            if (plan is null) _ = RlPanelCatalogBuilder.Evaluate(k, slices, holdout, mode, out shortCells);
            else _ = RlPanelCatalogBuilder.EvaluatePlan(k, slices, holdout, mode, plan, out shortCells);
            if (shortCells.IsEmpty) return new RlStartSuggestion(RlStartSuggestionStatus.Found, calendar[offset], required, k);
        }
        return new RlStartSuggestion(RlStartSuggestionStatus.InsufficientHistory, calendar[length - maximum], required, maximum);
    }
}
