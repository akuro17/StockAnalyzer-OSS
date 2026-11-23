using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// Pre-checks stored bars before backtest execution and formats path-free OHLCV invariant diagnostics.
/// Never repairs, clamps, skips, or loosens invalid bars.
/// </summary>
internal static class BacktestBarIntegrityValidator
{
    private const string CoverageHint =
        "Call sa_get_symbol_coverage to inspect invalidBarCount and firstInvalidBarDate, then re-fetch the symbol's historical data.";

    public static async Task<IReadOnlyList<CandleData>> ResolveCoverageWindowAsync(
        IReadOnlyStockAnalyzerGateway gateway,
        string symbol,
        TimeFrame timeFrame,
        IReadOnlyList<CandleData> loadedStored,
        DateOnly earliestNeededDate,
        int configuredLimit,
        CancellationToken cancellationToken)
    {
        if (loadedStored.Count > 0
            && (loadedStored.Count < configuredLimit || DateOnly.FromDateTime(loadedStored[0].Timestamp) <= earliestNeededDate))
        {
            return loadedStored;
        }

        return await gateway.GetCandlesAsync(symbol, timeFrame, int.MaxValue, cancellationToken).ConfigureAwait(false);
    }

    public static void ThrowIfInvalid(IReadOnlyList<CandleData> bars, string symbol, TimeFrame timeFrame, string symbolArgument)
    {
        if (CandleInvariant.TryFindFirstViolation(bars, out CandleData invalidBar, out IReadOnlyList<CandleViolationCode> violations))
        {
            throw new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Stored {timeFrame} bars contain an invalid OHLCV bar: {CandleInvariant.FormatBarViolation(symbol, invalidBar, violations)}. No automatic repair is permitted.",
                symbolArgument,
                CoverageHint);
        }
    }

    public static bool TryFormatViolation(IReadOnlyList<CandleData> bars, string symbol, out string formattedViolation)
    {
        if (CandleInvariant.TryFindFirstViolation(bars, out CandleData invalidBar, out IReadOnlyList<CandleViolationCode> violations))
        {
            formattedViolation = CandleInvariant.FormatBarViolation(symbol, invalidBar, violations);
            return true;
        }

        formattedViolation = string.Empty;
        return false;
    }

    public static ToolException CreatePortfolioException(
        IReadOnlyList<string> invalidSymbolViolations,
        TimeFrame timeFrame,
        int maxListed,
        string universeArgument)
    {
        int cap = Math.Max(1, maxListed);
        int shown = Math.Min(invalidSymbolViolations.Count, cap);
        string summary = string.Join("; ", invalidSymbolViolations.Take(shown));
        if (invalidSymbolViolations.Count > shown)
        {
            summary += FormattableString.Invariant($" (+{invalidSymbolViolations.Count - shown} more symbol(s))");
        }

        return new ToolException(
            ToolErrorCodes.InvalidArgument,
            $"Stored {timeFrame} bars contain invalid OHLCV bar(s) in {invalidSymbolViolations.Count} symbol(s): {summary}. No automatic repair is permitted.",
            universeArgument,
            CoverageHint);
    }

    public static bool IsCandleInvariantFailure(ArgumentException error)
        => error.Message.Contains("CandleData.IsValid()", StringComparison.Ordinal)
           || error.Message.Contains("Open/High/Low/Close > 0", StringComparison.Ordinal);

    public static async Task<ToolException?> TryDiagnoseFullHistoryAsync(
        IReadOnlyStockAnalyzerGateway gateway,
        string symbol,
        TimeFrame timeFrame,
        string symbolArgument,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CandleData> allBars = await gateway.GetCandlesAsync(symbol, timeFrame, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (CandleInvariant.TryFindFirstViolation(allBars, out CandleData invalidBar, out IReadOnlyList<CandleViolationCode> violations))
        {
            return new ToolException(
                ToolErrorCodes.InvalidArgument,
                $"Stored {timeFrame} bars contain an invalid OHLCV bar: {CandleInvariant.FormatBarViolation(symbol, invalidBar, violations)}. No automatic repair is permitted.",
                symbolArgument,
                CoverageHint);
        }

        return null;
    }
}
