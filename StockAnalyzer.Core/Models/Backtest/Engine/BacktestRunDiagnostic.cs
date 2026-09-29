namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>What went wrong in a run that did not complete (owner decision G3, A1 (Run diagnostics)).</summary>
public enum BacktestDiagnosticCode { ArithmeticOverflow }

/// <summary>Where the failure happened: before the bar loop (indicator batch preparation) or inside one bar of it.</summary>
public enum BacktestRunPhase { Preparation, Bar }

/// <summary>The engine step whose decimal arithmetic overflowed, when it is known.</summary>
public enum BacktestArithmeticOperation
{
    Unknown,
    PositionSizing,
    OrderFillPricing,
    EntryAccounting,
    ExitAccounting,
    LiquidationPricing,
    MarkToMarket,
}

/// <summary>
/// Structured description of an engine-owned arithmetic failure. <paramref name="BarIndex"/> is null for <see cref="BacktestRunPhase.Preparation"/> (an
/// indicator batch calculation has no bar) and is the exact bar whose uncommitted work was rolled back for <see cref="BacktestRunPhase.Bar"/>.
/// <paramref name="Message"/> is the original exception's message, for display only.
/// </summary>
public sealed record BacktestRunDiagnostic(
    BacktestDiagnosticCode Code,
    BacktestRunPhase Phase,
    int? BarIndex,
    BacktestArithmeticOperation Operation,
    string Message);
