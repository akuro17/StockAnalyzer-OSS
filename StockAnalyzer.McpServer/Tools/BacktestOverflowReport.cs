using System.Globalization;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.McpServer.Tools;

/// <summary>
/// The single definition of the text a backtest tool returns when the engine's decimal arithmetic overflows (owner decision D-8 = C): the error code, the wording
/// of the failing place and the hint. Every figure is written with the invariant culture. The tools differ only in WHAT they can say about the failing place
/// (a portfolio sleeve has a diagnostic with bar, date and operation; a sweep names the combination; a single run names the account settings).
/// </summary>
internal static class BacktestOverflowReport
{
    /// <summary>Scientific notation with one decimal and no padded exponent: decimal.MaxValue is written "7.9e28", never as its 29 digits.</summary>
    private const string LimitFormat = "0.0e0";

    /// <summary>What a portfolio message says when the failing symbol cannot be named (no index reached the tool); never the name of a symbol that was not proved to be the failing one.</summary>
    internal const string UnknownSymbolText = "one of the symbols";

    /// <summary>The same for a sweep (optimize, walk-forward): the failing combination cannot be named.</summary>
    internal const string UnknownRunText = "one of the runs";

    /// <summary>
    /// The limit comes from the platform constant and is written with the invariant culture, so the text is the same on every machine. Built on each use (an error
    /// path, never a hot one) so that a test can prove the culture independence; a static field would be fixed by whichever thread touched it first.
    /// </summary>
    private static string Hint => string.Create(
        CultureInfo.InvariantCulture,
        $"Lower initialCapital and sizingParameter, or check the stored prices of the symbol: the decimal arithmetic of the engine cannot hold a value above about {decimal.MaxValue.ToString(LimitFormat, CultureInfo.InvariantCulture)}.");

    /// <summary>"ArithmeticOverflow in PositionSizing at bar 123 (2024-05-17)", or "... before the first bar (indicator preparation)" for a failure without a bar.</summary>
    public static string Describe(BacktestRunDiagnostic diagnostic)
    {
        if (diagnostic.BarIndex is not { } bar)
        {
            return $"{diagnostic.Code} before the first bar (indicator preparation)";
        }

        string date = diagnostic.BarTimestamp is { } stamp ? $" ({ToolJson.FormatDate(stamp)})" : string.Empty;
        return FormattableString.Invariant($"{diagnostic.Code} in {diagnostic.Operation} at bar {bar}{date}");
    }

    /// <summary>The account figures that decide the size of every amount of a run.</summary>
    public static string Account(BacktestConfiguration configuration)
        => FormattableString.Invariant($"initialCapital {configuration.InitialCapital}, sizingModel {configuration.SizingModel}, sizingParameter {configuration.SizingParameter}");

    /// <summary>One run. A null <paramref name="symbol"/> (or configuration) means the failing run could not be identified: the text then says so instead of naming a guess.</summary>
    public static ToolException ForSingleRun(string? symbol, BacktestConfiguration? configuration)
    {
        string account = configuration is null ? string.Empty : $" (account: {Account(configuration)})";
        return new(ToolErrorCodes.BacktestArithmeticOverflow, $"The backtest of {symbol ?? UnknownSymbolText} overflowed decimal arithmetic{account}.", hint: Hint);
    }

    /// <summary>A sweep run: <paramref name="where"/> names the combination (and, for walk-forward, the window and part).</summary>
    public static ToolException ForSweepItem(string symbol, string where, BacktestConfiguration? configuration)
    {
        string account = configuration is null ? string.Empty : $" (account: {Account(configuration)})";
        return new(ToolErrorCodes.BacktestArithmeticOverflow, $"The backtest of {symbol} overflowed decimal arithmetic for {where}{account}.", hint: Hint);
    }

    /// <summary>Every sleeve that was run overflowed (or no run finished): the portfolio has no result worth returning.</summary>
    public static ToolException ForPortfolio(int symbolCount, int overflowCount, int noDataCount, string firstSymbol, string firstReason)
    {
        string noData = noDataCount > 0 ? $" {noDataCount} had no data." : string.Empty;
        return new(
            ToolErrorCodes.BacktestArithmeticOverflow,
            FormattableString.Invariant($"{overflowCount} of {symbolCount} symbols overflowed decimal arithmetic and none finished (first, in request order: {firstSymbol}, {firstReason}).{noData}"),
            hint: Hint);
    }
}
