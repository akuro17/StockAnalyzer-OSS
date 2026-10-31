using StockAnalyzer.Core.Services.Backtest.Evaluation;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Public view of the reason codes a caller reports when one of the pure diagnostic functions (<see cref="EquityCurveSampler"/>,
/// <see cref="DrawdownEpisodeEnumerator"/>, <see cref="PeriodicReturnsCalculator"/>) fails on arithmetic. The values are the single definition in
/// <c>DrawdownReasonCodes</c> (persisted vocabulary, must not change); they are re-exported here only because that class is internal.
/// </summary>
public static class BacktestDiagnosticReasons
{
    /// <summary>A decimal operation overflowed (for example a ratio against a tiny positive running high).</summary>
    public const string ArithmeticOverflow = DrawdownReasonCodes.ArithmeticOverflow;

    /// <summary>An arithmetic result was not finite.</summary>
    public const string NonFiniteResult = DrawdownReasonCodes.NonFiniteResult;
}
