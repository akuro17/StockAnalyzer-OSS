using System.Threading;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

public interface IBacktestEngine
{
    /// <summary>
    /// <paramref name="cancellationToken"/> defaults to <see cref="CancellationToken.None"/> (P3 Hardening
    /// Task 5, safe extension): every existing caller that omits it keeps its exact prior behavior. When
    /// cancelled, an implementation MUST throw <see cref="OperationCanceledException"/> rather than
    /// return a <see cref="BacktestResult"/>.
    /// </summary>
    BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy,
        CancellationToken cancellationToken = default);
}
