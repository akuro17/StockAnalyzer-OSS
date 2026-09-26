using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Backtest.Configuration;

namespace StockAnalyzer.Core.Services.Backtest.Configuration;

/// <summary>Creates a fully owned copy of a persisted backtest configuration.</summary>
public static class BacktestConfigurationSnapshot
{
    public static BacktestConfigurationDto Create(BacktestConfigurationDto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new BacktestConfigurationDto
        {
            SchemaVersion = source.SchemaVersion,
            ExecutionModel = source.ExecutionModel,
            Symbol = source.Symbol,
            Frame = source.Frame,
            EvaluationStartUtc = source.EvaluationStartUtc,
            EvaluationEndUtc = source.EvaluationEndUtc,
            InitialCapital = source.InitialCapital,
            CommissionFlat = source.CommissionFlat,
            CommissionPerUnit = source.CommissionPerUnit,
            SlippageRatio = source.SlippageRatio,
            SizingModel = source.SizingModel,
            SizingParameter = source.SizingParameter,
            InitialMarginRatio = source.InitialMarginRatio,
            MaintenanceMarginRatio = source.MaintenanceMarginRatio,
            LiquidationPenaltyRatio = source.LiquidationPenaltyRatio,
            BootstrapSeed = source.BootstrapSeed,
            BootstrapIterations = source.BootstrapIterations,
            SelectedIndicators = source.SelectedIndicators is null
                ? null!
                : source.SelectedIndicators.Select(CloneSelectedIndicator).ToList(),
            ConditionEntries = source.ConditionEntries is null
                ? null!
                : source.ConditionEntries.Select(CloneConditionEntry).ToList(),
            RiskManagement = source.RiskManagement is null ? null : new BacktestRiskManagementSettingsDto
            {
                StopLossPercent = source.RiskManagement.StopLossPercent,
                TakeProfitPercent = source.RiskManagement.TakeProfitPercent,
            },
        };
    }

    private static BacktestSelectedIndicatorDto CloneSelectedIndicator(BacktestSelectedIndicatorDto source) =>
        source is null
            ? null!
            : new BacktestSelectedIndicatorDto
            {
                Key = source.Key,
                Type = source.Type,
                Frame = source.Frame,
                Parameters = source.Parameters?.Clone(),
            };

    private static BacktestConditionEntryDto CloneConditionEntry(BacktestConditionEntryDto source) =>
        source is null
            ? null!
            : new BacktestConditionEntryDto
            {
                Left = CloneSide(source.Left),
                Operator = source.Operator,
                TargetMode = source.TargetMode,
                RightNumericValue = source.RightNumericValue,
                Right = source.Right is null ? null : CloneSide(source.Right),
                LogicalOperator = source.LogicalOperator,
                Role = source.Role,
                Position = source.Position,
            };

    private static BacktestConditionSideDto CloneSide(BacktestConditionSideDto source) =>
        source is null
            ? null!
            : new BacktestConditionSideDto
            {
                IndicatorType = source.IndicatorType,
                Parameters = source.Parameters?.Clone(),
                OutputName = source.OutputName,
                Offset = source.Offset,
                Frame = source.Frame,
                PriceSource = source.PriceSource,
            };
}
