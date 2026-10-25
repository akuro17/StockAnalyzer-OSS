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
        List<CandleData> dailyUpToDate = daily.Where(bar => DateOnly.FromDateTime(bar.Timestamp) <= endDate).ToList();
        IReadOnlyList<CandleData> upToDate = TimeFrameAggregator.Aggregate(dailyUpToDate, timeFrame);
        return upToDate.Count > count ? upToDate.Skip(upToDate.Count - count).ToList() : upToDate;
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

    public async Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(comparisonSymbols);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }

        var canonical = symbols.Select(RequireCanonical).ToList();
        ScreenerSecondaryCandles? secondary = await LoadComparisonCandlesAsync(comparisonSymbols, timeFrame, ScreenerHistoryRequirements.GetRequiredCandleCount(condition), cancellationToken).ConfigureAwait(false);
        return secondary is null
            ? await _screener.ScreenAsync(canonical, condition, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : await _screener.ScreenAsync(canonical, condition, timeFrame, secondary, new Progress<int>(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The bars of the comparison symbols with the history the condition needs, read once for the whole scan; null when there is no comparison symbol.</summary>
    private async Task<ScreenerSecondaryCandles?> LoadComparisonCandlesAsync(IReadOnlyList<string> comparisonSymbols, TimeFrame timeFrame, int requiredCount, CancellationToken cancellationToken)
        => comparisonSymbols.Count == 0
            ? null
            : await ScreenerSecondaryCandles.LoadAsync(_dataService, comparisonSymbols.Select(RequireCanonical), timeFrame, requiredCount, cancellationToken).ConfigureAwait(false);

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

    public async Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
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
        ScreenerSecondaryCandles? secondary = await LoadComparisonCandlesAsync(comparisonSymbols, timeFrame, requiredCount, cancellationToken).ConfigureAwait(false);
        HashSet<string> matched = secondary is null
            ? await _templateEvaluator.EvaluateAsync(canonical, template, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false)
            : await _templateEvaluator.EvaluateAsync(canonical, template, timeFrame, secondary, new Progress<int>(), cancellationToken).ConfigureAwait(false);
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

    public async Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string canonical = RequireCanonical(snapshot.Symbol);
        if (snapshot.Frame != ReadOnlyGatewayContract.BacktestTimeFrame)
        {
            throw new NotSupportedException($"Time frame {snapshot.Frame} is not supported by the read-only backtest.");
        }

        if (snapshot.Configuration.ExecutionModel != ExecutionModel.Legacy)
        {
            throw new NotSupportedException($"Execution model {snapshot.Configuration.ExecutionModel} is not supported by the read-only backtest.");
        }

        PreparedBacktestRun prepared = await _backtestPreparation.PrepareAsync(snapshot with { Symbol = canonical }, cancellationToken).ConfigureAwait(false);
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
