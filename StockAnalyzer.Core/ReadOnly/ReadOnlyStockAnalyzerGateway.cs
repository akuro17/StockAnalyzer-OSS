using System.Runtime.ExceptionServices;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Screener;

namespace StockAnalyzer.Core.ReadOnly;

/// <inheritdoc />
public sealed class ReadOnlyStockAnalyzerGateway : IReadOnlyStockAnalyzerGateway
{
    private readonly IReadOnlyMarketDataSource _marketData;
    private readonly IDataService _dataService;
    private readonly IAnalysisPipelineService _pipeline;
    private readonly IScreenerService _screener;
    private readonly IReadOnlyTemplateSource _templates;
    private readonly IScreenerTemplateEvaluator _templateEvaluator;
    private readonly IBacktestRunPreparationService _backtestPreparation;
    private readonly IBacktestEvaluationService _backtestEvaluation;
    private readonly IChartSeriesCalculationService _chartSeriesCalculation;

    public ReadOnlyStockAnalyzerGateway(
        IReadOnlyMarketDataSource marketData,
        IDataService dataService,
        IAnalysisPipelineService pipeline,
        IScreenerService screener,
        IReadOnlyTemplateSource templates,
        IScreenerTemplateEvaluator templateEvaluator,
        IBacktestRunPreparationService backtestPreparation,
        IBacktestEvaluationService backtestEvaluation,
        IChartSeriesCalculationService? chartSeriesCalculation = null)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _screener = screener ?? throw new ArgumentNullException(nameof(screener));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _templateEvaluator = templateEvaluator ?? throw new ArgumentNullException(nameof(templateEvaluator));
        _backtestPreparation = backtestPreparation ?? throw new ArgumentNullException(nameof(backtestPreparation));
        _backtestEvaluation = backtestEvaluation ?? throw new ArgumentNullException(nameof(backtestEvaluation));
        _chartSeriesCalculation = chartSeriesCalculation ?? new ChartSeriesCalculationService();
    }

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
        => _marketData.GetAvailableTickersAsync(cancellationToken);

    public async Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
    {
        string canonical = RequireCanonical(symbol);
        RequireSupportedTimeFrame(timeFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return await new TimeFrameManager(_dataService).GetCandlesAsync(canonical, timeFrame, count, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
    {
        string canonical = RequireCanonical(symbol);
        RequireSupportedTimeFrame(timeFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // The cut is applied to the DAILY series and a weekly or monthly bar is aggregated afterwards, so a bar that
        // straddles endDate holds only the days up to it. Count 0 asks the data layer for the whole stored history
        // (the manager truncates only for a positive count), so no leading week or month is truncated by the load.
        IReadOnlyList<CandleData> daily = await new TimeFrameManager(_dataService).GetCandlesAsync(canonical, TimeFrame.D1, 0, cancellationToken).ConfigureAwait(false);
        return AsOfCandleWindow.Build(daily, endDate, timeFrame, count);
    }

    private static void RequireSupportedTimeFrame(TimeFrame timeFrame)
    {
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }
    }

    public async Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        => await _marketData.TryGetStoredMetadataAsync(RequireCanonical(symbol), cancellationToken).ConfigureAwait(false);

    public async Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default)
        => await _marketData.GetTimeSeriesLastUpdatedAsync(RequireCanonical(symbol), cancellationToken).ConfigureAwait(false);

    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)
        => Task.Run(() => _pipeline.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol));

    public ReverseWatchCurveData? CalculateReverseWatch(
        IReadOnlyList<CoreCandleData> candles,
        int period,
        string symbol,
        bool isMaBased = true,
        bool isLogScaleVolume = false,
        int dataCount = 0)
        => _pipeline.CalculateReverseWatch(candles, period, RequireCanonical(symbol), isMaBased, isLogScaleVolume, dataCount);

    public ChartSeriesCalculationResult CalculateChartSeries(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(request);
        return _chartSeriesCalculation.Calculate(candles, request, cancellationToken);
    }

    public Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
        => RunScreenerAsync(symbols, condition, timeFrame, Array.Empty<string>(), cancellationToken);

    public Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
        => RunScreenerCoreAsync(_dataService, _screener, symbols, condition, timeFrame, comparisonSymbols, cancellationToken);

    public async Task<ScreenerBarsResult> RunScreenerOnBarsAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
    {
        var bars = new ScreenerBarsDataService(_dataService, selection);
        IReadOnlyList<string> matched = await RunScreenerCoreAsync(bars, ScreenerOver(bars), symbols, condition, timeFrame, comparisonSymbols, cancellationToken).ConfigureAwait(false);
        return ResultOf(matched, bars, symbols);
    }

    public async Task<IReadOnlyList<CandleData>> GetScreenerBarsAsync(string symbol, TimeFrame timeFrame, ScreenerBarSelection selection, int count, CancellationToken cancellationToken = default)
    {
        string canonical = RequireCanonical(symbol);
        RequireSupportedTimeFrame(timeFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return await new ScreenerBarsDataService(_dataService, selection).LoadCandlesAsync(canonical, timeFrame, count).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> RunScreenerCoreAsync(
        IDataService dataService,
        IScreenerService screener,
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(comparisonSymbols);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }

        var canonical = symbols.Select(RequireCanonical).ToList();
        ScreenerSecondaryCandles? secondary = await LoadComparisonCandlesAsync(dataService, comparisonSymbols, timeFrame, ScreenerHistoryRequirements.GetRequiredCandleCount(condition), cancellationToken).ConfigureAwait(false);
        return secondary is null
            ? await screener.ScreenAsync(canonical, condition, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : await screener.ScreenAsync(canonical, condition, timeFrame, secondary, new Progress<int>(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The configured screener rebuilt over <paramref name="bars"/>; the shared instance is never changed.</summary>
    private IScreenerService ScreenerOver(ScreenerBarsDataService bars)
        => _screener is IScreenerServiceFactory factory
            ? factory.CreateScreener(bars)
            : throw new NotSupportedException($"{_screener.GetType().Name} cannot be rebuilt over another data source.");

    /// <summary>What the scan over <paramref name="bars"/> served, restricted to the scanned symbols (the comparison symbols are read through the same service).</summary>
    private static ScreenerBarsResult ResultOf(IReadOnlyList<string> matched, ScreenerBarsDataService bars, IReadOnlyList<string> symbols)
        => bars.ToResult(matched, symbols.Select(RequireCanonical));

    /// <summary>The bars of the comparison symbols with the history the condition needs, read once for the whole scan; null when there is no comparison symbol.</summary>
    private static async Task<ScreenerSecondaryCandles?> LoadComparisonCandlesAsync(IDataService dataService, IReadOnlyList<string> comparisonSymbols, TimeFrame timeFrame, int requiredCount, CancellationToken cancellationToken)
        => comparisonSymbols.Count == 0
            ? null
            : await ScreenerSecondaryCandles.LoadAsync(dataService, comparisonSymbols.Select(RequireCanonical), timeFrame, requiredCount, cancellationToken).ConfigureAwait(false);

    public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default)
        => _templates.ListScreenerTemplatesAsync(cancellationToken);

    public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => _templates.GetScreenerTemplateAsync(id, cancellationToken);

    public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default)
        => _templates.ListBacktestConditionTemplatesAsync(cancellationToken);

    public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => _templates.GetBacktestConditionTemplateAsync(id, cancellationToken);

    public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
        => RunScreenerTemplateAsync(symbols, template, timeFrame, Array.Empty<string>(), cancellationToken);

    public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
        => RunScreenerTemplateCoreAsync(_dataService, _templateEvaluator, symbols, template, timeFrame, comparisonSymbols, cancellationToken);

    public async Task<ScreenerBarsResult> RunScreenerTemplateOnBarsAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
    {
        var bars = new ScreenerBarsDataService(_dataService, selection);
        var evaluator = new ScreenerTemplateEvaluator(ScreenerOver(bars));
        IReadOnlyList<string> matched = await RunScreenerTemplateCoreAsync(bars, evaluator, symbols, template, timeFrame, comparisonSymbols, cancellationToken).ConfigureAwait(false);
        return ResultOf(matched, bars, symbols);
    }

    public async Task<ScreenerSideFramesResult> RunScreenerWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(selection);
        RequireSupportedFrames(ScreenerSideFrames.Collect(condition));
        return await _screener.ScreenWithSideFramesAsync(symbols.Select(RequireCanonical).ToList(), condition, selection, new Progress<int>(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScreenerSideFramesResult> RunScreenerTemplateWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(selection);
        RequireSupportedFrames(ScreenerSideFrames.Collect(template.Entries));
        return await _templateEvaluator.EvaluateWithSideFramesReportAsync(symbols.Select(RequireCanonical).ToList(), template, selection, new Progress<int>(), cancellationToken).ConfigureAwait(false);
    }

    private static void RequireSupportedFrames(IReadOnlyList<TimeFrame> frames)
    {
        foreach (TimeFrame frame in frames)
        {
            RequireSupportedTimeFrame(frame);
        }
    }

    private static async Task<IReadOnlyList<string>> RunScreenerTemplateCoreAsync(
        IDataService dataService,
        IScreenerTemplateEvaluator templateEvaluator,
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(comparisonSymbols);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }

        var canonical = symbols.Select(RequireCanonical).ToList();
        int requiredCount = RequiredCandleCount(template);
        ScreenerSecondaryCandles? secondary = await LoadComparisonCandlesAsync(dataService, comparisonSymbols, timeFrame, requiredCount, cancellationToken).ConfigureAwait(false);
        HashSet<string> matched = secondary is null
            ? await templateEvaluator.EvaluateAsync(canonical, template, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : await templateEvaluator.EvaluateAsync(canonical, template, timeFrame, secondary, new Progress<int>(), cancellationToken).ConfigureAwait(false);
        return matched.ToList();
    }

    /// <summary>The most bars any enabled entry of <paramref name="template"/> needs (0 = the whole history), as the Screener sizes each entry's own load.</summary>
    private static int RequiredCandleCount(ScreenerIndicatorTemplate template)
    {
        int required = 0;
        foreach (ScreenerIndicatorEntry entry in template.Entries.Where(e => e.IsEnabled))
        {
            int count = ScreenerHistoryRequirements.GetRequiredCandleCount(entry);
            if (count == 0)
            {
                return 0;
            }

            required = Math.Max(required, count);
        }

        return required;
    }

    public Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default)
        => RunBacktestCoreAsync(snapshot, sharedBars: null, cancellationToken);

    public Task<IReadOnlyList<BacktestEvaluationArtifact>> RunBacktestBatchAsync(
        IReadOnlyList<BacktestRunSnapshot> snapshots,
        int degreeOfParallelism,
        CancellationToken cancellationToken = default)
        => RunBacktestBatchAsync(snapshots, degreeOfParallelism, artifact => artifact, cancellationToken);

    public async Task<IReadOnlyList<TResult>> RunBacktestBatchAsync<TResult>(
        IReadOnlyList<BacktestRunSnapshot> snapshots,
        int degreeOfParallelism,
        Func<BacktestEvaluationArtifact, TResult> project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfLessThan(degreeOfParallelism, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshots.Count == 0)
        {
            return Array.Empty<TResult>();
        }

        var sharedBars = new BacktestBarCache();
        var results = new TResult[snapshots.Count];
        var failures = new ExceptionDispatchInfo?[snapshots.Count];
        int lowestFailedIndex = int.MaxValue;
        var options = new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism, CancellationToken = cancellationToken };

        // Indices are handed out in ascending order. A run above an already failed index cannot change which failure is reported (the lowest
        // one wins), so it is skipped; a run below it still executes. Hence the reported failure is the one a sequential loop would hit first.
        await Parallel.ForEachAsync(Enumerable.Range(0, snapshots.Count), options, async (index, token) =>
        {
            if (index > Volatile.Read(ref lowestFailedIndex))
            {
                return;
            }

            try
            {
                results[index] = project(await RunBacktestCoreAsync(snapshots[index], sharedBars, token).ConfigureAwait(false));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                failures[index] = ExceptionDispatchInfo.Capture(ex);
                int observed = Volatile.Read(ref lowestFailedIndex);
                while (index < observed)
                {
                    int previous = Interlocked.CompareExchange(ref lowestFailedIndex, index, observed);
                    if (previous == observed)
                    {
                        break;
                    }

                    observed = previous;
                }
            }
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (lowestFailedIndex != int.MaxValue)
        {
            failures[lowestFailedIndex]!.Throw();
        }

        return results;
    }

    private async Task<BacktestEvaluationArtifact> RunBacktestCoreAsync(BacktestRunSnapshot snapshot, BacktestBarCache? sharedBars, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string canonical = RequireCanonical(snapshot.Symbol);
        if (!ReadOnlyGatewayContract.SupportedBacktestTimeFrames.Contains(snapshot.Frame))
        {
            throw new NotSupportedException($"Time frame {snapshot.Frame} is not supported by the read-only backtest.");
        }

        if (snapshot.Configuration.ExecutionModel != ExecutionModel.Legacy)
        {
            throw new NotSupportedException($"Execution model {snapshot.Configuration.ExecutionModel} is not supported by the read-only backtest.");
        }

        BacktestRunSnapshot canonicalSnapshot = snapshot with { Symbol = canonical };
        PreparedBacktestRun prepared = await (sharedBars is null
            ? _backtestPreparation.PrepareAsync(canonicalSnapshot, cancellationToken)
            : _backtestPreparation.PrepareAsync(canonicalSnapshot, sharedBars, cancellationToken)).ConfigureAwait(false);
        return await Task.Run(
            () => _backtestEvaluation.Evaluate(
                prepared.Input,
                prepared.Configuration,
                prepared.StrategySpecification,
                prepared.ReportOptions,
                samplingEvidence: null,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static string RequireCanonical(string symbol)
    {
        if (!TickerSymbol.IsWellFormed(symbol))
        {
            throw new ArgumentException("Symbol is not well formed.", nameof(symbol));
        }

        return TickerSymbol.Canonicalize(symbol);
    }
}
