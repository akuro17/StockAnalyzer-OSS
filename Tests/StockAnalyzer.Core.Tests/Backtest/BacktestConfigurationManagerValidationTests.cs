using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Direct coverage for <see cref="BacktestConfigurationManager.ValidateDeserializedConfiguration"/>
/// (spec §5.5's null/SchemaVersion/enum-boundary checks), exercised without touching the real
/// <c>backtest_configuration.json</c> path under the user's actual Data/Config folder - unlike
/// <c>BacktestConfigurationRoundTripTests</c>, which only verifies the ViewModel's reaction to an
/// already-constructed <see cref="BacktestConfigurationLoadResult"/> via a fake manager and never
/// exercises this validation logic itself.
/// </summary>
public class BacktestConfigurationManagerValidationTests
{
    private static BacktestConfigurationDto ValidDto(System.Action<DtoBuilder>? customize = null)
    {
        var builder = new DtoBuilder();
        customize?.Invoke(builder);
        return builder.Build();
    }

    private sealed class DtoBuilder
    {
        public int SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion;
        public ExecutionModel ExecutionModel = ExecutionModel.Legacy;
        public TimeFrame Frame = TimeFrame.D1;
        public PositionSizingModel SizingModel = PositionSizingModel.FixedQuantity;
        public decimal InitialCapital = 1_000_000m;
        public decimal SizingParameter = 1m;
        public decimal InitialMarginRatio = 0.30m;
        public decimal MaintenanceMarginRatio = 0.20m;
        public DateTime EvaluationStartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime EvaluationEndUtc = new(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        public int BootstrapIterations = 2000;
        public List<BacktestSelectedIndicatorDto> SelectedIndicators = new();
        public List<BacktestConditionEntryDto> ConditionEntries = new();

        public BacktestConfigurationDto Build() => new()
        {
            SchemaVersion = SchemaVersion,
            ExecutionModel = ExecutionModel,
            Frame = Frame,
            SizingModel = SizingModel,
            InitialCapital = InitialCapital,
            SizingParameter = SizingParameter,
            InitialMarginRatio = InitialMarginRatio,
            MaintenanceMarginRatio = MaintenanceMarginRatio,
            EvaluationStartUtc = EvaluationStartUtc,
            EvaluationEndUtc = EvaluationEndUtc,
            BootstrapIterations = BootstrapIterations,
            SelectedIndicators = SelectedIndicators,
            ConditionEntries = ConditionEntries,
        };
    }

    [Fact]
    public void NullDto_ReturnsError()
    {
        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(null);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Null(result.Configuration);
    }

    [Fact]
    public void ValidDto_ReturnsLoaded()
    {
        BacktestConfigurationDto dto = ValidDto();

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
        Assert.Same(dto, result.Configuration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SchemaVersionMismatch_ReturnsError(int schemaVersion)
    {
        BacktestConfigurationDto dto = ValidDto(b => b.SchemaVersion = schemaVersion);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("SchemaVersion", result.ErrorMessage);
    }

    [Fact]
    public void UndefinedFrame_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.Frame = (TimeFrame)9999);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Frame", result.ErrorMessage);
    }

    [Fact]
    public void UndefinedSizingModel_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.SizingModel = (PositionSizingModel)9999);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("SizingModel", result.ErrorMessage);
    }

    [Fact]
    public void UndefinedExecutionModel_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ExecutionModel = (ExecutionModel)9999);

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ExecutionModel", result.ErrorMessage);
    }

    [Fact]
    public void SelectedIndicator_UndefinedIndicatorType_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.SelectedIndicators = new List<BacktestSelectedIndicatorDto>
        {
            new() { Key = "ind1", Type = (IndicatorType)999999 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("IndicatorType", result.ErrorMessage);
        Assert.Contains("ind1", result.ErrorMessage);
    }

    [Fact]
    public void SelectedIndicator_UndefinedFrameOverride_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.SelectedIndicators = new List<BacktestSelectedIndicatorDto>
        {
            new() { Key = "ind1", Type = IndicatorType.SMA, Frame = (TimeFrame)9999 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ind1", result.ErrorMessage);
    }

    [Fact]
    public void SelectedIndicator_ValidFrameOverride_ReturnsLoaded()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.SelectedIndicators = new List<BacktestSelectedIndicatorDto>
        {
            new() { Key = "ind1", Type = IndicatorType.SMA, Frame = TimeFrame.H1 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void SelectedIndicator_NullFrameOverride_SkipsFrameCheck()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.SelectedIndicators = new List<BacktestSelectedIndicatorDto>
        {
            new() { Key = "ind1", Type = IndicatorType.SMA, Frame = null },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    // Task 4: ConditionEntries validation (mirrors the SelectedIndicators coverage above).

    [Fact]
    public void ConditionEntry_ValidLeftOnly_ReturnsLoaded()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA } },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void ConditionEntry_UndefinedLeftIndicatorType_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = (IndicatorType)999999 } },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Left", result.ErrorMessage);
        Assert.Contains("IndicatorType", result.ErrorMessage);
    }

    [Fact]
    public void ConditionEntry_UndefinedRightIndicatorType_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new()
            {
                Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA },
                TargetMode = RightHandTargetMode.Indicator,
                Right = new BacktestConditionSideDto { IndicatorType = (IndicatorType)999999 },
            },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Right", result.ErrorMessage);
    }

    [Fact]
    public void ConditionEntry_NullRight_SkipsRightCheck()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, Right = null },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, result.Status);
    }

    [Fact]
    public void ConditionEntry_UndefinedSideFrame_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA, Frame = (TimeFrame)9999 } },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Frame", result.ErrorMessage);
    }

    [Fact]
    public void ConditionEntry_UndefinedOperator_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, Operator = (ComparisonOperator)9999 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Operator", result.ErrorMessage);
    }

    [Fact]
    public void ConditionEntry_UndefinedTargetMode_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, TargetMode = (RightHandTargetMode)9999 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("TargetMode", result.ErrorMessage);
    }

    [Fact]
    public void ConditionEntry_UndefinedLogicalOperator_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, LogicalOperator = (LogicalOperator)9999 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("LogicalOperator", result.ErrorMessage);
    }

    [Fact]
    public void ConditionEntry_UndefinedRole_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, Role = (BacktestConditionRole)9999 },
        });

        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);

        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Role", result.ErrorMessage);
    }

    [Fact]
    public void ExplicitNullConditionCollection_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = null!);
        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ConditionEntries", result.ErrorMessage);
    }

    [Fact]
    public void NullConditionElement_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto> { null! });
        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("ConditionEntries[0]", result.ErrorMessage);
    }

    [Fact]
    public void NullLeft_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = null! },
        });
        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Left", result.ErrorMessage);
    }

    [Theory]
    [InlineData(ComparisonOperator.Contains)]
    [InlineData(ComparisonOperator.DoesNotContain)]
    public void DefinedButUnsupportedOperator_ReturnsError(ComparisonOperator value)
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new() { Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA }, Operator = value },
        });
        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("not supported", result.ErrorMessage);
    }

    [Fact]
    public void IndicatorModeWithoutRight_ReturnsError()
    {
        BacktestConfigurationDto dto = ValidDto(b => b.ConditionEntries = new List<BacktestConditionEntryDto>
        {
            new()
            {
                Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA },
                TargetMode = RightHandTargetMode.Indicator,
                Right = null,
            },
        });
        BacktestConfigurationLoadResult result = BacktestConfigurationValidation.Validate(dto);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, result.Status);
        Assert.Contains("Right", result.ErrorMessage);
    }

    [Fact]
    public void Snapshot_OwnsNestedParameterObjects()
    {
        var selectedParameters = new CoreSmaParameter { Period = 5 };
        var conditionParameters = new CoreSmaParameter { Period = 7 };
        BacktestConfigurationDto source = ValidDto(builder =>
        {
            builder.SelectedIndicators.Add(new BacktestSelectedIndicatorDto
            {
                Key = "sma",
                Type = IndicatorType.SMA,
                Parameters = selectedParameters,
            });
            builder.ConditionEntries.Add(new BacktestConditionEntryDto
            {
                Left = new BacktestConditionSideDto
                {
                    IndicatorType = IndicatorType.SMA,
                    Parameters = conditionParameters,
                },
            });
        });

        BacktestConfigurationDto snapshot = BacktestConfigurationSnapshot.Create(source);
        selectedParameters.Period = 50;
        conditionParameters.Period = 70;

        Assert.Equal(5, Assert.IsType<CoreSmaParameter>(Assert.Single(snapshot.SelectedIndicators).Parameters).Period);
        Assert.Equal(7, Assert.IsType<CoreSmaParameter>(Assert.Single(snapshot.ConditionEntries).Left.Parameters).Period);
    }
}
