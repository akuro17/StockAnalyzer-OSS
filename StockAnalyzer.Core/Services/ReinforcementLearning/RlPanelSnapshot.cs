using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Validated immutable multi-symbol source snapshot. Every symbol is an unchanged single-symbol snapshot directory that is re-validated by the
/// same per-file rules as <see cref="RlSourceSnapshot"/>; the panel adds the union calendar, the identity hashes, the symbol split and the
/// episode catalog. Loading never writes. UTC midnight remains a date label, never execution evidence.
/// </summary>
public sealed class RlPanelSnapshot : IRlPanelBars
{
    private const string Prefix = RlPortfolioContract.UniverseErrorPrefix;
    private readonly ImmutableArray<ImmutableArray<CandleData>> _bars;
    private readonly ImmutableArray<ImmutableArray<int>> _barAt;

    public string Directory { get; }
    public RlPanelManifest Manifest { get; }
    public string Revision { get; }
    /// <summary>Symbols in canonical Ordinal order; every index-based API uses this order.</summary>
    public ImmutableArray<string> Symbols { get; }
    /// <summary>Ascending distinct bar timestamps of all symbols (UTC midnight date labels).</summary>
    public ImmutableArray<DateTime> Calendar { get; }
    public RlPanelCatalog Catalog { get; }

    private RlPanelSnapshot(string directory, RlPanelManifest manifest, string revision, ImmutableArray<string> symbols,
        ImmutableArray<DateTime> calendar, ImmutableArray<ImmutableArray<CandleData>> bars, ImmutableArray<ImmutableArray<int>> barAt, RlPanelCatalog catalog)
    {
        Directory = directory; Manifest = manifest; Revision = revision; Symbols = symbols; Calendar = calendar; _bars = bars; _barAt = barAt; Catalog = catalog;
    }

    public int SymbolCount => Symbols.Length;

    /// <summary>Number of calendar indices on which the symbol has a bar (its coverage of the shared calendar).</summary>
    public int PresentBars(int symbolIndex) => _bars[symbolIndex].Length;

    public bool IsPresent(int symbolIndex, int calendarIndex) => _barAt[symbolIndex][calendarIndex] >= 0;

    public bool TryGetBar(int symbolIndex, int calendarIndex, out CandleData bar)
    {
        int index = _barAt[symbolIndex][calendarIndex];
        bar = index >= 0 ? _bars[symbolIndex][index] : default;
        return index >= 0;
    }

    /// <summary>Folder of one symbol's immutable single-symbol snapshot below a panel directory.</summary>
    internal static string SymbolDirectory(string panelDirectory, string symbol)
        => Path.Combine(panelDirectory, RlPortfolioContract.PanelSymbolFolder, symbol);

    /// <summary>The union calendar of the symbols' bars: ascending, distinct.</summary>
    internal static ImmutableArray<DateTime> BuildCalendar(IEnumerable<ImmutableArray<CandleData>> bars)
        => bars.SelectMany(series => series.Select(bar => bar.Timestamp)).Distinct().OrderBy(t => t).ToImmutableArray();

    internal static InvalidDataException Reject(string detail, Exception? inner = null) => new($"{Prefix} {detail}", inner);

    public static RlPanelSnapshot Load(string directory, TrainingResourceLimits limits, RlPortfolioSettings settings,
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.Standard, CancellationToken cancellationToken = default)
        => Load(directory, limits, settings, mode, null, cancellationToken);

    /// <summary>
    /// <see cref="Load(string, TrainingResourceLimits, RlPortfolioSettings, RlPanelEvaluationMode, CancellationToken)"/> with a partition plan:
    /// a non-null plan (short-history only) cuts the catalog by the plan; null is the behaviour of the evaluation mode.
    /// </summary>
    public static RlPanelSnapshot Load(string directory, TrainingResourceLimits limits, RlPortfolioSettings settings,
        RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(settings);
        limits.Validate();
        _ = RlPortfolioSettingsValidator.Validate(settings, limits);
        directory = Path.GetFullPath(directory);
        long byteBound = RlSourceSnapshot.ByteBound(limits);
        string manifestPath = Path.Combine(directory, RlPortfolioContract.PanelManifest);
        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifestPath).Length > byteBound)
            throw Reject("a linked or oversized panel manifest is prohibited.");
        var manifest = JsonSerializer.Deserialize<RlPanelManifest>(File.ReadAllText(manifestPath), RlFiles.Json)
            ?? throw Reject("the panel manifest is absent.");
        if (manifest.SchemaVersion != RlPortfolioContract.PanelSchemaVersion || manifest.SpecificationId != RlPortfolioContract.SpecificationId
            || manifest.Symbols.IsDefault || manifest.HoldoutSymbols.IsDefault)
            throw Reject("the panel manifest schema or specification is unsupported.");

        var symbols = manifest.Symbols.Select(e => e.Symbol).ToImmutableArray();
        ImmutableArray<string> canonical;
        try { canonical = RlPanelUniverse.Canonicalize(symbols, manifest.HoldoutEnabled, settings.MaxSymbols); }
        catch (ArgumentException error) { throw new InvalidDataException(error.Message, error); }
        if (!canonical.SequenceEqual(symbols)) throw Reject("the stored symbols are not in canonical order.");
        if (manifest.PeriodStartInclusive >= manifest.PeriodEndExclusive) throw Reject("the stored period is empty.");

        var bars = ImmutableArray.CreateBuilder<ImmutableArray<CandleData>>(symbols.Length);
        foreach (var entry in manifest.Symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RlSourceSnapshot.RlValidatedSource source;
            try { source = RlSourceSnapshot.ValidateSourceFiles(SymbolDirectory(directory, entry.Symbol), limits, cancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { throw Reject($"{entry.Symbol}: {error.Message}", error); }
            var m = source.Manifest;
            if (m.Symbol != entry.Symbol) throw Reject($"{entry.Symbol}: the symbol snapshot belongs to {m.Symbol}.");
            if (source.Revision != entry.SourceManifestSha256) throw Reject($"{entry.Symbol}: the source manifest hash differs from the panel manifest.");
            if (m.QuoteCurrency != manifest.Currency) throw Reject($"{entry.Symbol}: currency {m.QuoteCurrency} differs from the panel currency {manifest.Currency}.");
            if (m.ExchangeTimezone != manifest.ExchangeTimezone)
                throw Reject($"{entry.Symbol}: exchange timezone {m.ExchangeTimezone} differs from the panel timezone {manifest.ExchangeTimezone}.");
            if (m.CapturedExchangeDate != manifest.CapturedExchangeDate)
                throw Reject($"{entry.Symbol}: captured exchange date {m.CapturedExchangeDate} differs from the panel date {manifest.CapturedExchangeDate}; retry the capture.");
            if (m.PeriodStartInclusive != manifest.PeriodStartInclusive || m.PeriodEndExclusive != manifest.PeriodEndExclusive)
                throw Reject($"{entry.Symbol}: the stored period differs from the panel period.");
            if (source.Bars.Length != entry.BarCount || m.FirstDate != entry.FirstDate || m.LastDate != entry.LastDate)
                throw Reject($"{entry.Symbol}: the stored extent differs from the panel manifest.");
            bars.Add(source.Bars);
        }

        var calendar = BuildCalendar(bars);
        if (calendar.Length != manifest.CalendarLength || RlPanelUniverse.CalendarSha256(calendar) != manifest.CalendarSha256)
            throw Reject("the union calendar differs from the panel manifest.");
        string membership = RlPanelUniverse.MembershipSha256(symbols, manifest.Symbols.Select(e => e.SourceManifestSha256).ToArray());
        uint seed = RlPanelUniverse.SplitSeed(RlPanelUniverse.SymbolSetSha256(symbols));
        if (membership != manifest.MembershipSha256 || seed != manifest.SplitSeed) throw Reject("the membership hash or split seed differs from the panel manifest.");
        ImmutableArray<bool> holdout;
        try
        {
            holdout = manifest.HoldoutEnabled ? RlPanelUniverse.AssignHoldout(symbols, seed, manifest.HoldoutPercent)
                : Enumerable.Repeat(false, symbols.Length).ToImmutableArray();
        }
        catch (ArgumentException error) { throw new InvalidDataException(error.Message, error); }
        var holdoutSymbols = symbols.Where((_, i) => holdout[i]).ToImmutableArray();
        if (!holdoutSymbols.SequenceEqual(manifest.HoldoutSymbols)
            || RlPanelUniverse.UniverseSha256(membership, seed, manifest.HoldoutEnabled, manifest.HoldoutPercent, holdoutSymbols) != manifest.UniverseSha256)
            throw Reject("the symbol split or universe hash differs from the panel manifest.");

        var index = new Dictionary<DateTime, int>(calendar.Length);
        for (int t = 0; t < calendar.Length; t++) index[calendar[t]] = t;
        var barAt = ImmutableArray.CreateBuilder<ImmutableArray<int>>(symbols.Length);
        var prefixes = new List<ReadOnlyMemory<int>>(symbols.Length);
        foreach (var series in bars)
        {
            var map = new int[calendar.Length];
            Array.Fill(map, -1);
            for (int i = 0; i < series.Length; i++) map[index[series[i].Timestamp]] = i;
            var prefix = new int[calendar.Length + 1];
            for (int t = 0; t < calendar.Length; t++) prefix[t + 1] = prefix[t] + (map[t] >= 0 ? 1 : 0);
            barAt.Add(ImmutableCollectionsMarshal.AsImmutableArray(map));
            prefixes.Add(prefix);
        }
        var catalog = plan is null ? RlPanelCatalogBuilder.Build(calendar.Length, prefixes, holdout, mode)
            : RlPanelCatalogBuilder.BuildPlan(calendar.Length, prefixes, holdout, mode, plan);
        return new RlPanelSnapshot(directory, manifest, ContentHash.OfFile(manifestPath), symbols, calendar, bars.MoveToImmutable(), barAt.MoveToImmutable(), catalog);
    }
}
