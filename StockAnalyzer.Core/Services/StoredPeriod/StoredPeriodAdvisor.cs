using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.StoredPeriod;

/// <summary>
/// Reads the stored bar dates and proposes the pre-filled period (see <see cref="IStoredPeriodAdvisor"/>). A different purpose from
/// <see cref="RlPanelPeriodAdvisor"/> (which needs at least two symbols and the panel catalog rule), so it is a separate class: it only CALLS the existing
/// reader and folder rules. It writes no file; the files are read one after another over one shared connection (<see cref="RlLocalParquetSession"/>); a file
/// that cannot be read is skipped, never an error.
/// </summary>
public sealed class StoredPeriodAdvisor : IStoredPeriodAdvisor
{
    /// <summary>The newest-date query needs no history: one bar is the smallest requirement the proposal arithmetic accepts.</summary>
    private const int LatestOnlyBars = 1;

    private readonly MarketDataSettings _settings;
    private readonly TimeProvider _clock;
    private readonly TrainingResourceLimits _limits;
    private readonly int _maximumSymbols;

    public StoredPeriodAdvisor(IOptions<MarketDataSettings>? settings = null, TimeProvider? clock = null, int maximumSymbols = StoredPeriodLimits.MaximumSymbols)
    {
        _settings = settings?.Value ?? new MarketDataSettings();
        _clock = clock ?? TimeProvider.System;
        _maximumSymbols = StoredPeriodLimits.Validate(maximumSymbols);
        _limits = TrainingResourceGuard.Resolve(new TrainingResourceOverrides());
    }

    public Task<StoredPeriodProposal> ProposeForRlAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, CancellationToken cancellationToken)
    {
        var (daily, metadata) = LocalFolders();
        return ProposeForRlAsync(symbol, requiredBars, endBoundInclusive, daily, metadata, cancellationToken);
    }

    public Task<DateOnly?> LatestFinalizedDateAsync(IReadOnlyList<string> symbols, CancellationToken cancellationToken)
    {
        var (daily, metadata) = LocalFolders();
        return LatestFinalizedDateAsync(symbols, daily, metadata, cancellationToken);
    }

    internal Task<StoredPeriodProposal> ProposeForRlAsync(string symbol, int requiredBars, DateOnly? endBoundInclusive, string dailyFolder, string metadataFolder,
        CancellationToken cancellationToken)
        => Task.Run(() => ProposeFinalized(new[] { symbol }, requiredBars, endBoundInclusive, dailyFolder, metadataFolder, cancellationToken), cancellationToken);

    internal Task<DateOnly?> LatestFinalizedDateAsync(IReadOnlyList<string> symbols, string dailyFolder, string metadataFolder, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var proposal = ProposeFinalized(symbols, LatestOnlyBars, endBoundInclusive: null, dailyFolder, metadataFolder, cancellationToken);
            return proposal.Status is StoredPeriodStatus.Found or StoredPeriodStatus.InsufficientHistory ? proposal.Latest : null;
        }, cancellationToken);

    public Task<StoredPeriodProposal> ProposeForTrainingAsync(IReadOnlyList<string> symbols, TrainingTimeframe timeframe, int requiredBars,
        DateOnly? endBoundInclusive, CancellationToken cancellationToken)
    {
        string folder = TrainingFolder(timeframe);
        return Task.Run(() => ProposeStored(symbols, requiredBars, endBoundInclusive, folder, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<SymbolBarInfo>> ReadSymbolBarsForTrainingAsync(IReadOnlyList<string> symbols, TrainingTimeframe timeframe, int requiredBars,
        DateOnly? start, DateOnly? end, CancellationToken cancellationToken)
    {
        string folder = TrainingFolder(timeframe);
        return Task.Run(() => ReadSymbolBars(symbols, requiredBars, start, end, folder, cancellationToken), cancellationToken);
    }

    /// <summary>The configured folder of the timeframe (the one folder rule of the Training Wizard family).</summary>
    private string TrainingFolder(TrainingTimeframe timeframe) => Path.GetFullPath(timeframe switch
    {
        TrainingTimeframe.Daily => PathDiscovery.ResolveDataPath(_settings.DailyDataPath, MarketDataSettings.DefaultDailyDataPath),
        TrainingTimeframe.Weekly => PathDiscovery.ResolveDataPath(_settings.WeeklyDataPath, MarketDataSettings.DefaultWeeklyDataPath),
        TrainingTimeframe.Monthly => PathDiscovery.ResolveDataPath(_settings.MonthlyDataPath, MarketDataSettings.DefaultMonthlyDataPath),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported training timeframe."),
    });

    /// <summary>The Daily and Metadata folders of the RL family (the fixed Data folders, as the panel advisor and the preflight use them).</summary>
    private static (string Daily, string Metadata) LocalFolders()
        => (Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultDailyDataPath)),
            Path.GetFullPath(PathDiscovery.ResolveDataPath(null, MarketDataSettings.DefaultMetadataDataPath)));

    /// <summary>RL / Portfolio RL family: final bars only (before the exchange date of now), equity metadata required.</summary>
    internal StoredPeriodProposal ProposeFinalized(IReadOnlyList<string> symbols, int requiredBars, DateOnly? endBoundInclusive,
        string dailyFolder, string metadataFolder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var distinct = Distinct(symbols);
        if (distinct.Length > _maximumSymbols) return StoredPeriodProposal.None(StoredPeriodStatus.TooMany, requiredBars);
        if (requiredBars <= 0) return StoredPeriodProposal.None(StoredPeriodStatus.NoProposal, requiredBars);
        var calendars = new List<StoredPeriodCalendar>();
        RlLocalParquetSession? session = null;
        try
        {
            foreach (string symbol in distinct)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string? source = ParquetMarketDataProvider.ResolveFilePath(dailyFolder, symbol);
                    string? metadataFile = source is null ? null : Path.Combine(metadataFolder, Path.GetFileNameWithoutExtension(source) + RlLocalParquetReader.MetadataFileSuffix);
                    if (source is null || !File.Exists(metadataFile)) continue;
                    RlLocalSourceFiles.ValidatePath(dailyFolder, source);
                    RlLocalSourceFiles.ValidatePath(metadataFolder, metadataFile!);
                    session ??= new RlLocalParquetSession(_limits);
                    var meta = session.ReadMetadata(metadataFile!);
                    string Required(string name) => meta.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                        ? value : throw new InvalidDataException($"The local metadata {name} is missing.");
                    _ = Required(RlLocalParquetReader.CurrencyKey);
                    string type = Required(RlLocalParquetReader.QuoteTypeKey), timezone = Required(RlLocalParquetReader.ExchangeTimezoneKey);
                    if (type != RlLocalParquetReader.EquityQuoteType) continue;
                    var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
                    var dates = session.ReadDates(source, DateOnly.MaxValue, ct);
                    var exchangeDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone).DateTime);
                    calendars.Add(new StoredPeriodCalendar(symbol, Finalize(dates, exchangeDate, endBoundInclusive)));
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
        }
        finally { session?.Dispose(); }
        return StoredPeriodMath.Propose(calendars, requiredBars);
    }

    /// <summary>Training Wizard family: every stored bar of the timeframe folder counts.</summary>
    internal StoredPeriodProposal ProposeStored(IReadOnlyList<string> symbols, int requiredBars, DateOnly? endBoundInclusive, string folder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var distinct = Distinct(symbols);
        if (distinct.Length > _maximumSymbols) return StoredPeriodProposal.None(StoredPeriodStatus.TooMany, requiredBars);
        if (requiredBars <= 0) return StoredPeriodProposal.None(StoredPeriodStatus.NoProposal, requiredBars);
        var calendars = new List<StoredPeriodCalendar>();
        RlLocalParquetSession? session = null;
        try
        {
            foreach (string symbol in distinct)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string? source = ParquetMarketDataProvider.ResolveFilePath(folder, symbol);
                    if (source is null) continue;
                    session ??= new RlLocalParquetSession(_limits);
                    var dates = session.ReadDates(source, DateOnly.MaxValue, ct);
                    calendars.Add(new StoredPeriodCalendar(symbol, Finalize(dates, null, endBoundInclusive)));
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
        }
        finally { session?.Dispose(); }
        return StoredPeriodMath.Propose(calendars, requiredBars);
    }

    /// <summary>Training Wizard family: every stored bar counts; an unreadable symbol is left out; more symbols than the cap, or a requirement that is not positive, reads nothing.</summary>
    internal IReadOnlyList<SymbolBarInfo> ReadSymbolBars(IReadOnlyList<string> symbols, int requiredBars, DateOnly? start, DateOnly? end, string folder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var distinct = Distinct(symbols);
        if (distinct.Length > _maximumSymbols || requiredBars <= 0) return Array.Empty<SymbolBarInfo>();
        var calendars = new List<StoredPeriodCalendar>();
        RlLocalParquetSession? session = null;
        try
        {
            foreach (string symbol in distinct)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string? source = ParquetMarketDataProvider.ResolveFilePath(folder, symbol);
                    if (source is null) continue;
                    session ??= new RlLocalParquetSession(_limits);
                    calendars.Add(new StoredPeriodCalendar(symbol, session.ReadDates(source, DateOnly.MaxValue, ct)));
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
        }
        finally { session?.Dispose(); }
        return StoredPeriodBarCounts.Build(calendars, requiredBars, start, end);
    }

    /// <summary>Drops the bars on or after the exchange date (when given) and after the end bound (when given); the symbol itself stays.</summary>
    private static ImmutableArray<DateOnly> Finalize(ImmutableArray<DateOnly> dates, DateOnly? exchangeDate, DateOnly? endBoundInclusive)
        => dates.Where(d => (exchangeDate is not { } x || d < x) && (endBoundInclusive is not { } e || d <= e)).ToImmutableArray();

    private static string[] Distinct(IReadOnlyList<string> symbols)
        => symbols.Select(s => s?.Trim() ?? "").Where(s => s.Length > 0).DistinctBy(s => s, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
}
