using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
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
    /// Runs one backtest in memory with the application's own preparation service and evaluation service (the Backtest window's path):
    /// the stored bars are read, the engine simulates, the report and the run fingerprint are built. Nothing is saved or exported.
    /// Cancellation stops the simulation at the next bar.
    /// </summary>
    /// <exception cref="ArgumentException">The snapshot symbol is not well formed or the snapshot is rejected by the engine's validation.</exception>
    /// <exception cref="NotSupportedException">The execution model or the time frame is not supported by the read-only host.</exception>
    Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default);
}
