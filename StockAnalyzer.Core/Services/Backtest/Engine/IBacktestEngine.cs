using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

public interface IBacktestEngine
{
    BacktestResult Run(BacktestInput input, BacktestConfiguration configuration, IBacktestStrategy strategy);
}
