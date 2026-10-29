using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// Reads the value of two scatter metrics for every ticker with the Screener's own extractor (<see cref="IScreenerValueExtractor"/>) and its own
/// history sizing (<see cref="ScreenerHistoryRequirements"/>), so a metric shows the number the Screener would compare. The bar read is the
/// ticker's latest bar of the metric's own time frame (Day, Week or Month: the bars the data service reads for that time frame, a Week or Month bar
/// is never built here), moved back by the metric's Offset in bars of that time frame.
/// </summary>
public sealed class ScatterMetricEvaluator : IScatterMetricEvaluator
{
    private readonly IDataService _dataService;
    private readonly IEnumerable<IMarketDataProvider> _marketDataProviders;
    private readonly IScreenerValueExtractor _extractor;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IScreenerCatalogProvider _catalogProvider;
    private readonly IIndicatorFactory _indicatorFactory;
    private readonly ILogger<ScatterMetricEvaluator> _logger;

    public ScatterMetricEvaluator(
        IDataService dataService,
        IEnumerable<IMarketDataProvider> marketDataProviders,
        IScreenerValueExtractor extractor,
        IStockAnalyzerSettings settings,
        IScreenerCatalogProvider catalogProvider,
        IIndicatorFactory? indicatorFactory = null,
        ILogger<ScatterMetricEvaluator>? logger = null)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _marketDataProviders = marketDataProviders ?? Array.Empty<IMarketDataProvider>();
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _logger = logger ?? NullLogger<ScatterMetricEvaluator>.Instance;
    }

    public async Task<ScatterEvaluationResult> ComputeAsync(
        IReadOnlyList<string> tickers,
        ScatterMetricDefinition x,
        ScatterMetricDefinition y,
        CancellationToken ct)
    {
        EnsureValid(x, nameof(x));
        EnsureValid(y, nameof(y));

        // The fixed ranges belong to the metrics, not to the tickers: resolved once, outside the ticker loop.
        AxisRange? xRange = ResolveRange(x);
        AxisRange? yRange = ResolveRange(y);

        if (tickers is null || tickers.Count == 0)
        {
            return new ScatterEvaluationResult
            {
                Items = Array.Empty<ScatterEvaluationItem>(),
                XFixedRange = xRange,
                YFixedRange = yRange,
            };
        }

        // The bars to load per time frame: one load per ticker and time frame, shared by the metrics on that time frame.
        LoadPlan plan = LoadPlan.Create(x, y);

        // The extractor is the Screener's daily one and fails closed on any other time frame, so a Week or Month metric is read through a copy of its
        // source that says Day, over the bars of its own time frame. The copy is made once per call, not per ticker.
        ScreenerIndicatorSideConfig xSource = ExtractionSource(x);
        ScreenerIndicatorSideConfig ySource = ExtractionSource(y);
        bool needsMetadata = x.Source.CategoryType == ScreenerItemCategoryType.Column || y.Source.CategoryType == ScreenerItemCategoryType.Column;
        IMarketDataProvider? metadataProvider = needsMetadata ? _marketDataProviders.FirstOrDefault() : null;

        // Each ticker writes its own slot, so the order of the result does not depend on thread scheduling.
        double[] xs = new double[tickers.Count];
        double[] ys = new double[tickers.Count];
        int failed = 0;

        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _settings.ScatterMaxParallelism };
        await Parallel.ForEachAsync(Enumerable.Range(0, tickers.Count), options, async (index, token) =>
        {
            token.ThrowIfCancellationRequested();
            string ticker = tickers[index];
            xs[index] = double.NaN;
            ys[index] = double.NaN;

            try
            {
                IReadOnlyList<CandleData> xCandles = await _dataService.LoadCandlesAsync(ticker, plan.XTimeFrame, plan.XCount).ConfigureAwait(false);
                IReadOnlyList<CandleData> yCandles = plan.SharesOneLoad
                    ? xCandles
                    : await _dataService.LoadCandlesAsync(ticker, plan.YTimeFrame, plan.YCount).ConfigureAwait(false);
                bool xEmpty = xCandles is null || xCandles.Count == 0;
                bool yEmpty = yCandles is null || yCandles.Count == 0;
                if (xEmpty && yEmpty)
                {
                    return;
                }

                TickerMetadata metadata = default;
                if (metadataProvider is not null)
                {
                    try
                    {
                        metadata = await metadataProvider.GetMetadataAsync(ticker).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Best-effort metadata, as in the Screener: a Column then has no value for this ticker.
                    }
                }

                // Both reads happen with no await in between.
                decimal? xv = xEmpty ? null : _extractor.ExtractValueNullable(xSource, xCandles!, metadata);
                decimal? yv = yEmpty ? null : _extractor.ExtractValueNullable(ySource, yCandles!, metadata);

                // The only decimal -> double conversion of the evaluation: the scatter plots in double data coordinates.
                xs[index] = xv is null ? double.NaN : (double)xv.Value;
                ys[index] = yv is null ? double.NaN : (double)yv.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[ScatterMetricEvaluator] Evaluation failed for ticker '{Ticker}'", ticker);
                xs[index] = double.NaN;
                ys[index] = double.NaN;
                Interlocked.Increment(ref failed);
            }
        }).ConfigureAwait(false);

        var items = new ScatterEvaluationItem[tickers.Count];
        int valid = 0;
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new ScatterEvaluationItem(tickers[i], xs[i], ys[i]);
            if (double.IsFinite(xs[i]) && double.IsFinite(ys[i]))
            {
                valid++;
            }
        }

        return new ScatterEvaluationResult
        {
            Items = items,
            TotalCount = items.Length,
            ValidCount = valid,
            FailedCount = failed,
            XFixedRange = xRange,
            YFixedRange = yRange,
        };
    }

    private void EnsureValid(ScatterMetricDefinition metric, string parameterName)
    {
        ScatterMetricInvalidReason reason = ScatterMetricRules.Validate(metric, _settings.ScatterMaxMetricOffset, _indicatorFactory);
        if (reason != ScatterMetricInvalidReason.None)
        {
            throw new ArgumentException($"The scatter metric cannot be evaluated: {reason}.", parameterName);
        }
    }

    private AxisRange? ResolveRange(ScatterMetricDefinition metric)
        => ScatterAxisRangeResolver.TryResolve(metric, _catalogProvider, _indicatorFactory, out AxisRange range) ? range : null;

    /// <summary>The source the extractor reads: the metric's own for Day, a copy that says Day for Week and Month (the bars given to it are those of the metric's time frame).</summary>
    private static ScreenerIndicatorSideConfig ExtractionSource(ScatterMetricDefinition metric)
        => metric.Source.WithTimeFrame(TimeFrame.D1);

    private static int RequiredCount(ScatterMetricDefinition metric)
        => ScreenerHistoryRequirements.GetRequiredCandleCount(new ScreenerIndicatorEntry
        {
            LeftHand = metric.Source.Clone(),
            TargetMode = RightHandTargetMode.NumericValue,
            Operator = ComparisonOperator.GreaterThan,
        });

    /// <summary>
    /// What to load per ticker. Metrics on the same time frame share one load of the larger of their two Screener requirements (0 = all history when
    /// either asks for it), so each reads the very same list; metrics on different time frames load their own bars, each with its own requirement and
    /// never influenced by the other. A count is in bars of the time frame it is loaded for.
    /// </summary>
    private readonly record struct LoadPlan(TimeFrame XTimeFrame, int XCount, TimeFrame YTimeFrame, int YCount)
    {
        public bool SharesOneLoad => XTimeFrame == YTimeFrame;

        public static LoadPlan Create(ScatterMetricDefinition x, ScatterMetricDefinition y)
        {
            TimeFrame xFrame = x.Source.TimeFrame;
            TimeFrame yFrame = y.Source.TimeFrame;
            int xCount = RequiredCount(x);
            int yCount = RequiredCount(y);
            if (xFrame != yFrame)
            {
                return new LoadPlan(xFrame, xCount, yFrame, yCount);
            }

            int shared = xCount == 0 || yCount == 0 ? 0 : Math.Max(xCount, yCount);
            return new LoadPlan(xFrame, shared, yFrame, shared);
        }
    }
}
