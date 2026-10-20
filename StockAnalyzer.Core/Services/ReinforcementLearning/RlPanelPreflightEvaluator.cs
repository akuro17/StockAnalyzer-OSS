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

/// <summary>
/// Panel preflight (S13/S14): classifies every selected symbol from its existing local Daily and Metadata files and runs the SAME catalog
/// function as the engine (<see cref="RlPanelCatalogBuilder.Evaluate"/>) on the union calendar of the usable symbols. Read-only: files are read
/// in place, nothing is copied, written, downloaded or repaired. Pure with respect to its inputs, cancelable, and meant to run off the UI thread.
/// The currency and timezone reference is the first equity symbol in canonical order, exactly as the panel snapshot uses its first symbol.
/// </summary>
public static class RlPanelPreflightEvaluator
{
    private static readonly Lazy<int> s_standard = new(() => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.Standard));
    private static readonly Lazy<int> s_shortHistory = new(() => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory));

    private sealed record Candidate(string Symbol, ImmutableArray<DateTime> Dates);

    public static Task<PanelPreflight> EvaluateAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
        RlPanelEvaluationMode mode, RlPortfolioSettings settings, TrainingResourceLimits limits, CancellationToken cancellationToken = default,
        RlPanelPartitionPlan? plan = null)
    {
        string daily = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultDailyDataPath));
        string metadata = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath));
        return Task.Run(() => Evaluate(symbols, start, endExclusive, holdoutRequested, mode, settings, limits, daily, metadata, cancellationToken, plan), cancellationToken);
    }

    internal static PanelPreflight Evaluate(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
        RlPanelEvaluationMode mode, RlPortfolioSettings settings, TrainingResourceLimits limits, string dailyFolder, string metadataFolder,
        CancellationToken ct, RlPanelPartitionPlan? plan = null)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(limits);
        if (plan is not null && mode != RlPanelEvaluationMode.ShortHistory)
            throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} partition plans apply to the short-history evaluation only.", nameof(plan));
        if (start >= endExclusive) throw new ArgumentException("The period must contain at least one day.", nameof(endExclusive));
        limits.Validate();
        _ = RlPortfolioSettingsValidator.Validate(settings, limits);

        var names = symbols.Select(s => s?.Trim() ?? "").Where(s => s.Length > 0).DistinctBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var statuses = new Dictionary<string, RlPanelSymbolPreflight>(StringComparer.Ordinal);
        var candidates = new List<Candidate>();
        string? currency = null, timezone = null;
        foreach (string symbol in names)
        {
            ct.ThrowIfCancellationRequested();
            RlPanelSymbolPreflight Status(RlPanelSymbolStatus status, int bars = 0, string detail = "")
                => new(symbol, status, bars, RlContract.SupportLength, detail);
            try
            {
                if (symbol.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || symbol.Contains('/') || symbol.Contains('\\'))
                { statuses[symbol] = Status(RlPanelSymbolStatus.ReadError, detail: "The symbol is not a safe file name."); continue; }
                string? source = ParquetMarketDataProvider.ResolveFilePath(dailyFolder, symbol);
                string? metadataFile = source is null ? null : Path.Combine(metadataFolder, Path.GetFileNameWithoutExtension(source) + RlLocalParquetReader.MetadataFileSuffix);
                if (source is null || !File.Exists(metadataFile)) { statuses[symbol] = Status(RlPanelSymbolStatus.MissingFile); continue; }
                RlLocalSourceFiles.ValidatePath(dailyFolder, source);
                RlLocalSourceFiles.ValidatePath(metadataFolder, metadataFile!);
                var meta = RlLocalParquetReader.ReadMetadata(metadataFile!);
                string Required(string name) => meta.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                    ? value : throw new InvalidDataException($"The local metadata {name} is missing.");
                string symbolCurrency = Required(RlLocalParquetReader.CurrencyKey), type = Required(RlLocalParquetReader.QuoteTypeKey), symbolTimezone = Required(RlLocalParquetReader.ExchangeTimezoneKey);
                if (type != RlLocalParquetReader.EquityQuoteType) { statuses[symbol] = Status(RlPanelSymbolStatus.NotEquity, detail: type); continue; }
                currency ??= symbolCurrency; timezone ??= symbolTimezone;
                if (symbolCurrency != currency) { statuses[symbol] = Status(RlPanelSymbolStatus.CurrencyMismatch, detail: symbolCurrency); continue; }
                if (symbolTimezone != timezone) { statuses[symbol] = Status(RlPanelSymbolStatus.TimezoneMismatch, detail: symbolTimezone); continue; }
                var zone = TimeZoneInfo.FindSystemTimeZoneById(symbolTimezone);
                RlLocalParquetReader.Result read;
                try { read = RlLocalParquetReader.Read(source, start, endExclusive, limits, ct); }
                catch (InvalidDataException error) when (RlSourceContract.IsInsufficientData(error))
                { statuses[symbol] = Status(RlPanelSymbolStatus.NoValidBlock, detail: error.Message); continue; }
                var captured = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
                if (DateOnly.FromDateTime(read.Bars[^1].Timestamp) >= captured)
                { statuses[symbol] = Status(RlPanelSymbolStatus.ReadError, read.Bars.Length, "The selected stored bars include the current or a future exchange date."); continue; }
                statuses[symbol] = Status(RlPanelSymbolStatus.Ready, read.Bars.Length);
                candidates.Add(new Candidate(symbol, read.Bars.Select(b => b.Timestamp).ToImmutableArray()));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                statuses[symbol] = Status(RlPanelSymbolStatus.ReadError, detail: error.Message);
            }
        }

        // A symbol that has no complete block while the panel of the readable symbols does have blocks cannot be traded in any of them. When the
        // panel has no block at all the shortfall is the panel's (too short, too few symbols), so every symbol keeps its status and the counts stay visible.
        // Removing a symbol also removes the dates only it had, which moves every later calendar index: a symbol that sat in a block of the earlier
        // calendar may sit in none of the new one. The exclusion therefore repeats until no Ready symbol is left without a block; each pass removes at
        // least one symbol, so it ends after at most as many passes as there are candidates. A pass that removes nothing is the old single pass.
        while (candidates.Count > 0)
        {
            var first = Catalog(candidates, mode, plan, out _, out _, out _);
            var blocks = first.Train.Concat(first.Validation).Concat(first.Oos).ToArray();
            if (blocks.Length == 0) break;
            bool removed = false;
            for (int i = 0; i < candidates.Count; i++)
                if (!blocks.Any(b => b.EligibleSymbols.Contains(i)))
                {
                    var current = statuses[candidates[i].Symbol];
                    statuses[candidates[i].Symbol] = current with { Status = RlPanelSymbolStatus.NoValidBlock, Detail = "No complete block holds this symbol." };
                    removed = true;
                }
            if (!removed) break;
            candidates.RemoveAll(c => statuses[c.Symbol].Status != RlPanelSymbolStatus.Ready);
        }

        int length = 0;
        var cells = ImmutableArray<RlPanelCellCount>.Empty;
        bool feasible = false, risk = false;
        DateOnly? firstValidation = null;
        if (candidates.Count > 0)
        {
            var catalog = Catalog(candidates, mode, plan, out var shortCells, out length, out var calendar);
            cells = catalog.Cells;
            firstValidation = RlPortfolioTransferAdmission.FirstValidationDate(catalog, calendar);
            for (int i = 0; i < candidates.Count; i++)
                statuses[candidates[i].Symbol] = statuses[candidates[i].Symbol] with { Role = RlPanelSymbolRoles.Of(catalog, i) };
            feasible = shortCells.IsEmpty;
            if (feasible && holdoutRequested && candidates.Count >= RlPortfolioContract.MinimumSymbolsWithHoldout)
            {
                int held = RlPanelUniverse.HoldoutCount(candidates.Count, settings.HoldoutPercent);
                risk = catalog.Cells.Any(c => c.Group == RlPanelSymbolGroup.Train
                    && catalog.Partition(c.Partition).Count(b => b.EligibleSymbols.Length - held >= RlPortfolioContract.MinimumBlockSymbols) < c.RequiredBlocks);
            }
        }
        return new PanelPreflight(start, endExclusive, mode, names.Select(n => statuses[n]).ToImmutableArray(), length,
            plan is not null ? RlPanelCatalogBuilder.MinimumCalendarLength(mode, plan)
                : (mode == RlPanelEvaluationMode.ShortHistory ? s_shortHistory : s_standard).Value, cells, feasible, risk, firstValidation);
    }

    /// <summary>The engine's catalog function (with the partition plan when there is one) on the union calendar of the set, with every symbol in the training group.</summary>
    private static RlPanelCatalog Catalog(IReadOnlyList<Candidate> set, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan,
        out ImmutableArray<RlPanelCellCount> shortCells, out int length, out DateTime[] calendar)
    {
        calendar = set.SelectMany(c => c.Dates).Distinct().OrderBy(d => d).ToArray();
        var index = new Dictionary<DateTime, int>(calendar.Length);
        for (int t = 0; t < calendar.Length; t++) index[calendar[t]] = t;
        var prefixes = new List<ReadOnlyMemory<int>>(set.Count);
        foreach (var candidate in set)
        {
            var present = new bool[calendar.Length];
            foreach (var date in candidate.Dates) present[index[date]] = true;
            var prefix = new int[calendar.Length + 1];
            for (int t = 0; t < calendar.Length; t++) prefix[t + 1] = prefix[t] + (present[t] ? 1 : 0);
            prefixes.Add(prefix);
        }
        length = calendar.Length;
        var noHoldout = Enumerable.Repeat(false, set.Count).ToImmutableArray();
        return plan is null ? RlPanelCatalogBuilder.Evaluate(calendar.Length, prefixes, noHoldout, mode, out shortCells)
            : RlPanelCatalogBuilder.EvaluatePlan(calendar.Length, prefixes, noHoldout, mode, plan, out shortCells);
    }
}

/// <summary>The Start rules of the portfolio dialog (S13, S15) as a pure function, so the view model only renders the decision.</summary>
public static class RlPanelStartGate
{
    /// <param name="excludeUnavailable">The user confirmed that symbols which are not <c>Ready</c> are left out of the run.</param>
    /// <param name="holdoutRequested">The symbol holdout is on (it is the default); it is switched off with a flag when the universe is too small.</param>
    public static RlPanelStartDecision Decide(PanelPreflight preflight, bool excludeUnavailable, bool holdoutRequested, RlPortfolioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(settings);
        var ready = preflight.Symbols.Where(s => s.Status == RlPanelSymbolStatus.Ready).Select(s => s.Symbol).ToImmutableArray();
        var rest = preflight.Symbols.Where(s => s.Status != RlPanelSymbolStatus.Ready).Select(s => s.Symbol).ToImmutableArray();
        int resolved = preflight.Symbols.Length;
        int usable = excludeUnavailable ? ready.Length : resolved;
        var blocker = resolved == 0 ? RlPanelStartBlocker.NoSymbols
            : resolved == 1 || (excludeUnavailable && usable == 1) ? RlPanelStartBlocker.SingleSymbol
            : !excludeUnavailable && !rest.IsEmpty ? RlPanelStartBlocker.NotAllReady
            : usable < RlPortfolioContract.MinimumSymbols ? RlPanelStartBlocker.TooFewSymbols
            : usable > settings.MaxSymbols ? RlPanelStartBlocker.TooManySymbols
            : !preflight.Feasible ? RlPanelStartBlocker.Infeasible
            : RlPanelStartBlocker.None;
        bool holdout = holdoutRequested && usable >= RlPortfolioContract.MinimumSymbolsWithHoldout
            && usable - RlPanelUniverse.HoldoutCount(usable, settings.HoldoutPercent) >= RlPortfolioContract.MinimumTrainSymbols;
        return new RlPanelStartDecision(blocker == RlPanelStartBlocker.None, blocker, holdout, holdoutRequested && !holdout,
            excludeUnavailable ? ready : preflight.Symbols.Select(s => s.Symbol).ToImmutableArray(), excludeUnavailable ? rest : ImmutableArray<string>.Empty);
    }
}
