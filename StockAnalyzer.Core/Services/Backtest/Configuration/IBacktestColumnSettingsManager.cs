using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

/// <summary>
/// Service interface for loading and saving Backtest column customization settings.
/// </summary>
public interface IBacktestColumnSettingsManager
{
    Task<BacktestColumnSettings?> LoadAsync();
    BacktestColumnSettings? Load();
    Task SaveAsync(BacktestColumnSettings settings);
    Task SaveColumnsAsync(IEnumerable<string>? tradeColumns, IEnumerable<string>? metricNames);
}
