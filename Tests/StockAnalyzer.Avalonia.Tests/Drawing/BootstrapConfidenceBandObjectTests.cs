using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using StockAnalyzer.Core.Tests.Common;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The bootstrap confidence band tool (DSL expansion run D-6): the object around <see cref="BootstrapConfidenceBandValues"/>.</summary>
public sealed class BootstrapConfidenceBandObjectTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1);

    /// <summary>The label text of a rendered band is converted to UTF-8 by Skia, which allocates a little; the drawing itself allocates nothing.</summary>
    private const long TextOnlyAllocationBudget = 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ChartObjectJsonConverter(), new AvaloniaColorJsonConverter(), new JsonStringEnumConverter() },
    };

    private static List<CoreCandleData> MakeCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i * 0.2m) + Math.Round((decimal)(Math.Sin(i * 0.25) * 4.0), 4);
            list.Add(new CoreCandleData(BaseTime.AddDays(i), close, close + 0.5m, close - 0.5m, close, 1000L + i));
        }

        return list;
    }

    private static LinearCoordinateTransform MakeTransform(int width = 900, int height = 600)
        => new(BaseTime, BaseTime.AddDays(170), 70m, 150m, width, height);

    private static ChartPoint At(List<CoreCandleData> candles, int index) => new(candles[index].Timestamp, candles[index].Close);

    private static BootstrapConfidenceBandObject Create(List<CoreCandleData> candles, int first = 20, int last = 100)
        => new(At(candles, first), At(candles, last));

    private static void RenderOnce(IChartObject obj, int width = 900, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.ClipRect(new SKRect(0, 0, width, height));
        obj.Render(canvas, MakeTransform(width, height));
    }

    [Fact]
    public void Registration_ToolBehaviourCategoryAndEnums()
    {
        Assert.Equal(2, DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.BootstrapConfidenceBand)!.RequiredSteps);
        var algorithms = DrawingToolCategoryService.GetCategories().Single(c => c.NameKey == "DrawCat_Algorithms");
        Assert.Contains(algorithms.Tools, t => t.Tool == DrawingTool.BootstrapConfidenceBand && t.NameKey == "DrawTool_BootstrapConfidenceBand");
        Assert.Equal(DrawingTool.RegularPolygon + 1, DrawingTool.BootstrapConfidenceBand);
        Assert.Equal(ChartObjectType.RegularPolygon + 1, ChartObjectType.BootstrapConfidenceBand);
        Assert.Equal(ChartObjectType.BootstrapConfidenceBand, new BootstrapConfidenceBandObject().Type);
        Assert.IsAssignableFrom<IRangeAnalysisDrawing>(new BootstrapConfidenceBandObject());   // the one wiring clause of the family covers it
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleKeys_ExistForTheToolItsParametersAndItsEnums(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        JsonElement locale = JsonDocument.Parse(stream).RootElement;
        var keys = new List<string> { "DrawTool_BootstrapConfidenceBand", "Param_StatKind", "Param_ConfidenceLevel", "Param_Resamples", "Param_BlockLength", "Param_BlockMode" };
        keys.AddRange(Enum.GetValues<BootstrapStatKind>().Select(m => $"Enum_{nameof(BootstrapStatKind)}_{m}"));
        keys.AddRange(Enum.GetValues<BootstrapBlockMode>().Select(m => $"Enum_{nameof(BootstrapBlockMode)}_{m}"));
        foreach (string key in keys)
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Fact]
    public void Behavior_CreatesTwoAnchorsAndDoesNotCalculateWhilePlacing()
    {
        var candles = MakeCandles(150);
        var behavior = new BootstrapConfidenceBandBehavior();

        using var obj = (BootstrapConfidenceBandObject)behavior.CreateObject(At(candles, 20), candles);
        behavior.UpdatePoint(obj, 1, At(candles, 100), candles);

        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(candles[100].Timestamp, obj.Points[1].Time);
        Assert.Equal(BootstrapBandStatus.NoRange, obj.Band.Status);   // nothing computed until the placement has finished
        Assert.Empty(obj.GetCalculatedValues(candles[100].Timestamp));
    }

    [Fact]
    public void Recalculate_ComputesTheBand_AndTheValuesFollow()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.ConfidenceLevel = 0.9m;
        obj.Resamples = 300;
        obj.BlockLength = 4;

        obj.Recalculate(candles);

        BootstrapBand expected = BootstrapConfidenceBandValues.Calculate(candles, 20, 100, BootstrapStatKind.Mean, 0.9m, 300, 4, BootstrapBlockMode.CircularBlock);
        Assert.True(obj.Band.IsOk);
        Assert.Equal((expected.Estimate, expected.Lower, expected.Upper), (obj.Band.Estimate, obj.Band.Lower, obj.Band.Upper));

        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[100].Timestamp);
        Assert.Equal(BootstrapConfidenceBandValues.Calculate(expected, IndicatorColor.Gray).Select(v => v.Key), values.Select(v => v.Key));
        Assert.Equal(81, values.Single(v => v.Key == "RangeBars").NumericValue);
        Assert.Equal(81, values.Single(v => v.Key == "EffectiveBars").NumericValue);
        Assert.Same(values, obj.GetCalculatedValues(candles[100].Timestamp));   // cached while nothing changes
    }

    [Fact]
    public void SettingChange_IsPickedUpByTheNextRecalculation_AndTheParametersAreClamped()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.Recalculate(candles);
        Assert.True(obj.Band.IsPriceLevel);

        obj.StatKind = BootstrapStatKind.Sharpe;
        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));
        Assert.False(obj.Band.IsPriceLevel);
        Assert.Equal(80, obj.Band.SampleCount);   // 81 bars give 80 returns

        obj.ConfidenceLevel = 5m;
        Assert.Equal(BootstrapConfidenceBandValues.MaxConfidenceLevel, obj.ConfidenceLevel);
        obj.Resamples = 1;
        Assert.Equal(BootstrapConfidenceBandValues.MinResamples, obj.Resamples);
        obj.BlockLength = 999;
        Assert.Equal(BootstrapConfidenceBandValues.MaxBlockLength, obj.BlockLength);
        obj.StatKind = (BootstrapStatKind)99;
        Assert.Equal(BootstrapStatKind.Mean, obj.StatKind);
        var fresh = new BootstrapConfidenceBandObject();
        Assert.Equal((BootstrapStatKind.Mean, 0.95m, 200, 5, BootstrapBlockMode.CircularBlock), (fresh.StatKind, fresh.ConfidenceLevel, fresh.Resamples, fresh.BlockLength, fresh.BlockMode));
    }

    [Fact]
    public void Dispatcher_AfterMoveFrame_KeepsTheResult_AfterMoveCompleted_Recalculates()
    {
        var candles = MakeCandles(160);
        using var obj = Create(candles);
        Assert.True(DeferredComputationRecalculator.TryRecalculate(obj, candles));
        BootstrapBand before = obj.Band;
        Assert.True(before.IsOk);

        obj.Translate(TimeSpan.FromDays(10), 0m);
        DrawingRecalculation.AfterMoveFrame(obj, candles);
        Assert.Same(before, obj.Band);
        Assert.Empty(obj.GetCalculatedValues(candles[110].Timestamp));

        DrawingRecalculation.AfterMoveCompleted(obj, new DrawingCalculationContext(candles));
        Assert.NotSame(before, obj.Band);
        Assert.Equal(81, obj.Band.RangeBars);
        Assert.Equal(candles[110].Timestamp, obj.Band.WindowEnd);
        Assert.NotEmpty(obj.GetCalculatedValues(candles[110].Timestamp));
    }

    [Fact]
    public void ShortRange_ShowsTheStateInsteadOfAnInterval()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles, 40, 46);

        obj.Recalculate(candles);

        Assert.Equal(BootstrapBandStatus.TooFewSamples, obj.Band.Status);
        IReadOnlyList<DrawingCalculatedValue> values = obj.GetCalculatedValues(candles[46].Timestamp);
        Assert.Equal("State", values[0].Key);
        Assert.Equal("Insufficient samples (< 10)", values[0].FormattedText);
        RenderOnce(obj);   // the range edges and the state text

        using var wide = Create(candles);
        wide.Resamples = 100;
        wide.ConfidenceLevel = 0.99m;
        wide.Recalculate(candles);
        Assert.Equal(BootstrapBandStatus.TailResolution, wide.Band.Status);
        Assert.Equal("Increase Resamples or lower Confidence", wide.GetCalculatedValues(candles[100].Timestamp)[0].FormattedText);
        RenderOnce(wide);
    }

    [Fact]
    public void RangeWithoutBars_HasNoValues_AndRenders()
    {
        var candles = MakeCandles(150);
        using var obj = new BootstrapConfidenceBandObject(new ChartPoint(BaseTime.AddDays(-50), 100m), new ChartPoint(BaseTime.AddDays(-30), 100m));

        obj.Recalculate(candles);

        Assert.Equal(BootstrapBandStatus.NoRange, obj.Band.Status);
        Assert.Empty(obj.GetCalculatedValues(BaseTime));
        RenderOnce(obj);
    }

    [Fact]
    public void PriceStatistic_DrawsTheBox_ReturnStatisticDrawsNoBox()
    {
        var candles = MakeCandles(150);
        LinearCoordinateTransform transform = MakeTransform();

        using var price = Create(candles);
        price.Recalculate(candles);
        using var ret = Create(candles);
        ret.StatKind = BootstrapStatKind.StdDev;
        ret.Recalculate(candles);

        BootstrapBand band = price.Band;
        Point inside = transform.ChartToScreen(new ChartPoint(candles[60].Timestamp, (band.Lower + band.Upper) / 2m));
        Point above = transform.ChartToScreen(new ChartPoint(candles[60].Timestamp, band.Upper + 20m));
        Assert.True(price.HitTest(inside, transform, 3.0));
        Assert.False(price.HitTest(above, transform, 3.0));
        Assert.False(ret.HitTest(inside, transform, 3.0));   // a return statistic has no price level: only the range edges and the label

        Assert.True(CountNonBlack(price) > CountNonBlack(ret));
    }

    private static int CountNonBlack(IChartObject obj)
    {
        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        obj.Render(canvas, MakeTransform());
        return bitmap.Pixels.Count(p => p != SKColors.Black);
    }

    [Fact]
    public void HitTest_AllocatesNothing_RenderOnlyTheLabelText_AndDisposeIsSafe()
    {
        var candles = MakeCandles(150);
        var obj = Create(candles);
        obj.Recalculate(candles);
        LinearCoordinateTransform transform = MakeTransform();
        using var surface = SKSurface.Create(new SKImageInfo(900, 600));
        SKCanvas canvas = surface.Canvas;

        obj.Render(canvas, transform);
        long renderBytes = AllocationProbe.InCollectionFreeWindow(() => obj.Render(canvas, transform), () => canvas.Clear(SKColors.Black));
        long hitBytes = AllocationProbe.InCollectionFreeWindow(() => obj.HitTest(new Point(500, 300), transform));
        Assert.True(renderBytes <= TextOnlyAllocationBudget, $"render allocated {renderBytes} bytes");
        Assert.Equal(0L, hitBytes);

        obj.Dispose();
        obj.Dispose();
        obj.Render(canvas, transform);
        Assert.False(obj.HitTest(new Point(500, 300), transform));
        Assert.Empty(obj.GetCalculatedValues(candles[100].Timestamp));
    }

    [Fact]
    public void BothPersistencePaths_KeepTheParameters_AndNoComputedState()
    {
        var candles = MakeCandles(150);
        using var obj = Create(candles);
        obj.StatKind = BootstrapStatKind.Sharpe;
        obj.ConfidenceLevel = 0.9m;
        obj.Resamples = 400;
        obj.BlockLength = 7;
        obj.BlockMode = BootstrapBlockMode.Stationary;
        obj.Recalculate(candles);

        string json = JsonSerializer.Serialize<IChartObject>(obj, JsonOptions);
        using var restored = Assert.IsType<BootstrapConfidenceBandObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));
        Assert.Equal(BootstrapStatKind.Sharpe, restored.StatKind);
        Assert.Equal(0.9m, restored.ConfidenceLevel);
        Assert.Equal(400, restored.Resamples);
        Assert.Equal(7, restored.BlockLength);
        Assert.Equal(BootstrapBlockMode.Stationary, restored.BlockMode);
        using JsonDocument document = JsonDocument.Parse(json);
        string[] names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("band", StringComparison.OrdinalIgnoreCase) || n.Contains("overlay", StringComparison.OrdinalIgnoreCase));

        var manager = new ChartObjectManager();
        manager.AddObject(obj);
        var state = DrawingDocumentCodec.Capture(manager, new DrawingDocumentKey("BOOT", TimeframeType.Daily), revision: 1L);
        using var materialized = DrawingDocumentCodec.Materialize(DrawingDocumentCodec.DeserializeFromJson(DrawingDocumentCodec.SerializeToJson(state)));
        var codecRestored = Assert.IsType<BootstrapConfidenceBandObject>(materialized.Objects[ChartDrawingContextType.Standard].Single());
        Assert.Equal(BootstrapStatKind.Sharpe, codecRestored.StatKind);
        Assert.Equal(0.9m, codecRestored.ConfidenceLevel);
        Assert.Equal(BootstrapBlockMode.Stationary, codecRestored.BlockMode);
        Assert.Equal(BootstrapBandStatus.NoRange, codecRestored.Band.Status);   // computed again on load
    }
}
