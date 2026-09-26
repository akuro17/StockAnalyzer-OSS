using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Configuration;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// Y:\Temp\sa_implementation_plan_BacktestIndicatorCausalityGuard.md: the backtest refuses non-causal indicator outputs (TimeAtPrice, VolumeProfile,
/// ZigZag, Ichimoku's ChikouSpan), indicators without a synchronous calculation path, and negative Offsets - in the engine, in saved-configuration
/// validation and (see the ViewModel tests) in the selection UI - from ONE definition, <see cref="BacktestIndicatorEligibility"/>.
/// </summary>
[Trait("Category", "BacktestVerification")]
public class BacktestIndicatorCausalityGuardTests
{
    private const string ChikouSpan = CoreIchimokuIndicator.ChikouSpanSeriesName;
    private const string SenkouSpanA = CoreIchimokuIndicator.SenkouSpanASeriesName;
    private const string SenkouSpanB = CoreIchimokuIndicator.SenkouSpanBSeriesName;
    private const string TenkanSen = CoreIchimokuIndicator.TenkanSenSeriesName;
    private const string KijunSen = CoreIchimokuIndicator.KijunSenSeriesName;

    public static IEnumerable<object[]> NonCausalTypes() => BacktestIndicatorEligibility.NonCausalIndicatorTypes.Select(t => new object[] { t });
    public static IEnumerable<object[]> SynchronousUnsupportedTypes() => BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes.Select(t => new object[] { t });

    private sealed class RequestingStrategy : IBacktestStrategy
    {
        private readonly IReadOnlyList<StrategyIndicatorRequest> _requests;
        public RequestingStrategy(params StrategyIndicatorRequest[] requests) => _requests = requests;
        public string Name => "Requesting";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => _requests;
        public StrategyOrderRequest? Evaluate(StrategyContext context) => null;
    }

    private static BacktestResult RunWith(params StrategyIndicatorRequest[] requests)
    {
        BacktestInput input = VerificationHarness.MakeInput(SyntheticBars.Ramp(80, 100m, 1m));
        return VerificationHarness.Run(input, VerificationHarness.MakeConfig(), new RequestingStrategy(requests));
    }

    // ---- the single definition ----

    [Theory]
    [MemberData(nameof(NonCausalTypes))]
    public void NonCausalTypes_AreRefused_ForAnyOutputName(IndicatorType type)
    {
        Assert.Equal(BacktestIndicatorViolationReason.NonCausal, BacktestIndicatorEligibility.Check(type, null));
        Assert.Equal(BacktestIndicatorViolationReason.NonCausal, BacktestIndicatorEligibility.Check(type, "Main"));
        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(type));
    }

    [Theory]
    [MemberData(nameof(SynchronousUnsupportedTypes))]
    public void SynchronousUnsupportedTypes_AreRefusedWithTheirOwnReason(IndicatorType type)
    {
        Assert.Equal(BacktestIndicatorViolationReason.SynchronousCalculationUnsupported, BacktestIndicatorEligibility.Check(type, null));
        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(type));
    }

    [Fact]
    public void Ichimoku_OnlyTheChikouSeriesIsRefused()
    {
        Assert.Equal(BacktestIndicatorViolationReason.NonCausal, BacktestIndicatorEligibility.Check(IndicatorType.Ichimoku, ChikouSpan));
        Assert.Equal(BacktestIndicatorViolationReason.NonCausal, BacktestIndicatorEligibility.Check(IndicatorType.Ichimoku, ChikouSpan.ToLowerInvariant()));
        foreach (string allowed in new[] { "Main", TenkanSen, KijunSen, SenkouSpanA, SenkouSpanB })
        {
            Assert.True(BacktestIndicatorEligibility.IsAllowed(IndicatorType.Ichimoku, allowed), allowed);
        }
        Assert.False(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.Ichimoku));
    }

    [Theory]
    [InlineData(IndicatorType.Ichimoku, ChikouSpan, true)]
    [InlineData(IndicatorType.Ichimoku, "chikouspan", true)]
    [InlineData(IndicatorType.Ichimoku, "Main", false)]
    [InlineData(IndicatorType.Ichimoku, null, false)]
    [InlineData(IndicatorType.Ichimoku, SenkouSpanA, false)]
    [InlineData(IndicatorType.SMA, ChikouSpan, false)]
    public void IsNonCausalSeries_IsTheSeriesLevelRuleOnly(IndicatorType type, string? outputName, bool expected)
    {
        Assert.Equal(expected, BacktestIndicatorEligibility.IsNonCausalSeries(type, outputName));
    }

    [Fact]
    public void IsNonCausalSeries_DoesNotIncludeTheTypeLevelList()
    {
        // The type-level list is refused by Check(), but a whole-indicator block is not a "series" rule (consumers such as the analysis pipeline rely on this).
        Assert.False(BacktestIndicatorEligibility.IsNonCausalSeries(IndicatorType.ZigZag, "Main"));
    }

    [Fact]
    public void EligibilityLists_AreImmutableAtRuntime()
    {
        static bool IsWritable<T>(IReadOnlyCollection<T> collection) => collection is ICollection<T> { IsReadOnly: false };

        Assert.False(IsWritable(BacktestIndicatorEligibility.NonCausalIndicatorTypes));
        Assert.False(IsWritable(BacktestIndicatorEligibility.NonCausalSeries));
        Assert.False(IsWritable(BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes));
        Assert.False(BacktestIndicatorEligibility.NonCausalIndicatorTypes is IndicatorType[]);
        Assert.False(BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes is IndicatorType[]);
    }

    [Fact]
    public void EveryOtherRegisteredIndicator_IsAllowed()
    {
        var blocked = BacktestIndicatorEligibility.NonCausalIndicatorTypes
            .Concat(BacktestIndicatorEligibility.SynchronousCalculationUnsupportedTypes).ToHashSet();
        foreach (IndicatorType type in new IndicatorFactory().GetRegisteredTypes().Where(t => !blocked.Contains(t)))
        {
            Assert.True(BacktestIndicatorEligibility.IsAllowed(type), type.ToString());
        }
    }

    // ---- engine ----

    [Theory]
    [MemberData(nameof(NonCausalTypes))]
    public void Engine_RefusesNonCausalIndicator(IndicatorType type)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunWith(new StrategyIndicatorRequest("k", type, null)));
        Assert.Contains(type.ToString(), ex.Message);
        Assert.Contains("look-ahead", ex.Message);
    }

    [Theory]
    [MemberData(nameof(SynchronousUnsupportedTypes))]
    public void Engine_RefusesSynchronouslyUncomputableIndicator_InsteadOfSilentlyEvaluatingToFalse(IndicatorType type)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunWith(new StrategyIndicatorRequest("k", type, null)));
        Assert.Contains("synchronous", ex.Message);
    }

    [Fact]
    public void Engine_RefusesIchimokuChikouSeries_ButAcceptsTheCausalSeries()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunWith(new StrategyIndicatorRequest("chikou", IndicatorType.Ichimoku, null, OutputName: ChikouSpan)));

        foreach (string series in new[] { TenkanSen, KijunSen, SenkouSpanA, SenkouSpanB })
        {
            BacktestResult result = RunWith(new StrategyIndicatorRequest("k", IndicatorType.Ichimoku, null, OutputName: series));
            Assert.Equal(RunStatus.Completed, result.Status);
        }
    }

    [Fact]
    public void Engine_FailsLoudly_WhenAnAllowedIndicatorCalculationIsUnsuccessful()
    {
        // A real indicator that reports failure: EMA with Period 0 -> "Attempted to divide by zero" (IsSuccessful == false). The engine used to consume
        // that as an empty series (every condition silently false); it must now stop with an explicit error.
        var request = new StrategyIndicatorRequest("bad", IndicatorType.EMA, new CoreSmaParameter { Period = 0 });

        var ex = Assert.Throws<InvalidOperationException>(() => RunWith(request));

        Assert.Contains("calculation failed", ex.Message);
        Assert.Contains("bad", ex.Message);
    }

    // ---- Offset ----

    private static BacktestConditionEntry Entry(BacktestConditionSide left, BacktestConditionSide? right = null, RightHandTargetMode mode = RightHandTargetMode.NumericValue) => new()
    {
        Left = left,
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = mode,
        Right = right,
        RightNumericValue = 1m,
        Role = BacktestConditionRole.EntryOnly,
    };

    private static BacktestConditionSide Side(int offset, IndicatorType type = IndicatorType.Price) => new() { IndicatorType = type, Offset = offset };

    [Fact]
    public void NegativeLeftOffset_IsRejectedWhenTheStrategyIsBuilt()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ConditionBasedBacktestStrategy(new[] { Entry(Side(-1)) }));
        Assert.Contains("ConditionEntries[0].Left.Offset", ex.Message);
    }

    [Fact]
    public void NegativeRightOffset_IsRejected_OnlyWhenTheRightSideIsUsed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConditionBasedBacktestStrategy(new[] { Entry(Side(0), Side(-5), RightHandTargetMode.Indicator) }));

        // Right is ignored in NumericValue mode, so a stale negative value there cannot leak a future bar.
        _ = new ConditionBasedBacktestStrategy(new[] { Entry(Side(0), Side(-5), RightHandTargetMode.NumericValue) });
    }

    [Fact]
    public void ZeroAndPositiveOffsets_AreAccepted()
    {
        _ = new ConditionBasedBacktestStrategy(new[] { Entry(Side(0), Side(26), RightHandTargetMode.Indicator) });
    }

    // ---- end to end: "price vs the cloud currently drawn" ----

    [Fact]
    public void ClosePriceAboveSenkouSpanA_FirstSignalIsExactlyWhenTheStoredCloudExists()
    {
        // Ichimoku stores SenkouSpanA[i + 26] = value calculated at i (plot position). The first calculated value is at i = 25
        // (Kijun 26 needs 26 bars), hence the first "current cloud" value is at bar 51. On a rising ramp Close is above the cloud, so the very
        // first bar with a cloud value is the first signal - and there is no earlier one, i.e. nothing is read from a future bar.
        var entry = new BacktestConditionEntry
        {
            Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = PriceType.Close },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.Indicator,
            Right = new BacktestConditionSide
            {
                IndicatorType = IndicatorType.Ichimoku,
                Parameters = new CoreIchimokuParameter(),
                OutputName = SenkouSpanA,
            },
            Role = BacktestConditionRole.EntryOnly,
        };
        BacktestInput input = VerificationHarness.MakeInput(SyntheticBars.Ramp(120, 100m, 1m));

        BacktestResult result = VerificationHarness.Run(input, VerificationHarness.MakeConfig(), new ConditionBasedBacktestStrategy(new[] { entry }));

        const int firstCloudBar = 25 + 26;
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(firstCloudBar, Assert.Single(result.Signals).BarIndex);
    }

    private sealed class RecordingStrategy : IBacktestStrategy
    {
        private readonly StrategyIndicatorRequest _request;
        public List<decimal?> Recorded { get; } = new();
        public RecordingStrategy(StrategyIndicatorRequest request) => _request = request;
        public string Name => "Recording";
        public IReadOnlyList<StrategyIndicatorRequest> GetRequiredIndicators() => new[] { _request };

        public StrategyOrderRequest? Evaluate(StrategyContext context)
        {
            Recorded.Add(context.Indicators.ValueAt(_request.Key, context.BarIndex));
            return null;
        }
    }

    [Fact]
    public void IchimokuSenkouSpanA_OnAHigherTimeframe_IsAlignedFromTheClosedPeriodsPlotPosition()
    {
        // The Senkou series holds count + Displacement values (the tail is the projection past the last candle). The cross-timeframe alignment requires
        // one value per foreign bar, so the unknowable tail must be dropped instead of making the run throw; each daily bar then sees the weekly value
        // of the LAST CLOSED week at that week's own plot position (never the forming week, never a projected one).
        const int weeks = 60;
        const int daysPerWeek = 7;
        ImmutableArray<CandleData> weeklyBars = Enumerable.Range(0, weeks)
            .Select(w => { decimal close = 100m + w * 3m + (w % 5); return SyntheticBars.Bar(w * daysPerWeek, close, close + 2m, close - 2m, close); })
            .ToImmutableArray();
        ImmutableArray<CandleData> dailyBars = Enumerable.Range(0, weeks * daysPerWeek)
            .Select(d => { decimal close = 100m + d / 2m; return SyntheticBars.Bar(d, close, close + 1m, close - 1m, close); })
            .ToImmutableArray();
        var input = new BacktestInput(
            dailyBars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion,
            SyntheticBars.Start, SyntheticBars.Start.AddDays(dailyBars.Length + 1), 0, 0,
            new Dictionary<TimeFrame, ImmutableArray<CandleData>> { [TimeFrame.W1] = weeklyBars });
        var request = new StrategyIndicatorRequest("weeklyCloud", IndicatorType.Ichimoku, new CoreIchimokuParameter(), TimeFrame.W1, OutputName: SenkouSpanA);
        var strategy = new RecordingStrategy(request);

        BacktestResult result = VerificationHarness.Run(input, VerificationHarness.MakeConfig(), strategy);

        var weeklyIchimoku = new CoreIchimokuIndicator();
        weeklyIchimoku.Configure(new CoreIchimokuParameter());
        IReadOnlyList<decimal?> weeklyCloud = weeklyIchimoku
            .Calculate(weeklyBars.Select(b => new CoreCandleData(b.Timestamp, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray())
            .GetSeries(SenkouSpanA);
        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(dailyBars.Length, strategy.Recorded.Count);
        for (int day = 0; day < dailyBars.Length; day++)
        {
            int lastClosedWeek = day / daysPerWeek - 1;
            decimal? expected = lastClosedWeek >= 0 ? weeklyCloud[lastClosedWeek] : null;
            Assert.Equal(expected, strategy.Recorded[day]);
        }
        Assert.Contains(strategy.Recorded, v => v.HasValue);
    }

    // ---- saved configuration ----

    private static BacktestConfigurationDto Dto(BacktestConditionEntryDto? entry = null, BacktestSelectedIndicatorDto? selected = null) => new()
    {
        SchemaVersion = BacktestConfigurationDto.CurrentSchemaVersion,
        Frame = TimeFrame.D1,
        EvaluationStartUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EvaluationEndUtc = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        InitialCapital = 1_000_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 1m,
        InitialMarginRatio = 0.30m,
        MaintenanceMarginRatio = 0.20m,
        LiquidationPenaltyRatio = 0.005m,
        ConditionEntries = entry is null ? new() : new() { entry },
        SelectedIndicators = selected is null ? new() : new() { selected },
    };

    private static BacktestConditionEntryDto EntryDto(IndicatorType type, string outputName = "Main", int offset = 0) => new()
    {
        Left = new BacktestConditionSideDto { IndicatorType = type, OutputName = outputName, Offset = offset },
    };

    [Fact]
    public void SavedConfig_WithNegativeOffset_IsAnError()
    {
        BacktestConfigurationLoadResult r = BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.SMA, offset: -1)));
        Assert.Equal(BacktestConfigurationLoadStatus.Error, r.Status);
        Assert.Contains("Offset", r.ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(NonCausalTypes))]
    [MemberData(nameof(SynchronousUnsupportedTypes))]
    public void SavedConfig_WithBlockedIndicatorInACondition_IsAnError(IndicatorType type)
    {
        BacktestConfigurationLoadResult r = BacktestConfigurationValidation.Validate(Dto(EntryDto(type)));
        Assert.Equal(BacktestConfigurationLoadStatus.Error, r.Status);
        Assert.Contains(type.ToString(), r.ErrorMessage);
    }

    [Theory]
    [InlineData(RightHandTargetMode.NumericValue, BacktestConfigurationLoadStatus.Loaded)]
    [InlineData(RightHandTargetMode.Indicator, BacktestConfigurationLoadStatus.Error)]
    public void SavedConfig_RightSideCausalityAndOffset_AreCheckedOnlyWhenTheRightSideIsEvaluated(RightHandTargetMode mode, BacktestConfigurationLoadStatus expected)
    {
        // The strategy only reads Right in Indicator mode, so an unused (stale) Right must not make the configuration unloadable.
        BacktestConditionEntryDto entry = new()
        {
            Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA },
            TargetMode = mode,
            Right = new BacktestConditionSideDto { IndicatorType = IndicatorType.ZigZag, Offset = -1 },
        };

        Assert.Equal(expected, BacktestConfigurationValidation.Validate(Dto(entry)).Status);
    }

    [Fact]
    public void SavedConfig_WithBlockedSelectedIndicator_IsAnError()
    {
        var selected = new BacktestSelectedIndicatorDto { Key = "zz", Type = IndicatorType.ZigZag };
        BacktestConfigurationLoadResult r = BacktestConfigurationValidation.Validate(Dto(selected: selected));
        Assert.Equal(BacktestConfigurationLoadStatus.Error, r.Status);
        Assert.Contains("zz", r.ErrorMessage);
    }

    [Fact]
    public void SavedConfig_OffsetAboveTheConfiguredLimit_IsAnError_AtTheLimitItLoads()
    {
        int limit = BacktestConfigurationValidation.DefaultMaxConditionOffset;

        BacktestConfigurationLoadResult atLimit = BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.SMA, offset: limit)));
        BacktestConfigurationLoadResult above = BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.SMA, offset: limit + 1)));

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, atLimit.Status);
        Assert.Equal(BacktestConfigurationLoadStatus.Error, above.Status);
        Assert.Contains("MaxConditionOffset", above.ErrorMessage);
    }

    [Fact]
    public void SavedConfig_OffsetLimitFollowsTheGivenConfiguration()
    {
        // The limit is an input (Backtest:MaxConditionOffset), not a constant of the validator.
        Assert.Equal(BacktestConfigurationLoadStatus.Error,
            BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.SMA, offset: 11)), maxConditionOffset: 10).Status);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded,
            BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.SMA, offset: 10)), maxConditionOffset: 10).Status);
    }

    [Fact]
    public void SavedConfig_UnusedRightSide_IsNotCheckedAgainstTheOffsetLimit()
    {
        BacktestConditionEntryDto entry = new()
        {
            Left = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA },
            TargetMode = RightHandTargetMode.NumericValue,
            Right = new BacktestConditionSideDto { IndicatorType = IndicatorType.SMA, Offset = int.MaxValue },
        };

        Assert.Equal(BacktestConfigurationLoadStatus.Loaded, BacktestConfigurationValidation.Validate(Dto(entry)).Status);
    }

    [Fact]
    public void SavedConfig_IchimokuChikou_IsAnError_ButSenkouIsLoaded()
    {
        Assert.Equal(BacktestConfigurationLoadStatus.Error,
            BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.Ichimoku, ChikouSpan))).Status);
        Assert.Equal(BacktestConfigurationLoadStatus.Loaded,
            BacktestConfigurationValidation.Validate(Dto(EntryDto(IndicatorType.Ichimoku, SenkouSpanA, offset: 0))).Status);
    }
}
