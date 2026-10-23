using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
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

    public ReadOnlyStockAnalyzerGateway(
        IReadOnlyMarketDataSource marketData,
        IDataService dataService,
        IAnalysisPipelineService pipeline,
        IScreenerService screener,
        IReadOnlyTemplateSource templates,
        IScreenerTemplateEvaluator templateEvaluator,
        IBacktestRunPreparationService backtestPreparation,
        IBacktestEvaluationService backtestEvaluation)
    {
        _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _screener = screener ?? throw new ArgumentNullException(nameof(screener));
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        _templateEvaluator = templateEvaluator ?? throw new ArgumentNullException(nameof(templateEvaluator));
        _backtestPreparation = backtestPreparation ?? throw new ArgumentNullException(nameof(backtestPreparation));
        _backtestEvaluation = backtestEvaluation ?? throw new ArgumentNullException(nameof(backtestEvaluation));
    }

    public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
        => _marketData.GetAvailableTickersAsync(cancellationToken);

    public async Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
    {
        string canonical = RequireCanonical(symbol);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return await new TimeFrameManager(_dataService).GetCandlesAsync(canonical, timeFrame, count, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(condition);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }

        var canonical = symbols.Select(RequireCanonical).ToList();
        return await _screener.ScreenAsync(canonical, condition, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false);
    }

    public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default)
        => _templates.ListScreenerTemplatesAsync(cancellationToken);

    public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => _templates.GetScreenerTemplateAsync(id, cancellationToken);

    public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default)
        => _templates.ListBacktestConditionTemplatesAsync(cancellationToken);

    public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default)
        => _templates.GetBacktestConditionTemplateAsync(id, cancellationToken);

    public async Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(template);
        if (!ReadOnlyGatewayContract.SupportedTimeFrames.Contains(timeFrame))
        {
            throw new NotSupportedException($"Time frame {timeFrame} is not supported.");
        }

        var canonical = symbols.Select(RequireCanonical).ToList();
        HashSet<string> matched = await _templateEvaluator.EvaluateAsync(canonical, template, timeFrame, new Progress<int>(), cancellationToken).ConfigureAwait(false);
        return matched.ToList();
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
