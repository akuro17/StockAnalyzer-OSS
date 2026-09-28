using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Backtest.Configuration;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>Test entry point for the saved-configuration validator using the default configured limits (<see cref="BacktestSettings"/>), the same values production reads from appsettings.json.</summary>
internal static class BacktestConfigurationValidation
{
    public static int DefaultMaxConditionOffset { get; } = new BacktestSettings().MaxConditionOffset;
    public static int DefaultMaxConditionTreeDepth { get; } = new BacktestSettings().MaxConditionTreeDepth;
    public static int DefaultMaxConditionTreeNodes { get; } = new BacktestSettings().MaxConditionTreeNodes;

    public static BacktestConfigurationLoadResult Validate(BacktestConfigurationDto? dto, int? maxConditionOffset = null, int? maxConditionTreeDepth = null, int? maxConditionTreeNodes = null)
        => BacktestConfigurationManager.ValidateDeserializedConfiguration(
            dto,
            maxConditionOffset ?? DefaultMaxConditionOffset,
            maxConditionTreeDepth ?? DefaultMaxConditionTreeDepth,
            maxConditionTreeNodes ?? DefaultMaxConditionTreeNodes);
}
