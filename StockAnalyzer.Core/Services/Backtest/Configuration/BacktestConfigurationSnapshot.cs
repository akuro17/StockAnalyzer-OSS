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
            ConditionTree = source.ConditionTree is null ? null : CloneTree(source.ConditionTree),
            RiskManagement = source.RiskManagement is null ? null : new BacktestRiskManagementSettingsDto
            {
                StopLossPercent = source.RiskManagement.StopLossPercent,
                TakeProfitPercent = source.RiskManagement.TakeProfitPercent,
            },
        };
    }

    private static BacktestConditionTreeDto CloneTree(BacktestConditionTreeDto source) => new()
    {
        EntryLong = CloneNode(source.EntryLong),
        EntryShort = CloneNode(source.EntryShort),
        ExitLong = CloneNode(source.ExitLong),
        ExitShort = CloneNode(source.ExitShort),
        ReverseLong = CloneNode(source.ReverseLong),
        ReverseShort = CloneNode(source.ReverseShort),
    };

    /// <summary>Null-preserving like the other clones (validation, not the copy, reports a malformed value); a null Children list stays null.</summary>
    private static BacktestConditionNodeDto CloneNode(BacktestConditionNodeDto source)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        return source is null
            ? null!
            : new BacktestConditionNodeDto
            {
                Kind = source.Kind,
                Operator = source.Operator,
                Name = source.Name,
                Children = source.Children is null ? null! : source.Children.Select(CloneNode).ToList(),
                Comparison = source.Comparison is null ? null : CloneConditionEntry(source.Comparison),
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
