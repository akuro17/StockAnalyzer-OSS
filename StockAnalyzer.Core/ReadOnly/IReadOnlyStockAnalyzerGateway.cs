using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// The complete surface a read-only host (the MCP server) may use. Every member reads; none writes, deletes,
/// starts a process or contacts an external service. Symbols are validated with <see cref="TickerSymbol.IsWellFormed"/>
/// and canonicalised with <see cref="TickerSymbol.Canonicalize"/>.
/// </summary>
public interface IReadOnlyStockAnalyzerGateway
{
    /// <summary>Canonical symbols of all available tickers.</summary>
    Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Latest <paramref name="count"/> bars of <paramref name="symbol"/> in chronological order.
    /// Uses a new cache per call, so a call never sees data cached by an earlier call.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is not well formed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="NotSupportedException"><paramref name="timeFrame"/> is not in <see cref="ReadOnlyGatewayContract.SupportedTimeFrames"/>.</exception>
    Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest <paramref name="count"/> bars of <paramref name="symbol"/> whose timestamp date is on or before <paramref name="endDate"/>, in chronological
    /// order (fewer when fewer are stored; empty when none is). The whole stored daily history is read for the call and cut at the date, so the date may be older
    /// than the newest <paramref name="count"/> bars, which <see cref="GetCandlesAsync"/> cannot reach. A weekly or monthly bar is aggregated from the days up to
    /// <paramref name="endDate"/> only (it is dated by its first day and its high, low, close and volume never include a later day). Uses a new cache per call.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is not well formed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="NotSupportedException"><paramref name="timeFrame"/> is not in <see cref="ReadOnlyGatewayContract.SupportedTimeFrames"/>.</exception>
    Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default);

    /// <summary>Stored metadata for <paramref name="symbol"/>, or null when none is stored. Never fetches from an external source.</summary>
    /// <exception cref="DataUnavailableException">The stored file exists but could not be read.</exception>
    Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Last write time of the stored time series of <paramref name="symbol"/>, or null.</summary>
    Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates the indicators on a thread-pool thread. The returned task is the underlying calculation itself:
    /// the pipeline has no cancellation token, so a caller that stops waiting (timeout, cancellation) leaves the
    /// calculation running and owns the decision about when its resources are released.
    /// </summary>
    Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(
        IReadOnlyList<CoreCandleData> candles,
        IEnumerable<CoreIndicatorSettings> settings,
        IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null);

    /// <summary>
    /// The Reverse Watch curve of <paramref name="candles"/> with the application's own service (the chart's calculation), or null when it cannot be
    /// calculated (for example fewer bars than <paramref name="period"/>). The phase of every point is relative to the last <paramref name="dataCount"/> points.
    /// </summary>
    ReverseWatchCurveData? CalculateReverseWatch(
        IReadOnlyList<CoreCandleData> candles,
        int period,
        string symbol,
        bool isMaBased = true,
        bool isLogScaleVolume = false,
        int dataCount = 0);

    /// <summary>
    /// Calculates the series, effective sizing, DataWindow metrics and optional pattern analysis for <paramref name="request"/>
    /// over <paramref name="candles"/> (HeikinAshi, Renko, Kagi, PointAndFigure, ThreeLineBreak).
    /// </summary>
    ChartSeriesCalculationResult CalculateChartSeries(
        IReadOnlyList<CoreCandleData> candles,
        ChartSeriesRequest request,
        CancellationToken cancellationToken = default)
        => new StockAnalyzer.Core.Services.Analysis.ChartSeriesCalculationService().Calculate(candles, request, cancellationToken);

    /// <summary>
    /// Evaluates <paramref name="condition"/> on the stored bars of every symbol in <paramref name="symbols"/> with the Screener's own service and returns the
    /// symbols that meet it, in the order of <paramref name="symbols"/>. Metadata is read from the stored files only. Cancellation stops the scan.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol is not well formed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The condition uses an Offset above the configured maximum.</exception>
    Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="RunScreenerAsync(IReadOnlyList{string}, IScreeningCondition, TimeFrame, CancellationToken)"/> for a condition whose indicators compare
    /// two tickers: the stored bars of <paramref name="comparisonSymbols"/> are read once, with the history the condition needs, and shared by the
    /// evaluation of every symbol. A symbol of the list that is also evaluated is compared with itself. An empty list is the overload above.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol is not well formed.</exception>
    Task<IReadOnlyList<string>> RunScreenerAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
        => comparisonSymbols.Count == 0
            ? RunScreenerAsync(symbols, condition, timeFrame, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} does not evaluate indicators that compare two tickers.");

    /// <summary>
    /// The scan of <see cref="RunScreenerAsync(IReadOnlyList{string}, IScreeningCondition, TimeFrame, IReadOnlyList{string}, CancellationToken)"/> over the bars of
    /// <paramref name="selection"/>: with an as-of date each symbol is evaluated on its last bar dated on or before it (a weekly or monthly bar is built from the days up
    /// to the date), the comparison symbols too; a weekly or monthly frame is built from the stored daily bars; <see cref="ScreenerBarSelection.CompleteBarsOnly"/> leaves out
    /// a last period that has not ended. A symbol with no bar to evaluate is returned in <see cref="ScreenerBarsResult.SymbolsWithoutBars"/>. Metadata is the stored snapshot.
    /// </summary>
    /// <exception cref="ArgumentException">A symbol is not well formed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The condition uses an Offset above the configured maximum.</exception>
    /// <exception cref="NotSupportedException">The host cannot rebuild its screener over another data source.</exception>
    Task<ScreenerBarsResult> RunScreenerOnBarsAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not run a scan on a selection of bars.");

    /// <summary>
    /// The latest <paramref name="count"/> bars of <paramref name="symbol"/> that a scan over <paramref name="selection"/> evaluates (the same cut, aggregation and partial-period
    /// rule), in chronological order; empty when there is none. Used to show the values a scan read. Uses a new cache per call.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="symbol"/> is not well formed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="NotSupportedException"><paramref name="timeFrame"/> is not in <see cref="ReadOnlyGatewayContract.SupportedTimeFrames"/>.</exception>
    Task<IReadOnlyList<CandleData>> GetScreenerBarsAsync(
        string symbol,
        TimeFrame timeFrame,
        ScreenerBarSelection selection,
        int count,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not read a selection of bars.");

    /// <summary>The saved Screener templates (favourites first, then by name). Reads the template files only: nothing is created, migrated or written.</summary>
    Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The saved Screener template with <paramref name="id"/>, or null when there is none or its file cannot be read.</summary>
    Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The saved backtest-condition templates (favourites first, then by name). Reads the template files only: nothing is created, migrated or written.</summary>
    Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>The saved backtest-condition template with <paramref name="id"/>, or null when there is none or its file cannot be read.</summary>
    Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the enabled entries of <paramref name="template"/> with the Screener's own template evaluator (entries are combined by the
    /// And/Or of the preceding entry, as the Screener tab does) and returns the symbols that meet it, in no particular order.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An entry uses an Offset above the configured maximum.</exception>
    Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="RunScreenerTemplateAsync(IReadOnlyList{string}, ScreenerIndicatorTemplate, TimeFrame, CancellationToken)"/> for a template whose
    /// indicators compare two tickers; <paramref name="comparisonSymbols"/> are read once as in the Screener overload. An empty list is the overload above.
    /// </summary>
    Task<IReadOnlyList<string>> RunScreenerTemplateAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        CancellationToken cancellationToken = default)
        => comparisonSymbols.Count == 0
            ? RunScreenerTemplateAsync(symbols, template, timeFrame, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} does not evaluate indicators that compare two tickers.");

    /// <summary>
    /// <see cref="RunScreenerOnBarsAsync"/> for a saved template: the enabled entries are combined as in
    /// <see cref="RunScreenerTemplateAsync(IReadOnlyList{string}, ScreenerIndicatorTemplate, TimeFrame, IReadOnlyList{string}, CancellationToken)"/>, over the bars of <paramref name="selection"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An entry uses an Offset above the configured maximum.</exception>
    /// <exception cref="NotSupportedException">The host cannot rebuild its screener over another data source.</exception>
    Task<ScreenerBarsResult> RunScreenerTemplateOnBarsAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        TimeFrame timeFrame,
        IReadOnlyList<string> comparisonSymbols,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not run a scan on a selection of bars.");

    /// <summary>
    /// The scan in which every side of <paramref name="condition"/> reads the bars of its OWN time frame (<see cref="IScreenerService.ScreenWithSideFramesAsync"/>), built from the
    /// stored daily bars: with an as-of date each frame is built from the days up to it, <see cref="ScreenerBarSelection.CompleteBarsOnly"/> leaves out a last weekly or monthly period that
    /// has not ended. Matched symbols are in the order of <paramref name="symbols"/>. Indicators that compare two tickers are not evaluated (no comparison symbols are read).
    /// </summary>
    /// <exception cref="ArgumentException">A symbol is not well formed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The condition uses an Offset above the configured maximum.</exception>
    /// <exception cref="NotSupportedException">A side reads a time frame outside <see cref="ReadOnlyGatewayContract.SupportedTimeFrames"/>, or the host does not scan sides on their own time frames.</exception>
    Task<ScreenerSideFramesResult> RunScreenerWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        IScreeningCondition condition,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not scan sides on their own time frames.");

    /// <summary>
    /// <see cref="RunScreenerWithSideFramesAsync"/> for a saved template: the enabled entries are combined as in
    /// <see cref="RunScreenerTemplateAsync(IReadOnlyList{string}, ScreenerIndicatorTemplate, TimeFrame, CancellationToken)"/>, each side reading its own stored time frame; the
    /// reports of the entries are merged. Matched symbols are in no particular order.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An entry uses an Offset above the configured maximum.</exception>
    /// <exception cref="NotSupportedException">A side reads a time frame outside <see cref="ReadOnlyGatewayContract.SupportedTimeFrames"/>, or the host does not scan sides on their own time frames.</exception>
    Task<ScreenerSideFramesResult> RunScreenerTemplateWithSideFramesAsync(
        IReadOnlyList<string> symbols,
        ScreenerIndicatorTemplate template,
        ScreenerBarSelection selection,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not scan sides on their own time frames.");

    /// <summary>
    /// Runs one backtest in memory with the application's own preparation service and evaluation service (the Backtest window's path):
    /// the stored bars are read, the engine simulates, the report and the run fingerprint are built. Nothing is saved or exported.
    /// Cancellation stops the simulation at the next bar.
    /// </summary>
    /// <exception cref="ArgumentException">The snapshot symbol is not well formed or the snapshot is rejected by the engine's validation.</exception>
    /// <exception cref="NotSupportedException">The execution model or the time frame is not supported by the read-only host.</exception>
    Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default);
}
