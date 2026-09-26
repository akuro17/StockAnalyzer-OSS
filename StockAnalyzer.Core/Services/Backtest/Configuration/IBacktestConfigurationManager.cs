using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

public interface IBacktestConfigurationManager
{
    Task<BacktestConfigurationLoadResult> LoadAsync();

    Task SaveAsync(BacktestConfigurationDto configuration);
}
