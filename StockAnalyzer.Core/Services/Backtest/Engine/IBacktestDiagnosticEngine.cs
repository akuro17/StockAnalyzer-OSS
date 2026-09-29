using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>
/// Additive entry point next to <see cref="IBacktestEngine"/> (owner decision G3, A1 (Run diagnostics)). The existing
/// <see cref="IBacktestEngine.Run"/> is unchanged and still throws its original exceptions.
/// </summary>
public interface IBacktestDiagnosticEngine
{
    /// <summary>
    /// Same simulation as <see cref="IBacktestEngine.Run"/>, but an <see cref="System.OverflowException"/> raised by the ENGINE's own arithmetic becomes a
    /// <see cref="RunStatus.Failed"/> result with a <see cref="BacktestRunDiagnostic"/> and the committed prefix of bars. Argument/validation errors, strategy
    /// exceptions (including an OverflowException thrown by a strategy), cancellation and every other exception are still thrown.
    /// </summary>
    BacktestDiagnosedRun RunWithDiagnostics(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        CancellationToken cancellationToken = default);
}
