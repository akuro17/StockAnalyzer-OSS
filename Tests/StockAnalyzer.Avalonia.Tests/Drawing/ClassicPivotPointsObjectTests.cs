using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class ClassicPivotPointsObjectTests
{
    private static List<CoreCandleData> CreateKnownDailyCandles()
    {
        return new List<CoreCandleData>
        {
            // Day 1: 2026-09-21 (Monday)
            new CoreCandleData(new DateTime(2026, 9, 21, 0, 0, 0), 95m, 110m, 90m, 100m, 1000),
            // Day 2: 2026-09-22 (Tuesday)
            new CoreCandleData(new DateTime(2026, 9, 22, 0, 0, 0), 100m, 115m, 98m, 105m, 1200),
            // Day 3: 2026-09-23 (Wednesday)
            new CoreCandleData(new DateTime(2026, 9, 23, 0, 0, 0), 105m, 120m, 102m, 118m, 1500)
        };
    }

    [Fact]
    public void Defaults_AreCorrect()
    {
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var obj = new ClassicPivotPointsObject(anchor);

        Assert.Equal(ChartObjectType.ClassicPivotPoints, obj.Type);
        Assert.Equal(PivotPeriod.Day, obj.Period);
        Assert.Equal(PivotLineExtendMode.None, obj.ExtendMode);
        Assert.True(obj.ShowP);
        Assert.True(obj.ShowR1);
        Assert.True(obj.ShowS1);
        Assert.True(obj.ShowR2);
        Assert.True(obj.ShowS2);
        Assert.True(obj.ShowR3);
        Assert.True(obj.ShowS3);
        Assert.False(obj.HasValidData);
        Assert.Null(obj.Pivot);
        Assert.Null(obj.R1);
        Assert.Null(obj.S1);
        Assert.Null(obj.R2);
        Assert.Null(obj.S2);
        Assert.Null(obj.R3);
        Assert.Null(obj.S3);
        Assert.Empty(obj.GetCalculatedValues(anchor.Time));
    }

    [Fact]
    public void Recalculate_DayPeriod_ComputesExact7Levels()
    {
        var candles = CreateKnownDailyCandles();
        // Anchor is on Day 2 (2026-09-22). Preceding period is Day 1 (2026-09-21):
        // H = 110, L = 90, C = 100
        var anchor = new ChartPoint(new DateTime(2026, 9, 22, 10, 0, 0), 105m);
        var obj = new ClassicPivotPointsObject(anchor)
        {
            Period = PivotPeriod.Day
        };

        obj.Recalculate(candles);

        Assert.True(obj.HasValidData);
        Assert.Equal(new DateTime(2026, 9, 22), obj.PeriodStart);
        Assert.Equal(new DateTime(2026, 9, 23), obj.PeriodEnd);

        // Expected:
        // P = (110 + 90 + 100) / 3 = 100m
        // R1 = 2P - L = 200 - 90 = 110m
        // S1 = 2P - H = 200 - 110 = 90m
        // diff = 110 - 90 = 20m
        // R2 = P + diff = 100 + 20 = 120m
        // S2 = P - diff = 100 - 20 = 80m
        // R3 (HBOP) = R1 + diff = 110 + 20 = 130m
        // S3 (LBOP) = S1 - diff = 90 - 20 = 70m
        Assert.Equal(100m, obj.Pivot);
        Assert.Equal(110m, obj.R1);
        Assert.Equal(90m, obj.S1);
        Assert.Equal(120m, obj.R2);
        Assert.Equal(80m, obj.S2);
        Assert.Equal(130m, obj.R3);
        Assert.Equal(70m, obj.S3);

        var calcValues = obj.GetCalculatedValues(anchor.Time);
        Assert.NotEmpty(calcValues);
        Assert.Contains(calcValues, v => v.Key == "Pivot" && v.NumericValue == 100m);
        Assert.Contains(calcValues, v => v.Key == "HBOP" && v.NumericValue == 130m);
        Assert.Contains(calcValues, v => v.Key == "LBOP" && v.NumericValue == 70m);
    }

    [Fact]
    public void Recalculate_IntradayCandles_AggregatesDayCorrectly()
    {
        var candles = new List<CoreCandleData>
        {
            // 2026-09-21 bars
            new CoreCandleData(new DateTime(2026, 9, 21, 9, 30, 0), 95m, 105m, 92m, 100m, 500),
            new CoreCandleData(new DateTime(2026, 9, 21, 12, 0, 0), 100m, 110m, 98m, 105m, 600),
            new CoreCandleData(new DateTime(2026, 9, 21, 15, 30, 0), 105m, 108m, 90m, 96m, 700),
            // 2026-09-22 bar (current)
            new CoreCandleData(new DateTime(2026, 9, 22, 9, 30, 0), 96m, 102m, 95m, 101m, 400),
        };

        var anchor = new ChartPoint(new DateTime(2026, 9, 22, 9, 30, 0), 100m);
        var obj = new ClassicPivotPointsObject(anchor);
        obj.Recalculate(candles);

        Assert.True(obj.HasValidData);
        // H = 110, L = 90, C = 96
        // P = (110 + 90 + 96) / 3 = 296 / 3m
        decimal expectedP = 296m / 3m;
        decimal expectedDiff = 110m - 90m; // 20m
        decimal expectedR1 = 2m * expectedP - 90m;
        decimal expectedS1 = 2m * expectedP - 110m;
        decimal expectedR2 = expectedP + expectedDiff;
        decimal expectedS2 = expectedP - expectedDiff;
        decimal expectedR3 = expectedR1 + expectedDiff;
        decimal expectedS3 = expectedS1 - expectedDiff;

        Assert.Equal(expectedP, obj.Pivot);
        Assert.Equal(expectedR1, obj.R1);
        Assert.Equal(expectedS1, obj.S1);
        Assert.Equal(expectedR2, obj.R2);
        Assert.Equal(expectedS2, obj.S2);
        Assert.Equal(expectedR3, obj.R3);
        Assert.Equal(expectedS3, obj.S3);
    }

    [Fact]
    public void Recalculate_OverWeekend_FindsPrecedingFriday()
    {
        var candles = new List<CoreCandleData>
        {
            // Friday: 2026-09-18
            new CoreCandleData(new DateTime(2026, 9, 18, 0, 0, 0), 190m, 200m, 180m, 195m, 2000),
            // Monday: 2026-09-21
            new CoreCandleData(new DateTime(2026, 9, 21, 0, 0, 0), 196m, 205m, 194m, 202m, 2100)
        };

        var anchor = new ChartPoint(new DateTime(2026, 9, 21, 10, 0, 0), 200m);
        var obj = new ClassicPivotPointsObject(anchor) { Period = PivotPeriod.Day };
        obj.Recalculate(candles);

        Assert.True(obj.HasValidData);
        // Friday data: H=200, L=180, C=195
        decimal expectedP = (200m + 180m + 195m) / 3m; // 575 / 3 = 191.66666666666666666666666667
        Assert.Equal(expectedP, obj.Pivot);
        Assert.Equal(2m * expectedP - 180m, obj.R1);
        Assert.Equal(2m * expectedP - 200m, obj.S1);
    }

    [Fact]
    public void Recalculate_WeekPeriod_AggregatesPrecedingWeek()
    {
        var candles = new List<CoreCandleData>
        {
            // Week 1 (Monday 2026-09-14 to Friday 2026-09-18)
            new CoreCandleData(new DateTime(2026, 9, 14), 100m, 105m, 98m, 102m, 1000),
            new CoreCandleData(new DateTime(2026, 9, 16), 102m, 120m, 101m, 115m, 1500),
            new CoreCandleData(new DateTime(2026, 9, 18), 115m, 118m, 95m, 110m, 1200),
            // Week 2 (Wednesday 2026-09-23)
            new CoreCandleData(new DateTime(2026, 9, 23), 112m, 116m, 108m, 114m, 1100),
        };

        var anchor = new ChartPoint(new DateTime(2026, 9, 23), 112m);
        var obj = new ClassicPivotPointsObject(anchor) { Period = PivotPeriod.Week };
        obj.Recalculate(candles);

        Assert.True(obj.HasValidData);
        // Week 2 start is Monday 2026-09-21, end is Monday 2026-09-28
        Assert.Equal(new DateTime(2026, 9, 21), obj.PeriodStart);
        Assert.Equal(new DateTime(2026, 9, 28), obj.PeriodEnd);

        // Preceding week aggregate:
        // H = max(105, 120, 118) = 120
        // L = min(98, 101, 95) = 95
        // C = last close = 110
        decimal expectedP = (120m + 95m + 110m) / 3m; // 325 / 3
        Assert.Equal(expectedP, obj.Pivot);
    }

    [Fact]
    public void Recalculate_MonthPeriod_AggregatesPrecedingMonth()
    {
        var candles = new List<CoreCandleData>
        {
            // August 2026
            new CoreCandleData(new DateTime(2026, 8, 3), 100m, 110m, 90m, 105m, 1000),
            new CoreCandleData(new DateTime(2026, 8, 28), 105m, 130m, 100m, 125m, 1500),
            // September 2026
            new CoreCandleData(new DateTime(2026, 9, 15), 125m, 128m, 120m, 122m, 1200),
        };

        var anchor = new ChartPoint(new DateTime(2026, 9, 15), 125m);
        var obj = new ClassicPivotPointsObject(anchor) { Period = PivotPeriod.Month };
        obj.Recalculate(candles);

        Assert.True(obj.HasValidData);
        Assert.Equal(new DateTime(2026, 9, 1), obj.PeriodStart);
        Assert.Equal(new DateTime(2026, 10, 1), obj.PeriodEnd);

        // August: H=130, L=90, C=125
        decimal expectedP = (130m + 90m + 125m) / 3m; // 345 / 3 = 115m
        Assert.Equal(115m, expectedP);
        Assert.Equal(expectedP, obj.Pivot);
    }

    [Fact]
    public void Recalculate_NoPriorData_HasValidDataFalse()
    {
        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(new DateTime(2026, 9, 21), 100m, 110m, 90m, 105m, 1000)
        };

        // Anchor is on the first candle itself: no preceding day exists
        var anchor = new ChartPoint(new DateTime(2026, 9, 21), 100m);
        var obj = new ClassicPivotPointsObject(anchor);
        obj.Recalculate(candles);

        Assert.False(obj.HasValidData);
        Assert.Null(obj.Pivot);
        Assert.Empty(obj.GetCalculatedValues(anchor.Time));
    }

    [Fact]
    public void Recalculate_EmptyCandles_DoesNotThrow()
    {
        var anchor = new ChartPoint(new DateTime(2026, 9, 21), 100m);
        var obj = new ClassicPivotPointsObject(anchor);
        obj.Recalculate(Array.Empty<CoreCandleData>());

        Assert.False(obj.HasValidData);
    }

    [Fact]
    public void Translate_UpdatesAnchorCoordinate()
    {
        var anchor = new ChartPoint(new DateTime(2026, 9, 21, 10, 0, 0), 100m);
        var obj = new ClassicPivotPointsObject(anchor);

        obj.Translate(TimeSpan.FromDays(2), 15m);

        Assert.Equal(new DateTime(2026, 9, 23, 10, 0, 0), obj.Points[0].Time);
        Assert.Equal(115m, obj.Points[0].Price);
    }

    [Fact]
    public void DeferredComputationRecalculator_DispatchesSuccessfully()
    {
        var candles = CreateKnownDailyCandles();
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var obj = new ClassicPivotPointsObject(anchor);

        bool result = DeferredComputationRecalculator.TryRecalculate(obj, candles);

        Assert.True(result);
        Assert.True(obj.HasValidData);
        Assert.Equal(100m, obj.Pivot);
    }

    [Fact]
    public void Behavior_CreatesAndRecalculatesInstance()
    {
        var candles = CreateKnownDailyCandles();
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var behavior = new ClassicPivotPointsBehavior();

        var created = behavior.CreateObject(anchor, candles);

        var obj = Assert.IsType<ClassicPivotPointsObject>(created);
        Assert.True(obj.HasValidData);
        Assert.Equal(100m, obj.Pivot);
        Assert.Equal(1, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void ExtendMode_ExpandsHitTestBounds()
    {
        var candles = CreateKnownDailyCandles();
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 105m);
        var obj = new ClassicPivotPointsObject(anchor);
        obj.Recalculate(candles);

        var transform = new LinearCoordinateTransform(
            new DateTime(2026, 9, 20), new DateTime(2026, 9, 25),
            50m, 150m,
            1000, 500);

        // Day 2 (2026-09-22) is roughly from x = 400 to x = 600.
        // Screen point far to the right (x = 900) at Pivot price (100m)
        double pivotY = transform.GetYFromPrice(100m);
        var rightPoint = new global::Avalonia.Point(900, pivotY);

        // 1. None: point far to the right must not hit
        obj.ExtendMode = PivotLineExtendMode.None;
        Assert.False(obj.HitTest(rightPoint, transform));

        // 2. ExtendRight: point far to the right must hit
        obj.ExtendMode = PivotLineExtendMode.ExtendRight;
        Assert.True(obj.HitTest(rightPoint, transform));

        // 3. ExtendLeft: point far to the left must hit
        var leftPoint = new global::Avalonia.Point(50, pivotY);
        obj.ExtendMode = PivotLineExtendMode.ExtendLeft;
        Assert.True(obj.HitTest(leftPoint, transform));
        Assert.False(obj.HitTest(rightPoint, transform));

        // 4. ExtendBoth: both points must hit
        obj.ExtendMode = PivotLineExtendMode.ExtendBoth;
        Assert.True(obj.HitTest(leftPoint, transform));
        Assert.True(obj.HitTest(rightPoint, transform));
    }

    [Fact]
    public void Recalculate_DefensiveGuard_RejectsCorruptCandles()
    {
        var corruptCandles = new List<CoreCandleData>
        {
            // High < Low
            new CoreCandleData(new DateTime(2026, 9, 21), 100m, 90m, 110m, 95m, 1000)
        };

        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var obj = new ClassicPivotPointsObject(anchor);
        obj.Recalculate(corruptCandles);

        Assert.False(obj.HasValidData);
        Assert.Null(obj.Pivot);
    }

    [Fact]
    public void Recalculate_DefensiveGuard_RejectsNonPositiveLow()
    {
        var zeroCandles = new List<CoreCandleData>
        {
            // Low <= 0
            new CoreCandleData(new DateTime(2026, 9, 21), 10m, 20m, 0m, 15m, 1000)
        };

        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var obj = new ClassicPivotPointsObject(anchor);
        obj.Recalculate(zeroCandles);

        Assert.False(obj.HasValidData);
        Assert.Null(obj.Pivot);
    }

    [Fact]
    public void DrawingParameterViewBuilder_DoesNotHideExtendMode_UnderFallbackHiddenTags()
    {
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var obj = new ClassicPivotPointsObject(anchor);

        var builder = new DrawingParameterViewBuilder();
        var fallbackHiddenTags = new[] { DrawingParameterTags.Common, DrawingParameterTags.Geometry };
        var control = builder.Build(obj, fallbackHiddenTags);

        Assert.NotNull(control);
        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(ClassicPivotPointsObject));
        var extendMeta = metadata.FirstOrDefault(m => m.Prop.Name == nameof(ClassicPivotPointsObject.ExtendMode));
        Assert.NotNull(extendMeta);
        Assert.DoesNotContain(DrawingParameterTags.Geometry, extendMeta.Tags);
        Assert.Contains(DrawingParameterTags.Analysis, extendMeta.Tags);
    }

    [Fact]
    public void Recalculate_Failure_ResetsPreviousCachedLevelsToNull()
    {
        var candles = CreateKnownDailyCandles();
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        using var obj = new ClassicPivotPointsObject(anchor);

        obj.Recalculate(candles);
        Assert.True(obj.HasValidData);
        Assert.NotNull(obj.Pivot);
        Assert.NotNull(obj.R1);

        // Recalculate with empty data (failure scenario)
        obj.Recalculate(Array.Empty<CoreCandleData>());
        Assert.False(obj.HasValidData);
        Assert.Null(obj.Pivot);
        Assert.Null(obj.R1);
        Assert.Null(obj.R2);
        Assert.Null(obj.R3);
        Assert.Null(obj.S1);
        Assert.Null(obj.S2);
        Assert.Null(obj.S3);
    }

    [Fact]
    public void Dispose_CanBeCalledWithoutException()
    {
        var anchor = new ChartPoint(new DateTime(2026, 9, 22), 100m);
        var obj = new ClassicPivotPointsObject(anchor);
        var ex = Record.Exception(() => obj.Dispose());
        Assert.Null(ex);
    }
}
