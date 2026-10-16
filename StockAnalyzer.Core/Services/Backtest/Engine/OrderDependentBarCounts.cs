using System.Collections.Generic;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// The counts of order-dependent bars (API_SPECIFICATION "Order-dependent bars"): <see cref="Detected"/> is every recorded bar
/// (<see cref="WarningCode.AmbiguousIntrabarOrder"/> plus <see cref="WarningCode.AmbiguousIntrabarOrderAffectsOutcome"/>), <see cref="Affecting"/> is the
/// subset whose result the configured convention decided (<see cref="WarningCode.AmbiguousIntrabarOrderAffectsOutcome"/>). <see cref="RiskTies"/> and
/// <see cref="ExitVersusLiquidation"/> split the detected bars by kind. The single place that interprets those codes and message subjects, so the Results tab,
/// the engine that writes the messages and any measurement agree.
/// </summary>
public readonly record struct OrderDependentBarCounts(int Detected, int Affecting, int RiskTies = 0, int ExitVersusLiquidation = 0)
{
    /// <summary>The subject of an AB-1 message (Stop-Loss and Take-Profit both reached); the engine builds its text from this constant.</summary>
    internal const string RiskTieSubject = "Stop-Loss and Take-Profit were both reached";

    /// <summary>The subject of an AB-2 message (pending exit order versus margin liquidation); the engine builds its text from this constant.</summary>
    internal const string ExitVersusLiquidationSubject = "the pending exit order and the margin liquidation are order-dependent";

    /// <summary>True for the codes that describe an order-dependent bar.</summary>
    public static bool IsOrderDependent(WarningCode code)
        => code is WarningCode.AmbiguousIntrabarOrder or WarningCode.AmbiguousIntrabarOrderAffectsOutcome;

    /// <summary>Counts the warnings of bars at or after <paramref name="fromBarIndex"/> (the first evaluated bar).</summary>
    public static OrderDependentBarCounts Count(IEnumerable<BacktestWarning> warnings, int fromBarIndex = 0)
    {
        int detected = 0;
        int affecting = 0;
        int riskTies = 0;
        int exitVersusLiquidation = 0;
        foreach (BacktestWarning warning in warnings)
        {
            if (warning.BarIndex < fromBarIndex || !IsOrderDependent(warning.Code)) continue;
            detected++;
            if (warning.Code == WarningCode.AmbiguousIntrabarOrderAffectsOutcome) affecting++;
            if (warning.Message.Contains(RiskTieSubject, System.StringComparison.Ordinal)) riskTies++;
            else if (warning.Message.Contains(ExitVersusLiquidationSubject, System.StringComparison.Ordinal)) exitVersusLiquidation++;
        }

        return new OrderDependentBarCounts(detected, affecting, riskTies, exitVersusLiquidation);
    }
}
