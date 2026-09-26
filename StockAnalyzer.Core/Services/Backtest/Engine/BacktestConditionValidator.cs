using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Services.Backtest.Engine;

/// <summary>Single ownership and validation boundary for condition-based backtest input.</summary>
public static class BacktestConditionValidator
{
    public static ImmutableArray<BacktestConditionEntry> SnapshotDtos(
        IReadOnlyList<BacktestConditionEntryDto> entries,
        int maxOffset)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var domainEntries = new List<BacktestConditionEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            BacktestConditionEntryDto dto = entries[i]
                ?? throw new ArgumentException($"ConditionEntries[{i}] must not be null.", nameof(entries));
            domainEntries.Add(new BacktestConditionEntry
            {
                Left = ToSide(dto.Left, i, "Left"),
                Operator = dto.Operator,
                TargetMode = dto.TargetMode,
                RightNumericValue = dto.RightNumericValue,
                Right = dto.Right is null ? null : ToSide(dto.Right, i, "Right"),
                LogicalOperator = dto.LogicalOperator,
                Role = dto.Role,
                Position = dto.Position,
            });
        }

        return Snapshot(domainEntries, maxOffset);

        static BacktestConditionSide ToSide(BacktestConditionSideDto? dto, int index, string name)
        {
            if (dto is null)
            {
                throw new ArgumentException($"ConditionEntries[{index}].{name} must not be null.", nameof(entries));
            }
            return new BacktestConditionSide
            {
                IndicatorType = dto.IndicatorType,
                Parameters = dto.Parameters?.Clone(),
                OutputName = dto.OutputName,
                Offset = dto.Offset,
                Frame = dto.Frame,
                PriceSource = dto.PriceSource,
            };
        }
    }

    public static ImmutableArray<BacktestConditionEntry> Snapshot(
        IReadOnlyList<BacktestConditionEntry> entries,
        int? maxOffset = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var builder = ImmutableArray.CreateBuilder<BacktestConditionEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            BacktestConditionEntry entry = entries[i]
                ?? throw new ArgumentException($"ConditionEntries[{i}] must not be null.", nameof(entries));
            ValidateEntry(entry, i, maxOffset);
            builder.Add(CloneEntry(entry));
        }

        return builder.MoveToImmutable();
    }

    public static IEnumerable<BacktestConditionSide> ActiveSides(IReadOnlyList<BacktestConditionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        for (int i = 0; i < entries.Count; i++)
        {
            BacktestConditionEntry entry = entries[i]
                ?? throw new ArgumentException($"ConditionEntries[{i}] must not be null.", nameof(entries));
            yield return entry.Left
                ?? throw new ArgumentException($"ConditionEntries[{i}].Left must not be null.", nameof(entries));
            if (entry.TargetMode == RightHandTargetMode.Indicator)
            {
                yield return entry.Right
                    ?? throw new ArgumentException($"ConditionEntries[{i}].Right is required in Indicator mode.", nameof(entries));
            }
        }
    }

    public static void ValidateRiskManagement(BacktestRiskManagementSettings? settings)
    {
        if (settings is null) return;
        ValidateRiskRatio(settings.StopLossPercent, nameof(settings.StopLossPercent));
        ValidateRiskRatio(settings.TakeProfitPercent, nameof(settings.TakeProfitPercent));
    }

    public static string NormalizeOutputName(BacktestConditionSide side)
    {
        ArgumentNullException.ThrowIfNull(side);
        return IndicatorOutputSeriesResolver.Normalize(side.IndicatorType, side.OutputName, side.PriceSource);
    }

    private static void ValidateEntry(BacktestConditionEntry entry, int index, int? maxOffset)
    {
        if (!IsSupportedOperator(entry.Operator))
        {
            throw new ArgumentException($"ConditionEntries[{index}].Operator '{entry.Operator}' is not supported.", nameof(entry));
        }
        if (entry.TargetMode is not (RightHandTargetMode.NumericValue or RightHandTargetMode.Indicator))
        {
            throw new ArgumentException($"ConditionEntries[{index}].TargetMode '{entry.TargetMode}' is not supported.", nameof(entry));
        }
        if (!Enum.IsDefined(typeof(LogicalOperator), entry.LogicalOperator))
        {
            throw new ArgumentException($"ConditionEntries[{index}].LogicalOperator is undefined.", nameof(entry));
        }
        if (!Enum.IsDefined(typeof(BacktestConditionRole), entry.Role))
        {
            throw new ArgumentException($"ConditionEntries[{index}].Role is undefined.", nameof(entry));
        }
        if (!Enum.IsDefined(typeof(TradeSide), entry.Position))
        {
            throw new ArgumentException($"ConditionEntries[{index}].Position is undefined.", nameof(entry));
        }

        ValidateSide(entry.Left, index, "Left", active: true, maxOffset);
        if (entry.TargetMode == RightHandTargetMode.Indicator && entry.Right is null)
        {
            throw new ArgumentException($"ConditionEntries[{index}].Right is required in Indicator mode.", nameof(entry));
        }
        if (entry.Right is not null)
        {
            ValidateSide(entry.Right, index, "Right", entry.TargetMode == RightHandTargetMode.Indicator, maxOffset);
        }
    }

    private static void ValidateSide(BacktestConditionSide? side, int index, string sideName, bool active, int? maxOffset)
    {
        if (side is null)
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName} must not be null.", nameof(side));
        }
        if (!Enum.IsDefined(typeof(IndicatorType), side.IndicatorType))
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName}.IndicatorType is undefined.", nameof(side));
        }
        if (side.Frame is { } frame && !Enum.IsDefined(typeof(TimeFrame), frame))
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName}.Frame is undefined.", nameof(side));
        }
        if (side.PriceSource is { } priceSource && !Enum.IsDefined(typeof(PriceType), priceSource))
        {
            throw new ArgumentException($"ConditionEntries[{index}].{sideName}.PriceSource is undefined.", nameof(side));
        }
        if (!active) return;

        if (!BacktestConditionOffsetRule.IsValid(side.Offset))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side.Offset, BacktestConditionOffsetRule.Describe(index, sideName, side.Offset));
        }
        if (maxOffset is { } limit && !BacktestConditionOffsetRule.IsWithinLimit(side.Offset, limit))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side.Offset, BacktestConditionOffsetRule.DescribeAboveLimit(index, sideName, side.Offset, limit));
        }

        string outputName = NormalizeOutputName(side);
        BacktestIndicatorViolationReason violation = BacktestIndicatorEligibility.Check(side.IndicatorType, outputName);
        if (violation != BacktestIndicatorViolationReason.None)
        {
            throw new ArgumentException(
                $"ConditionEntries[{index}].{sideName}: {BacktestIndicatorEligibility.Describe(side.IndicatorType, outputName, violation)}",
                nameof(side));
        }
    }

    private static BacktestConditionEntry CloneEntry(BacktestConditionEntry entry) => new()
    {
        Left = CloneSide(entry.Left),
        Operator = entry.Operator,
        TargetMode = entry.TargetMode,
        RightNumericValue = entry.RightNumericValue,
        Right = entry.Right is null ? null : CloneSide(entry.Right),
        LogicalOperator = entry.LogicalOperator,
        Role = entry.Role,
        Position = entry.Position,
    };

    private static BacktestConditionSide CloneSide(BacktestConditionSide side) => new()
    {
        IndicatorType = side.IndicatorType,
        Parameters = side.Parameters?.Clone(),
        OutputName = NormalizeOutputName(side),
        Offset = side.Offset,
        Frame = side.Frame,
        PriceSource = side.PriceSource,
    };

    private static bool IsSupportedOperator(ComparisonOperator value) => value is
        ComparisonOperator.GreaterThan or
        ComparisonOperator.GreaterThanOrEqual or
        ComparisonOperator.LessThan or
        ComparisonOperator.LessThanOrEqual or
        ComparisonOperator.Equal or
        ComparisonOperator.NotEqual;

    private static void ValidateRiskRatio(decimal? value, string name)
    {
        if (value is < 0m or >= 1m)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be null or satisfy 0 <= ratio < 1.");
        }
    }
}
