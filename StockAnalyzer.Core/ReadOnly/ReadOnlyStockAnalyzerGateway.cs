using System.Runtime.ExceptionServices;
using StockAnalyzer.Core.Helpers;
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
public sealed class ReadOnlyStockAnalyzerGateway : IReadOnlyStockAnalyzerGateway, ICancellableIndicatorCalculation, ITimeFrameAwareIndicatorCalculation
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
    private readonly IRsRatingClosesProvider? _rsRatingClosesProvider;
    private readonly IMarketBreadthSeriesProvider? _marketBreadthProvider;

    public ReadOnlyStockAnalyzerGateway(
        IReadOnlyMarketDataSource marketData,
        IDataService dataService,
        IAnalysisPipelineService pipeline,
        IScreenerService screener,
        IReadOnlyTemplateSource templates,
        IScreenerTemplateEvaluator templateEvaluator,
        IBacktestRunPreparationService backtestPreparation,
        IBacktestEvaluationService backtestEvaluation,
        IChartSeriesCalculationService? chartSeriesCalculation = null,
        IRsRatingClosesProvider? rsRatingClosesProvider = null,
        IMarketBreadthSeriesProvider? marketBreadthProvider = null)
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
        _rsRatingClosesProvider = rsRatingClosesProvider;
        _marketBreadthProvider = marketBreadthProvider;
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
    {
        string canonical = RequireCanonical(symbol);
        if (SyntheticSymbolParser.IsSyntheticExpression(canonical))
        {
            return null;
        }

        return await _marketData.TryGetStoredMetadataAsync(canonical, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default)
    {
        string canonical = RequireCanonical(symbol);
        if (SyntheticSymbolParser.TryParse(canonical, out var expr))
        {
            DateTimeOffset? left = await _marketData.GetTimeSeriesLastUpdatedAsync(expr.LeftSymbol, cancellationToken).ConfigureAwait(false);
            DateTimeOffset? right = await _marketData.GetTimeSeriesLastUpdatedAsync(expr.RightSymbol, cancellationToken).ConfigureAwait(false);
            if (left is null || right is null)
            {
                return left ?? right;
            }

            return left < right ? left : right;
        }

        return await _marketData.GetTimeSeriesLastUpdatedAsync(canonical, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SymbolCoverage> GetCoverageAsync(string symbol, CancellationToken cancellationToken = default)
    {
        string canonical = RequireCanonical(symbol);
        if (SyntheticSymbolParser.TryParse(canonical, out var expr))
        {
            return await GetSyntheticCoverageAsync(canonical, cancellationToken).ConfigureAwait(false);
        }

        var tickers = await GetAvailableTickersAsync(cancellationToken).ConfigureAwait(false);
        bool inList = tickers.Contains(canonical, StringComparer.OrdinalIgnoreCase);

        TickerMetadata? metadata = null;
        try
        {
            metadata = await GetStoredTickerMetadataAsync(canonical, cancellationToken).ConfigureAwait(false);
        }
        catch (DataUnavailableException)
        {
            // Unreadable stored metadata treated as null
        }

        DateTimeOffset? lastUpdated = await GetTimeSeriesLastUpdatedAsync(canonical, cancellationToken).ConfigureAwait(false);

        var timeFrames = new List<TimeFrameCoverage>();
        var unreadable = new List<TimeFrame>();

        // Weekly and monthly bars are aggregated from the daily file, so the daily file decides all of them. A data service that can tell a missing
        // file from an unreadable one is asked once: no file means no bars in any time frame, an unreadable file makes every time frame unreadable
        // (and the file is not touched again), and a file that was read is not read a second time.
        IDataService barSource = _dataService;
        IReadOnlyList<TimeFrame> toRead = ReadOnlyGatewayContract.SupportedTimeFrames;
        if (_dataService is IDataReadStatusSource statusSource)
        {
            DataReadResult daily = await statusSource.LoadCandlesWithStatusAsync(canonical, TimeFrame.D1, 0, cancellationToken).ConfigureAwait(false);
            switch (daily.Status)
            {
                case DataReadStatus.FileMissing:
                    toRead = Array.Empty<TimeFrame>();
                    break;
                case DataReadStatus.Unreadable:
                    unreadable.AddRange(ReadOnlyGatewayContract.SupportedTimeFrames);
                    toRead = Array.Empty<TimeFrame>();
                    break;
                default:
                    barSource = new LoadedDailyDataService(daily.Candles);
                    break;
            }
        }

        var manager = new TimeFrameManager(barSource);

        foreach (TimeFrame tf in toRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> candles;
            try
            {
                candles = await manager.GetCandlesAsync(canonical, tf, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stored but unreadable is not "no data": name the time frame, keep the other time frames and say nothing about the cause.
                unreadable.Add(tf);
                continue;
            }

            if (candles.Count > 0)
            {
                timeFrames.Add(new TimeFrameCoverage(
                    tf,
                    candles.Count,
                    candles[0].Timestamp,
                    candles[^1].Timestamp));
            }
        }

        bool found = inList && timeFrames.Count > 0;
        return new SymbolCoverage(
            canonical,
            found,
            metadata is not null,
            lastUpdated,
            timeFrames)
        {
            UnreadableTimeFrames = unreadable,
        };
    }

    private async Task<SymbolCoverage> GetSyntheticCoverageAsync(string canonical, CancellationToken cancellationToken)
    {
        var synthTimeFrames = new List<TimeFrameCoverage>();
        var synthUnreadable = new List<TimeFrame>();
        var synthManager = new TimeFrameManager(_dataService);

        foreach (TimeFrame tf in ReadOnlyGatewayContract.SupportedTimeFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CandleData> candles;
            try
            {
                candles = await synthManager.GetCandlesAsync(canonical, tf, 0, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                synthUnreadable.Add(tf);
                continue;
            }

            if (candles.Count > 0)
            {
                synthTimeFrames.Add(new TimeFrameCoverage(
                    tf,
                    candles.Count,
                    candles[0].Timestamp,
                    candles[^1].Timestamp));
            }
        }

        DateTimeOffset? synthLastUpdated = await GetTimeSeriesLastUpdatedAsync(canonical, cancellationToken).ConfigureAwait(false);
        return new SymbolCoverage(
            canonical,
            Found: false,
            HasMetadata: false,
            synthLastUpdated,
            synthTimeFrames)
        {
            UnreadableTimeFrames = synthUnreadable,
        };
    }

    /// <summary>Answers daily requests from bars that were already read, so aggregating them needs no second read of the file.</summary>
    private sealed class LoadedDailyDataService : IDataService
    {
        private readonly IReadOnlyList<CandleData> _daily;

        public LoadedDailyDataService(IReadOnlyList<CandleData> daily) => _daily = daily;

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            if (timeFrame != TimeFrame.D1)
            {
                return Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
            }

            return Task.FromResult<IReadOnlyList<CandleData>>(count > 0 && _daily.Count > count ? _daily.Skip(_daily.Count - count).ToList() : _daily);
        }
    }

    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null)
        => Task.Run(() => _pipeline.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol));

    /// <inheritdoc />
    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol,
        CancellationToken cancellationToken)
        => Task.Run(() => _pipeline is ICancellableIndicatorCalculation cancellable
            ? cancellable.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol, cancellationToken)
            : _pipeline.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol));

    /// <inheritdoc />
    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
        => CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol, timeFrame, endDate: null, cancellationToken);

    /// <inheritdoc />
    public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol,
        TimeFrame timeFrame,
        DateOnly? endDate,
        CancellationToken cancellationToken = default)
        => Task.Run(() => _pipeline is ITimeFrameAwareIndicatorCalculation tfAware
            ? tfAware.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol, timeFrame, endDate, cancellationToken)
            : _pipeline is ICancellableIndicatorCalculation cancellable
                ? cancellable.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol, cancellationToken)
                : _pipeline.CalculateIndicatorsAsync(candles, settings, secondaryCandlesBySymbol));

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

    public async Task<IScreenerEvaluationContext?> EvaluateScreenerContextAsync(
        IReadOnlyList<string> symbols,
        DateOnly? asOfDate = null,
        CancellationToken cancellationToken = default)
    {
        if (asOfDate.HasValue)
        {
            return ScreenerEvaluationContext.Empty;
        }

        if (_rsRatingClosesProvider == null)
        {
            return null;
        }

        var canonical = symbols.Select(RequireCanonical).ToList();
        return await ScreenerRsRatingInspector
            .ComputeContextAsync(_rsRatingClosesProvider, canonical, asOfDate: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IScreenerEvaluationContext?> EvaluateUniverseBreadthContextAsync(
        IReadOnlyList<UniverseBreadthKey> keys,
        IScreenerEvaluationContext? baseContext,
        TimeFrame? scanTimeFrame = null,
        CancellationToken cancellationToken = default)
        => ScreenerUniverseBreadthInspector.ComputeContextAsync(_marketBreadthProvider, keys, baseContext, scanTimeFrame, cancellationToken);

    public Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
        => RunScreenerAsync(symbols, condition, timeFrame, comparisonSymbols, evaluationContext: null, cancellationToken);

    public Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        IScreenerEvaluationContext? evaluationContext,
        CancellationToken cancellationToken = default)
        => RunScreenerCoreAsync(_dataService, _screener, symbols, condition, timeFrame, comparisonSymbols, evaluationContext, cancellationToken);

    public Task<ScreenerBarsResult> RunScreenerOnBarsAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => RunScreenerOnBarsAsync(symbols, condition, timeFrame, comparisonSymbols, selection, evaluationContext: null, cancellationToken);

    public async Task<ScreenerBarsResult> RunScreenerOnBarsAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        IScreenerEvaluationContext? evaluationContext,
        CancellationToken cancellationToken = default)
    {
        var bars = new ScreenerBarsDataService(_dataService, selection);
        IScreenerEvaluationContext? effectiveContext = selection.AsOfDate is { } asOf
            ? ScreenerEvaluationContext.ForAsOfDate(asOf, evaluationContext)
            : evaluationContext;
        IReadOnlyList<string> matched = await RunScreenerCoreAsync(bars, ScreenerOver(bars), symbols, condition, timeFrame, comparisonSymbols, effectiveContext, cancellationToken).ConfigureAwait(false);
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
        IScreenerEvaluationContext? evaluationContext,
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
        if (evaluationContext is not null)
        {
            return await screener.ScreenAsync(canonical, condition, timeFrame, secondary, evaluationContext, new Progress<int>(), cancellationToken).ConfigureAwait(false);
        }

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
        => RunScreenerTemplateAsync(symbols, template, timeFrame, comparisonSymbols, evaluationContext: null, cancellationToken);

    public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        IScreenerEvaluationContext? evaluationContext,
        CancellationToken cancellationToken = default)
        => RunScreenerTemplateCoreAsync(_dataService, _templateEvaluator, symbols, template, timeFrame, comparisonSymbols, evaluationContext, cancellationToken);

    public Task<ScreenerBarsResult> RunScreenerTemplateOnBarsAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => RunScreenerTemplateOnBarsAsync(symbols, template, timeFrame, comparisonSymbols, selection, evaluationContext: null, cancellationToken);

    public async Task<ScreenerBarsResult> RunScreenerTemplateOnBarsAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        IScreenerEvaluationContext? evaluationContext,
        CancellationToken cancellationToken = default)
    {
        var bars = new ScreenerBarsDataService(_dataService, selection);
        IScreenerEvaluationContext? effectiveContext = selection.AsOfDate is { } asOf
            ? ScreenerEvaluationContext.ForAsOfDate(asOf, evaluationContext)
            : evaluationContext;
        var evaluator = new ScreenerTemplateEvaluator(ScreenerOver(bars), selection.AsOfDate.HasValue ? null : _rsRatingClosesProvider, _marketBreadthProvider);
        IReadOnlyList<string> matched = await RunScreenerTemplateCoreAsync(bars, evaluator, symbols, template, timeFrame, comparisonSymbols, effectiveContext, cancellationToken).ConfigureAwait(false);
        return ResultOf(matched, bars, symbols);
    }

    public Task<ScreenerSideFramesResult> RunScreenerWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => RunScreenerWithSideFramesAsync(symbols, condition, selection, evaluationContext: null, cancellationToken);

    public async Task<ScreenerSideFramesResult> RunScreenerWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        ScreenerBarSelection selection,
        IScreenerEvaluationContext? evaluationContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(selection);
        RequireSupportedFrames(ScreenerSideFrames.Collect(condition));
        IScreenerEvaluationContext? effectiveContext = selection.AsOfDate is { } asOf
            ? ScreenerEvaluationContext.ForAsOfDate(asOf, evaluationContext)
            : evaluationContext;
        var canonical = symbols.Select(RequireCanonical).ToList();
        return effectiveContext is null
            ? await _screener.ScreenWithSideFramesAsync(canonical, condition, selection, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : await _screener.ScreenWithSideFramesAsync(canonical, condition, selection, effectiveContext, new Progress<int>(), cancellationToken).ConfigureAwait(false);
    }

    public Task<ScreenerSideFramesResult> RunScreenerTemplateWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => RunScreenerTemplateWithSideFramesAsync(symbols, template, selection, evaluationContext: null, cancellationToken);

    public async Task<ScreenerSideFramesResult> RunScreenerTemplateWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        IScreenerEvaluationContext? evaluationContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(selection);
        RequireSupportedFrames(ScreenerSideFrames.Collect(template.Entries));
        IScreenerEvaluationContext? effectiveContext = selection.AsOfDate is { } asOf
            ? ScreenerEvaluationContext.ForAsOfDate(asOf, evaluationContext)
            : evaluationContext;
        var canonical = symbols.Select(RequireCanonical).ToList();
        return effectiveContext is null
            ? await _templateEvaluator.EvaluateWithSideFramesReportAsync(canonical, template, selection, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : await _templateEvaluator.EvaluateWithSideFramesReportAsync(canonical, template, selection, effectiveContext, new Progress<int>(), cancellationToken).ConfigureAwait(false);
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
        IScreenerEvaluationContext? evaluationContext,
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
        HashSet<string> matched = evaluationContext is not null
            ? await templateEvaluator.EvaluateAsync(canonical, template, timeFrame, secondary, evaluationContext, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : (secondary is null
                ? await templateEvaluator.EvaluateAsync(canonical, template, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false)
                : await templateEvaluator.EvaluateAsync(canonical, template, timeFrame, secondary, new Progress<int>(), cancellationToken).ConfigureAwait(false));
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
                BacktestBatchFailure.Tag(ex, index);
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
            () => snapshot.CaptureArithmeticFailures
                ? _backtestEvaluation.EvaluateCapturingArithmeticFailures(
                    prepared.Input,
                    prepared.Configuration,
                    prepared.StrategySpecification,
                    prepared.ReportOptions,
                    samplingEvidence: null,
                    cancellationToken)
                : _backtestEvaluation.Evaluate(
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
        if (SyntheticSymbolParser.TryParse(symbol, out var expr))
        {
            if (!expr.IsOperatorSupported)
            {
                throw new ArgumentException($"Operator '{expr.OperatorChar}' is not supported for custom formula symbols.", nameof(symbol));
            }

            return expr.CanonicalExpression;
        }

        if (!TickerSymbol.IsWellFormed(symbol))
        {
            throw new ArgumentException("Symbol is not well formed.", nameof(symbol));
        }

        return TickerSymbol.Canonicalize(symbol);
    }
}
