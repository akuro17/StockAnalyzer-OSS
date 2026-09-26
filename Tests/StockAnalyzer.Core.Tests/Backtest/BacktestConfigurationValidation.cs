using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Configuration;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Test entry point for the saved-configuration validator using the default configured Offset limit (<see cref="BacktestSettings"/>), the same value production reads from appsettings.json.</summary>
internal static class BacktestConfigurationValidation
{
    public static int DefaultMaxConditionOffset { get; } = new BacktestSettings().MaxConditionOffset;

    public static BacktestConfigurationLoadResult Validate(BacktestConfigurationDto? dto, int? maxConditionOffset = null)
        => BacktestConfigurationManager.ValidateDeserializedConfiguration(dto, maxConditionOffset ?? DefaultMaxConditionOffset);
}
