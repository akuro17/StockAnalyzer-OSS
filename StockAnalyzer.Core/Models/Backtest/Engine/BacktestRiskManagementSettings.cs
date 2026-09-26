namespace StockAnalyzer.Core.Models.Backtest.Engine;

/// <summary>
/// Entry-price-relative Stop-Loss/Take-Profit settings, consumed by
/// <see cref="Services.Backtest.Engine.ConditionBasedBacktestStrategy"/> (Task 8b of
/// Y:\Temp\sa_implementation_plan_BacktestComparisonSignals.md section 4.5). Both percents are `null` by
/// default (disabled) — no magic non-null default, matching this plan's existing "opt-in, no invented
/// default" pattern. Expressed as a decimal ratio (0.05m = 5%), consistent with this project's
/// decimal-for-price-and-price-derived-ratios rule.
/// </summary>
public sealed class BacktestRiskManagementSettings
{
    public decimal? StopLossPercent { get; init; }

    public decimal? TakeProfitPercent { get; init; }

    /// <summary>True when at least one threshold is configured — lets a caller skip constructing/passing
    /// this type at all when both are left blank, without duplicating that null-check at every call site.</summary>
    public bool IsEnabled => StopLossPercent.HasValue || TakeProfitPercent.HasValue;
}
