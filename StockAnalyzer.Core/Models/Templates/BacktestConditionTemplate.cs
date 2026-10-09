using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Models.Templates;

/// <summary>
/// Domain template encapsulating a complete backtest signal condition tree and optional risk management rules.
/// Persisted under Data/Templates/BacktestCondition/{Guid}.json.
/// </summary>
public class BacktestConditionTemplate : TemplateBase
{
    public override TemplateType TemplateType => TemplateType.BacktestCondition;

    /// <summary>
    /// Serialized condition tree covering the 6 canonical roots: Entry/Exit/Reverse x Long/Short.
    /// </summary>
    public BacktestConditionTreeDto ConditionTree { get; set; } = new();

    /// <summary>
    /// Optional position-wide risk management parameters (StopLoss / TakeProfit percentage).
    /// </summary>
    public BacktestRiskManagementSettingsDto? RiskManagement { get; set; }
}
