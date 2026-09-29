namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Outcome of a diagnosed run (owner decision G3). A completed run has <c>Diagnostic == null</c> and a <see cref="Result"/> identical to what the plain
/// <c>Run</c> returns. An engine arithmetic failure has <c>Result.Status == RunStatus.Failed</c> and a non-null <see cref="Diagnostic"/>; the result holds the
/// fully committed prefix of bars before the failing one and is never a report-ready result.
/// </summary>
public sealed record BacktestDiagnosedRun(BacktestResult Result, BacktestRunDiagnostic? Diagnostic);
