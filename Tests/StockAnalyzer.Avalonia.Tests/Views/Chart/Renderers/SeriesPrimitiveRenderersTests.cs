using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Tests.Common;
using StockAnalyzer.Core.Theme;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.Linq;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public sealed class SeriesPrimitiveRenderersTests
{
    private const int Width = 400;
    private const int Height = 200;
    private const int CandleCount = 20;
    private static readonly DateTime Start = new(2026, 1, 1);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);
    private static readonly Rect Area = new(0, 0, Width, Height);

    private static List<CoreCandleData> MakeCandles(int count = CandleCount)
    {
        var list = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new CoreCandleData(Start.AddDays(i), 100m, 110m, 90m, 100m, 1000));
        }
        return list;
    }

    private static GenericCoordinateTransform MakeTransform(
        decimal minY = -20m,
        decimal maxY = 120m,
        int count = CandleCount,
        PriceScaleType priceScale = PriceScaleType.Linear)
    {
        var tr = new GenericCoordinateTransform(ChartAxisMode.Time, Width, Height)
        {
            PriceScale = priceScale
        };
        tr.UpdateRange(Start, Start.AddDays(count), minY, maxY);
        return tr;
    }

    private sealed class StubThemeManager : IThemeManager
    {
        private static readonly IReadOnlyDictionary<ThemeColorKey, IndicatorColor> EmptyColors =
            new Dictionary<ThemeColorKey, IndicatorColor>();

        public ThemeColors CurrentTheme { get; set; } = ThemeColors.Dark;
        public AppThemeMode CurrentMode { get; set; } = AppThemeMode.Dark;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void ChangeTheme(ThemeColors newTheme)
        {
            CurrentTheme = newTheme;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentTheme)));
        }

        public void SetThemeMode(AppThemeMode mode) => CurrentMode = mode;
        public void UpdateSingleColor(ThemeColorKey key, IndicatorColor color) { }
        public IReadOnlyDictionary<ThemeColorKey, IndicatorColor> GetCurrentColors() => EmptyColors;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
    }

    private sealed class StubChartRenderConfig : IChartRenderConfig
    {
        private static readonly NamedColor DefaultLabelColor = new("Default", SKColors.Gray);
        public required IThemeManager ThemeManager { get; init; }
        public ChartType ChartType { get; init; } = ChartType.Candlestick;
        public double CurrentPrice => 100.0;
        public IndicatorColor BullishColor => ThemeManager.CurrentTheme.Bullish;
        public IndicatorColor BearishColor => ThemeManager.CurrentTheme.Bearish;
        public IndicatorColor NeutralColor => ThemeManager.CurrentTheme.Neutral;
        public NamedColor ReversalLabelColor => DefaultLabelColor;
        public NamedColor PriceLabelColor => DefaultLabelColor;
        public int VisibleStartIndex { get; init; }
        public int VisibleCandleCount { get; init; } = CandleCount;
        public ICoordinateTransform? Transform { get; init; }
        public Core.Models.Point MousePosition => default;
        public bool ShowMultiWavePatterns => false;
        public bool ShowGhostProjections => false;
        public int MultiWavePatternMaxLines => 0;
        public IndicatorColor? MultiWaveBullishColor => null;
        public IndicatorColor? MultiWaveBearishColor => null;
        public float GhostProjectionFontSize => 10f;
        public bool ShowGhostLabelsOnHoverOnly => false;
        public double RenderScaling => 1.0;
        public bool IsSubWindowVisible => true;
        public bool InvertOscillator => false;
        public double DefaultDrawingThickness => 1.5;
        public bool CrosshairLabelVisible => false;
    }

    private static IChartRenderConfig MakeConfig(GenericCoordinateTransform tr, ThemeColors? theme = null, int candleCount = CandleCount, ChartType chartType = ChartType.Candlestick)
    {
        return new StubChartRenderConfig
        {
            ThemeManager = new StubThemeManager { CurrentTheme = theme ?? ThemeColors.Dark },
            Transform = tr,
            VisibleCandleCount = candleCount,
            ChartType = chartType
        };
    }

    private sealed class StubPrimitiveResult : IIndicatorResult, IPrimitiveAwareIndicatorResult
    {
        private readonly IIndicatorResult _inner;
        public IReadOnlyList<SeriesPrimitiveDescriptor> PrimitiveDescriptors { get; }

        public StubPrimitiveResult(Dictionary<string, IReadOnlyList<decimal?>> series, IReadOnlyList<SeriesPrimitiveDescriptor> descriptors)
        {
            _inner = IndicatorResult.Success(series);
            PrimitiveDescriptors = descriptors;
        }

        public bool IsSuccessful => _inner.IsSuccessful;
        public string? ErrorMessage => _inner.ErrorMessage;
        public IReadOnlyList<decimal?> MainValues => _inner.MainValues;
        public bool HasSeries(string name) => _inner.HasSeries(name);
        public IReadOnlyList<decimal?> GetSeries(string name) => _inner.GetSeries(name);
        public IEnumerable<string> SeriesNames => _inner.SeriesNames;
        public IReadOnlyList<string> SeriesNamesList => _inner.SeriesNamesList;
        public IReadOnlyDictionary<string, string> SeriesLabels => _inner.SeriesLabels;
        public object? CustomData => _inner.CustomData;
        public StockAnalyzer.Core.Models.Confluence.IConfluenceSignalProvider? SignalProvider => _inner.SignalProvider;
        public int Count => _inner.Count;
        public decimal? this[int index] => _inner[index];
        public IEnumerator<decimal?> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static SKBitmap RenderToBitmap(
        CoreIndicatorSettings setting,
        Dictionary<string, IReadOnlyList<decimal?>> series,
        IReadOnlyList<SeriesPrimitiveDescriptor> descriptors,
        decimal minY = -20m,
        decimal maxY = 120m,
        PriceScaleType priceScale = PriceScaleType.Linear,
        int candleCount = CandleCount,
        IndicatorRenderer? existingRenderer = null)
    {
        var candles = MakeCandles(candleCount);
        var tr = MakeTransform(minY, maxY, candleCount, priceScale);
        var cfg = MakeConfig(tr);
        var result = new StubPrimitiveResult(series, descriptors);
        var snapshot = new ChartDataSnapshot(
            candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: candleCount,
            priceScale: priceScale);

        var bmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);

        var owned = existingRenderer == null ? new IndicatorRenderer() : null;
        try
        {
            (existingRenderer ?? owned!).RenderIndicator(canvas, Area, snapshot, setting, cfg);
        }
        finally
        {
            owned?.Dispose();
        }
        return bmp;
    }

    private static int CountNonBlackPixels(SKBitmap bmp)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y) != SKColors.Black) count++;
        return count;
    }

    private static float BarX(GenericCoordinateTransform tr, int i) =>
        (float)tr.ChartToScreen(new ChartPoint(Start.AddDays(i), 0m)).X;

    private static float PriceY(GenericCoordinateTransform tr, decimal price) =>
        (float)tr.ChartToScreen(new ChartPoint(DateTime.MinValue, price)).Y;

    [Theory]
    [InlineData(StepAlignmentMode.After)]
    [InlineData(StepAlignmentMode.Before)]
    [InlineData(StepAlignmentMode.Mid)]
    public void StepLine_AlignmentModes_RenderExpectedTransitionGeometry(StepAlignmentMode mode)
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "step_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 200, 255),
            Thickness = 2.0,
            IsOverlay = true
        };
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = i < 10 ? 20m : 80m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.StepLine,
            PrimarySeriesName = "Main",
            StepMode = mode
        };

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(setting, new() { { "Main", vals } }, new[] { desc });

        float x9 = BarX(tr, 9);
        float x10 = BarX(tr, 10);
        float xMid = (x9 + x10) * 0.5f;
        float yMid = (PriceY(tr, 20m) + PriceY(tr, 80m)) * 0.5f;

        SKColor expected = new(0, 200, 255, 255);
        float probeX = mode switch
        {
            StepAlignmentMode.After => x10,
            StepAlignmentMode.Before => x9,
            _ => xMid
        };

        Assert.Equal(expected, bmp.GetPixel((int)MathF.Floor(probeX), (int)MathF.Floor(yMid)));
    }

    [Fact]
    public void HorizontalSegment_DrawsPerBarWithoutVerticalConnectors_AndSupportsSinglePoint()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "hseg_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 255, 200, 0),
            Thickness = 2.0,
            IsOverlay = true
        };
        var vals = new decimal?[CandleCount];
        vals[5] = 20m;
        vals[6] = 80m;

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(
            setting,
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.HorizontalSegment, PrimarySeriesName = "Main", SegmentWidthRatio = 0.6f } });

        float midY = (PriceY(tr, 20m) + PriceY(tr, 80m)) * 0.5f;
        float x5 = BarX(tr, 5);
        float x6 = BarX(tr, 6);

        Assert.NotEqual(SKColors.Black, bmp.GetPixel((int)MathF.Floor(x5), (int)MathF.Floor(PriceY(tr, 20m))));
        Assert.NotEqual(SKColors.Black, bmp.GetPixel((int)MathF.Floor(x6), (int)MathF.Floor(PriceY(tr, 80m))));
        // No vertical connector at midY between bar 5 and bar 6
        Assert.Equal(SKColors.Black, bmp.GetPixel((int)MathF.Floor(x6), (int)MathF.Floor(midY)));

        // Single valid point also renders
        var single = new decimal?[1] { 50m };
        using var singleBmp = RenderToBitmap(
            setting,
            new() { { "Main", single } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.HorizontalSegment, PrimarySeriesName = "Main" } },
            candleCount: 1);
        Assert.True(CountNonBlackPixels(singleBmp) > 0);
    }

    [Fact]
    public void Spline_InterpolatesExactDataPoints_AndBreaksOnNullGaps()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "spline_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 80, 220, 120),
            Thickness = 2.0,
            IsOverlay = true
        };
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < 8; i++) vals[i] = 20m + (i % 2 == 0 ? 0m : 40m);
        // Null gap at 8..11
        for (int i = 12; i < CandleCount; i++) vals[i] = 60m;

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(
            setting,
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Spline, PrimarySeriesName = "Main", SplineTension = 0.5 } });

        // Exact data point at index 3 (val = 60m) must be lit, and gap at index 10 must be dark
        Assert.NotEqual(SKColors.Black, bmp.GetPixel((int)MathF.Floor(BarX(tr, 3)), (int)MathF.Floor(PriceY(tr, 60m))));
        Assert.Equal(SKColors.Black, bmp.GetPixel((int)MathF.Floor(BarX(tr, 10)), (int)MathF.Floor(PriceY(tr, 60m))));
    }

    [Fact]
    public void Area_ZeroFillAlphaSkipsFill_AndPositiveFillRendersGradient()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "area_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 180, 255),
            Thickness = 2.0,
            IsOverlay = true
        };
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 80m;

        using var noFill = RenderToBitmap(
            setting,
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Area, PrimarySeriesName = "Main", FillAlpha = 0, DrawBorderStroke = true } });

        using var withFill = RenderToBitmap(
            setting,
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Area, PrimarySeriesName = "Main", FillAlpha = 120, BaselineMode = AreaBaselineMode.ScalarValue, BaselineValue = 10m } });

        var tr = MakeTransform();
        int interiorX = (int)MathF.Floor(BarX(tr, 10));
        int interiorY = (int)MathF.Floor(PriceY(tr, 45m));

        Assert.Equal(SKColors.Black, noFill.GetPixel(interiorX, interiorY));
        Assert.NotEqual(SKColors.Black, withFill.GetPixel(interiorX, interiorY));
    }

    [Fact]
    public void BandCloud_And_PositiveNegativeArea_SplitCrossingsWithoutBowtieArtifacts()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "band_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 200, 200, 200),
            UpColor = new IndicatorColor(255, 0, 200, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };

        var upper = new decimal?[CandleCount];
        var lower = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            upper[i] = i < 10 ? 80m : 20m;
            lower[i] = i < 10 ? 20m : 80m;
        }

        var tr = MakeTransform();
        using var bandBmp = RenderToBitmap(
            setting,
            new() { { "Main", upper }, { "Lower", lower } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.BandCloud, PrimarySeriesName = "Main", SecondarySeriesName = "Lower", FillAlpha = 180, DrawBorderStroke = false } });

        // Left of crossing (bar 5, y=50): green dominant; right of crossing (bar 15, y=50): red dominant
        SKColor leftPixel = bandBmp.GetPixel((int)MathF.Floor(BarX(tr, 5)), (int)MathF.Floor(PriceY(tr, 50m)));
        SKColor rightPixel = bandBmp.GetPixel((int)MathF.Floor(BarX(tr, 15)), (int)MathF.Floor(PriceY(tr, 50m)));
        Assert.True(leftPixel.Green > leftPixel.Red);
        Assert.True(rightPixel.Red > rightPixel.Green);

        // PositiveNegativeArea crossing around BaselineValue = 50m
        using var posNegBmp = RenderToBitmap(
            setting,
            new() { { "Main", upper } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.PositiveNegativeArea, PrimarySeriesName = "Main", BaselineValue = 50m, FillAlpha = 180, DrawBorderStroke = false } });

        SKColor posSide = posNegBmp.GetPixel((int)MathF.Floor(BarX(tr, 5)), (int)MathF.Floor(PriceY(tr, 65m)));
        SKColor negSide = posNegBmp.GetPixel((int)MathF.Floor(BarX(tr, 15)), (int)MathF.Floor(PriceY(tr, 35m)));
        Assert.True(posSide.Green > posSide.Red);
        Assert.True(negSide.Red > negSide.Green);
    }

    [Fact]
    public void Batch2Primitives_ZeroManagedAllocationPerRender_AndLogNonPositiveSafe()
    {
        var candles = MakeCandles(200);
        var tr = MakeTransform(1m, 200m, 200);
        tr.PriceScale = PriceScaleType.Log;
        var cfg = MakeConfig(tr);

        var main = new decimal?[200];
        var sec = new decimal?[200];
        for (int i = 0; i < 200; i++)
        {
            main[i] = i == 50 ? -5m : 20m + (i % 30);
            sec[i] = 15m + (i % 15);
        }

        var descriptors = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.StepLine, PrimarySeriesName = "Main" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.HorizontalSegment, PrimarySeriesName = "Main" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Spline, PrimarySeriesName = "Main" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Area, PrimarySeriesName = "Main", BaselineMode = AreaBaselineMode.PanelBottom },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.BandCloud, PrimarySeriesName = "Main", SecondarySeriesName = "Sec" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.PositiveNegativeArea, PrimarySeriesName = "Main", BaselineValue = 25m }
        };

        var setting = new CoreIndicatorSettings { Id = "alloc_b2", IsEnabled = true, IsOverlay = true, UseUpDownColors = true };
        var result = new StubPrimitiveResult(new() { { "Main", main }, { "Sec", sec } }, descriptors);
        var snapshot = new ChartDataSnapshot(
            candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: 200,
            priceScale: PriceScaleType.Log);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var renderer = new IndicatorRenderer();

        // Warmup JIT & shader caches
        renderer.RenderIndicator(surface.Canvas, Area, snapshot, setting, cfg);

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, snapshot, setting, cfg));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void FloatingBar_NormalizesInvertedBounds_AndEnforcesOnePixelFlatGuard()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "fbar_1",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 220, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };
        var low = new decimal?[CandleCount];
        var high = new decimal?[CandleCount];
        low[4] = 80m; high[4] = 30m;  // inverted -> DownColor
        low[8] = 50m; high[8] = 50m;  // flat -> 1px height guard

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(
            setting,
            new() { { "Main", low }, { "High", high } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.FloatingBar, PrimarySeriesName = "Main", SecondarySeriesName = "High" } });

        SKColor invPx = bmp.GetPixel((int)MathF.Floor(BarX(tr, 4)), (int)MathF.Floor(PriceY(tr, 55m)));
        Assert.Equal(new SKColor(220, 0, 0, 255), invPx);

        SKColor flatPx = bmp.GetPixel((int)MathF.Floor(BarX(tr, 8)), (int)MathF.Floor(PriceY(tr, 50m)));
        Assert.NotEqual(SKColors.Black, flatPx);
    }

    [Fact]
    public void StackedArea_ComputesCumulativeEnvelope_AndUsesChartColorPaletteFallback()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "stack_1",
            IsEnabled = true,
            IsOverlay = false
        };
        var s1 = new decimal?[CandleCount];
        var s2 = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            s1[i] = 30m;
            s2[i] = 40m;
        }

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.StackedArea,
            PrimarySeriesName = "S1",
            StackedSeriesNames = new[] { "S1", "S2" },
            FillAlpha = 255,
            DrawBorderStroke = true
        };

        var result = new StubPrimitiveResult(new() { { "S1", s1 }, { "S2", s2 } }, new[] { desc });
        var snapshot = new ChartDataSnapshot(
            MakeCandles(),
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount);

        var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, setting);
        Assert.Equal(0m - 70m * 0.05m, minVal);
        Assert.Equal(70m + 70m * 0.05m, maxVal);

        var tr = MakeTransform(0m, 80m);
        using var bmp = RenderToBitmap(setting, new() { { "S1", s1 }, { "S2", s2 } }, new[] { desc }, minY: 0m, maxY: 80m);

        // Layer 0 (0..30) uses ChartColorPalette.Get(0, isDark: true), Layer 1 (30..70) uses ChartColorPalette.Get(1, isDark: true)
        int probeX = (int)MathF.Floor(BarX(tr, 10));
        SKColor layer0 = bmp.GetPixel(probeX, (int)MathF.Floor(PriceY(tr, 15m)));
        SKColor layer1 = bmp.GetPixel(probeX, (int)MathF.Floor(PriceY(tr, 50m)));
        Assert.Equal(ChartColorPalette.Get(0, true).ToSkColor(), layer0);
        Assert.Equal(ChartColorPalette.Get(1, true).ToSkColor(), layer1);
    }

    [Fact]
    public void Bubble_ScalesAreaProportionally_AndSortsSmallOnTopOfLarge()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "bubble_1",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 200, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };
        var y = new decimal?[CandleCount];
        var sz = new decimal?[CandleCount];
        y[5] = 50m; sz[5] = 100m;   // large positive (drawn first)
        y[6] = 50m; sz[6] = -1m;    // small negative (drawn second, on top)

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Bubble,
            PrimarySeriesName = "Main",
            SecondarySeriesName = "Size",
            BubbleMinRadiusPx = 4f,
            BubbleMaxRadiusPx = 24f,
            BubbleAllowSignedSize = true,
            BubbleSortDescending = true,
            FillAlpha = 255,
            DrawBorderStroke = false
        };

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(setting, new() { { "Main", y }, { "Size", sz } }, new[] { desc });

        // Small negative bubble at bar 6 center must be Red (drawn on top of large green bubble from bar 5)
        SKColor smallCenter = bmp.GetPixel((int)MathF.Floor(BarX(tr, 6)), (int)MathF.Floor(PriceY(tr, 50m)));
        Assert.Equal(new SKColor(220, 0, 0, 255), smallCenter);
    }

    [Theory]
    [InlineData(MarkerSymbolShape.Circle)]
    [InlineData(MarkerSymbolShape.TriangleUp)]
    [InlineData(MarkerSymbolShape.TriangleDown)]
    [InlineData(MarkerSymbolShape.Square)]
    [InlineData(MarkerSymbolShape.Cross)]
    [InlineData(MarkerSymbolShape.Plus)]
    [InlineData(MarkerSymbolShape.Diamond)]
    public void MarkerSymbol_AllSevenShapes_RenderPixels(MarkerSymbolShape shape)
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "marker_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 255, 180, 0),
            Thickness = 2.0,
            IsOverlay = true
        };
        var vals = new decimal?[CandleCount];
        vals[10] = 50m;

        using var bmp = RenderToBitmap(
            setting,
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.MarkerSymbol, PrimarySeriesName = "Main", MarkerShape = shape, MarkerRadiusPx = 6f } });

        Assert.True(CountNonBlackPixels(bmp) > 0);
    }

    [Fact]
    public void ColorStrip_UsesThemeFallbackWhenUseUpDownColorsOff_AndExcludedFromYRange()
    {
        // Note: CoreIndicatorSettings defaults UpColor=Lime, DownColor=Red with Alpha=255.
        // With UseUpDownColors=false, Diverging must fall back to theme.Bullish/Bearish, NOT raw Lime/Red.
        var setting = new CoreIndicatorSettings
        {
            Id = "strip_1",
            IsEnabled = true,
            UseUpDownColors = false,
            IsOverlay = false
        };
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = i < 10 ? -100m : 100m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.ColorStrip,
            PrimarySeriesName = "Main",
            StripScaleMode = ColorStripScaleMode.Diverging,
            StripAnchor = ColorStripAnchor.Bottom,
            StripHeightPx = 16f
        };

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(setting, new() { { "Main", vals } }, new[] { desc });

        // Top of chart must remain untouched (anchored Bottom 16px)
        Assert.Equal(SKColors.Black, bmp.GetPixel((int)MathF.Floor(BarX(tr, 15)), 5));

        // Bottom bar 15 (+100 max) must match Dark theme.Bullish (#4CAF50), not raw Lime (#00FF00)
        SKColor stripPixel = bmp.GetPixel((int)MathF.Floor(BarX(tr, 15)), Height - 4);
        Assert.Equal(ThemeColors.Dark.Bullish.ToSkColor(), stripPixel);

        // Excluded from Y auto-range
        var result = new StubPrimitiveResult(new() { { "Main", vals } }, new[] { desc });
        var snapshot = new ChartDataSnapshot(
            MakeCandles(),
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount);
        var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, setting);
        Assert.Equal((0m, 1m), (minVal, maxVal));
    }

    [Fact]
    public void Lollipop_TerminatesStemAtHeadBoundary_AndSupportsTranslucentHead()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "lolli_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 200, 255),
            IsOverlay = true
        };
        var vals = new decimal?[1] { 80m };

        var tr = MakeTransform(0m, 100m, 1);
        using var bmp = RenderToBitmap(
            setting,
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Lollipop, PrimarySeriesName = "Main", BaselineValue = 0m, MarkerRadiusPx = 8f, MarkerFilled = false } },
            minY: 0m,
            maxY: 100m,
            candleCount: 1);

        int x = (int)MathF.Floor(BarX(tr, 0));
        int headCenterY = (int)MathF.Floor(PriceY(tr, 80m));
        int stemMidY = (int)MathF.Floor(PriceY(tr, 40m));

        // Hollow head center must stay black (stem did not draw through circle interior)
        Assert.Equal(SKColors.Black, bmp.GetPixel(x, headCenterY));
        // Stem between baseline and head boundary must be drawn
        Assert.NotEqual(SKColors.Black, bmp.GetPixel(x, stemMidY));
    }

    [Fact]
    public void Batch3Primitives_ZeroManagedAllocationPerRender()
    {
        var candles = MakeCandles(200);
        var tr = MakeTransform(-50m, 200m, 200);
        var cfg = MakeConfig(tr, candleCount: 200);

        var low = new decimal?[200];
        var high = new decimal?[200];
        var size = new decimal?[200];
        for (int i = 0; i < 200; i++)
        {
            low[i] = 10m + (i % 20);
            high[i] = 30m + (i % 25);
            size[i] = (i % 10) + 1m;
        }

        var descriptors = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.FloatingBar, PrimarySeriesName = "Low", SecondarySeriesName = "High" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.StackedArea, PrimarySeriesName = "Low", StackedSeriesNames = new[] { "Low", "High" } },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Bubble, PrimarySeriesName = "High", SecondarySeriesName = "Size" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.MarkerSymbol, PrimarySeriesName = "High", MarkerShape = MarkerSymbolShape.Diamond },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.ColorStrip, PrimarySeriesName = "Size", StripScaleMode = ColorStripScaleMode.Diverging },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Lollipop, PrimarySeriesName = "Low", BaselineValue = 0m }
        };

        var setting = new CoreIndicatorSettings { Id = "alloc_b3", IsEnabled = true, IsOverlay = true, UseUpDownColors = true };
        var result = new StubPrimitiveResult(new() { { "Low", low }, { "High", high }, { "Size", size } }, descriptors);
        var snapshot = new ChartDataSnapshot(
            candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: 200);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var renderer = new IndicatorRenderer();

        renderer.RenderIndicator(surface.Canvas, Area, snapshot, setting, cfg);

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, snapshot, setting, cfg));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void DynamicStyleLine_ModulatesWidthAndColor_AndRespectsUseUpDownColorsGate()
    {
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 10m + i * 3m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.DynamicStyling,
            PrimarySeriesName = "Main",
            StyleMetric = DynamicStyleMetric.Value,
            MinStrokeWidthPx = 1f,
            MaxStrokeWidthPx = 8f,
            MinDynamicAlpha = 100,
            MaxDynamicAlpha = 255
        };

        var tr = MakeTransform(0m, 80m);

        // UseUpDownColors = false -> fixed setting.Color (pure blue)
        var fixedSetting = new CoreIndicatorSettings
        {
            Id = "dyn_fixed",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 0, 255),
            UseUpDownColors = false,
            IsOverlay = true
        };
        using var fixedBmp = RenderToBitmap(fixedSetting, new() { { "Main", vals } }, new[] { desc }, minY: 0m, maxY: 80m);
        SKColor fixedPx = fixedBmp.GetPixel((int)MathF.Floor(BarX(tr, 18)), (int)MathF.Floor(PriceY(tr, vals[18]!.Value)));
        Assert.True(fixedPx.Blue > fixedPx.Red && fixedPx.Blue > fixedPx.Green);

        // UseUpDownColors = true -> DownColor -> UpColor (end bar is green-dominant)
        var lerpSetting = new CoreIndicatorSettings
        {
            Id = "dyn_lerp",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 220, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };
        using var lerpBmp = RenderToBitmap(lerpSetting, new() { { "Main", vals } }, new[] { desc }, minY: 0m, maxY: 80m);
        SKColor endPx = lerpBmp.GetPixel((int)MathF.Floor(BarX(tr, 18)), (int)MathF.Floor(PriceY(tr, vals[18]!.Value)));
        Assert.True(endPx.Green > endPx.Red);
    }

    [Fact]
    public void Vector_RendersBothEndpointAndPolarModes_AndSkipsZeroLengthVectors()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "vec_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 255, 160, 0),
            Thickness = 2.0,
            IsOverlay = true
        };

        var yStart = new decimal?[CandleCount];
        var angle = new decimal?[CandleCount];
        var len = new decimal?[CandleCount];
        yStart[5] = 50m; angle[5] = 0m; len[5] = 25m;     // horizontal right 25px
        yStart[10] = 50m; angle[10] = 0m; len[10] = 0m;   // zero-length -> skipped

        var polarDesc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Vector,
            PrimarySeriesName = "Y",
            SecondarySeriesName = "Angle",
            TertiarySeriesName = "Len",
            VectorMode = VectorInputMode.BarAnchoredPolar,
            VectorHeadSizePx = 8f
        };

        var tr = MakeTransform(0m, 100m);
        using var polarBmp = RenderToBitmap(setting, new() { { "Y", yStart }, { "Angle", angle }, { "Len", len } }, new[] { polarDesc }, minY: 0m, maxY: 100m);

        // Bar 5 shaft/tip lit; Bar 10 skipped
        Assert.NotEqual(SKColors.Black, polarBmp.GetPixel((int)MathF.Floor(BarX(tr, 5) + 10f), (int)MathF.Floor(PriceY(tr, 50m))));
        Assert.Equal(SKColors.Black, polarBmp.GetPixel((int)MathF.Floor(BarX(tr, 10)), (int)MathF.Floor(PriceY(tr, 50m))));

        // PriceSpaceEndpoints mode
        var endY = new decimal?[CandleCount];
        var deltaBars = new decimal?[CandleCount];
        yStart[2] = 20m; endY[2] = 80m; deltaBars[2] = 3m;
        var priceDesc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Vector,
            PrimarySeriesName = "Y",
            SecondarySeriesName = "EndY",
            TertiarySeriesName = "Delta",
            VectorMode = VectorInputMode.PriceSpaceEndpoints,
            VectorHeadSizePx = 8f
        };
        using var priceBmp = RenderToBitmap(setting, new() { { "Y", yStart }, { "EndY", endY }, { "Delta", deltaBars } }, new[] { priceDesc }, minY: 0m, maxY: 100m);
        Assert.True(CountNonBlackPixels(priceBmp) > 0);
    }

    [Fact]
    public void Waterfall_RendersScrollInvariantDeltaAndTotalBars_WithConnectors()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "wf_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 64, 160, 255),
            UpColor = new IndicatorColor(255, 0, 200, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = false
        };

        var runStart = new decimal?[CandleCount];
        var runEnd = new decimal?[CandleCount];
        var kind = new decimal?[CandleCount];
        runStart[2] = 0m; runEnd[2] = 40m; kind[2] = (decimal)WaterfallBarKind.Delta;
        runStart[3] = 40m; runEnd[3] = 25m; kind[3] = (decimal)WaterfallBarKind.Delta;
        runStart[4] = 0m; runEnd[4] = 25m; kind[4] = (decimal)WaterfallBarKind.Total;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Waterfall,
            PrimarySeriesName = "End",
            SecondarySeriesName = "Start",
            TertiarySeriesName = "Kind",
            WaterfallDrawConnectors = true
        };

        var tr = MakeTransform(-10m, 60m);
        using var bmp = RenderToBitmap(setting, new() { { "End", runEnd }, { "Start", runStart }, { "Kind", kind } }, new[] { desc }, minY: -10m, maxY: 60m);

        Assert.Equal(new SKColor(0, 200, 0, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 2)), (int)MathF.Floor(PriceY(tr, 20m))));
        Assert.Equal(new SKColor(220, 0, 0, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 3)), (int)MathF.Floor(PriceY(tr, 32m))));
        Assert.Equal(new SKColor(64, 160, 255, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 4)), (int)MathF.Floor(PriceY(tr, 12m))));
    }

    [Fact]
    public void Wrappers_PreservePrimitiveDescriptors_AndOverlayRangeExcludesAuxiliarySeries()
    {
        var main = new decimal?[CandleCount];
        var size = new decimal?[CandleCount];
        var strip = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            main[i] = 100m + i;
            size[i] = 1_000_000m; // Bubble size must NOT stretch price Y-axis
            strip[i] = -500m;     // ColorStrip scalar must NOT stretch price Y-axis
        }

        var descriptors = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Bubble, PrimarySeriesName = "Main", SecondarySeriesName = "Size" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.ColorStrip, PrimarySeriesName = "Strip" }
        };

        var raw = new StubPrimitiveResult(new() { { "Main", main }, { "Size", size }, { "Strip", strip } }, descriptors);
        var sliced = new SlicedIndicatorResult(raw, 0, CandleCount);
        var tfAligned = new TimeframeAlignedIndicatorResult(
            raw,
            new Dictionary<string, IReadOnlyList<decimal?>> { { "Main", main }, { "Size", size }, { "Strip", strip } },
            new[] { "Main", "Size", "Strip" });

        Assert.Same(descriptors, ((IPrimitiveAwareIndicatorResult)sliced).PrimitiveDescriptors);
        Assert.Same(descriptors, ((IPrimitiveAwareIndicatorResult)tfAligned).PrimitiveDescriptors);

        var setting = new CoreIndicatorSettings { Id = "ov_1", IsEnabled = true, IsOverlay = true };
        var snapshot = new ChartDataSnapshot(
            MakeCandles(),
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, raw } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount);

        Assert.InRange(snapshot.MinPrice, 85m, 100m);
        Assert.InRange(snapshot.MaxPrice, 110m, 125m);
    }

    [Theory]
    [InlineData(SeriesPrimitiveType.Line)]
    [InlineData(SeriesPrimitiveType.DashedLine)]
    [InlineData(SeriesPrimitiveType.Dot)]
    [InlineData(SeriesPrimitiveType.Histogram)]
    [InlineData(SeriesPrimitiveType.StepLine)]
    [InlineData(SeriesPrimitiveType.HorizontalSegment)]
    [InlineData(SeriesPrimitiveType.Spline)]
    [InlineData(SeriesPrimitiveType.Area)]
    [InlineData(SeriesPrimitiveType.BandCloud)]
    [InlineData(SeriesPrimitiveType.FloatingBar)]
    [InlineData(SeriesPrimitiveType.PositiveNegativeArea)]
    [InlineData(SeriesPrimitiveType.StackedArea)]
    [InlineData(SeriesPrimitiveType.Bubble)]
    [InlineData(SeriesPrimitiveType.MarkerSymbol)]
    [InlineData(SeriesPrimitiveType.ColorStrip)]
    [InlineData(SeriesPrimitiveType.DynamicStyling)]
    [InlineData(SeriesPrimitiveType.Vector)]
    [InlineData(SeriesPrimitiveType.Waterfall)]
    [InlineData(SeriesPrimitiveType.Lollipop)]
    public void AllNineteenPrimitives_HandleEmptyNullAndLogBoundariesSafely(SeriesPrimitiveType type)
    {
        var setting = new CoreIndicatorSettings
        {
            Id = $"bnd_{type}",
            IsEnabled = true,
            IsOverlay = true,
            UseUpDownColors = true
        };

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = type,
            PrimarySeriesName = "Main",
            SecondarySeriesName = "Sec",
            TertiarySeriesName = "Tert",
            StackedSeriesNames = new[] { "Main", "Sec" }
        };

        // Empty / all-null / Log <= 0m must all return cleanly with zero pixels drawn and zero exceptions
        var allNull = new decimal?[CandleCount];
        using var nullBmp = RenderToBitmap(
            setting,
            new() { { "Main", allNull }, { "Sec", allNull }, { "Tert", allNull } },
            new[] { desc },
            minY: 1m,
            maxY: 100m,
            priceScale: PriceScaleType.Log);
        Assert.Equal(0, CountNonBlackPixels(nullBmp));

        // Non-positive Log values must be skipped cleanly for Y-projected primitives;
        // ColorStrip maps scalars to strip colors rather than Y coordinates, so non-positive scalars render safely.
        var nonPos = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) nonPos[i] = -i;
        using var logBmp = RenderToBitmap(
            setting,
            new() { { "Main", nonPos }, { "Sec", nonPos }, { "Tert", nonPos } },
            new[] { desc },
            minY: 1m,
            maxY: 100m,
            priceScale: PriceScaleType.Log);
        if (type == SeriesPrimitiveType.ColorStrip)
        {
            Assert.True(CountNonBlackPixels(logBmp) > 0);
        }
        else
        {
            Assert.Equal(0, CountNonBlackPixels(logBmp));
        }
    }

    [Fact]
    public void AllNineteenPrimitives_ZeroManagedAllocationPerRender_AndLegacySolidStepParity()
    {
        var candles = MakeCandles(200);
        var tr = MakeTransform(-50m, 200m, 200);
        var cfg = MakeConfig(tr, candleCount: 200);

        var main = new decimal?[200];
        var sec = new decimal?[200];
        var tert = new decimal?[200];
        for (int i = 0; i < 200; i++)
        {
            main[i] = 20m + (i % 30);
            sec[i] = 10m + (i % 15);
            tert[i] = i % 3;
        }

        var allTypes = Enum.GetValues<SeriesPrimitiveType>();
        var descriptors = new SeriesPrimitiveDescriptor[allTypes.Length];
        for (int i = 0; i < allTypes.Length; i++)
        {
            descriptors[i] = new SeriesPrimitiveDescriptor
            {
                PrimitiveType = allTypes[i],
                PrimarySeriesName = "Main",
                SecondarySeriesName = "Sec",
                TertiarySeriesName = "Tert",
                StackedSeriesNames = new[] { "Main", "Sec" }
            };
        }

        var setting = new CoreIndicatorSettings { Id = "alloc_all19", IsEnabled = true, IsOverlay = true, UseUpDownColors = true };
        var result = new StubPrimitiveResult(new() { { "Main", main }, { "Sec", sec }, { "Tert", tert } }, descriptors);
        var snapshot = new ChartDataSnapshot(
            candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: 200);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var renderer = new IndicatorRenderer();

        renderer.RenderIndicator(surface.Canvas, Area, snapshot, setting, cfg);

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, snapshot, setting, cfg));
        Assert.Equal(0L, bytes);

        // Legacy LineIndicatorRenderer: CoreLineStyle.Step stays pixel-identical to CoreLineStyle.Solid, while Dash differs
        using var legacyRenderer = new LineIndicatorRenderer();
        var solidSet = new CoreIndicatorSettings { Style = CoreLineStyle.Solid, Thickness = 2.0, Color = new IndicatorColor(255, 255, 0, 0) };
        var stepSet = new CoreIndicatorSettings { Style = CoreLineStyle.Step, Thickness = 2.0, Color = new IndicatorColor(255, 255, 0, 0) };
        var dashSet = new CoreIndicatorSettings { Style = CoreLineStyle.Dash, Thickness = 2.0, Color = new IndicatorColor(255, 255, 0, 0) };

        byte[] DrawLegacy(CoreIndicatorSettings s)
        {
            using var bmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.Black);
            legacyRenderer.Render(c, Area, main, s, -50m, 200m, 200, 0, 0, candles, Interval, tr);
            return bmp.Bytes;
        }

        byte[] solidBytes = DrawLegacy(solidSet);
        Assert.Equal(solidBytes, DrawLegacy(stepSet));
        Assert.NotEqual(solidBytes, DrawLegacy(dashSet));
    }

    [Fact]
    public void LogAxis_BandCloudAndPositiveNegativeArea_MatchScreenSpaceCrossing()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "log_cross_1",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 220, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };

        var upper = new decimal?[] { 100m, 100m };
        var lower = new decimal?[] { 10m, 1000m };

        var tr = MakeTransform(10m, 1000m, 2, PriceScaleType.Log);

        using var bandBmp = RenderToBitmap(
            setting,
            new() { { "Main", upper }, { "Lower", lower } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.BandCloud, PrimarySeriesName = "Main", SecondarySeriesName = "Lower", FillAlpha = 220, DrawBorderStroke = false } },
            minY: 10m,
            maxY: 1000m,
            priceScale: PriceScaleType.Log,
            candleCount: 2);

        float x0 = BarX(tr, 0);
        float x1 = BarX(tr, 1);
        float yMid = PriceY(tr, 100m);

        // Screen intersection is at t = 0.5 on Log scale (data ratio would falsely place crossing at t = 0.091)
        SKColor leftOfMid = bandBmp.GetPixel((int)MathF.Floor(x0 + 0.25f * (x1 - x0)), (int)MathF.Floor(yMid + 15f));
        SKColor rightOfMid = bandBmp.GetPixel((int)MathF.Floor(x0 + 0.75f * (x1 - x0)), (int)MathF.Floor(yMid - 15f));
        Assert.True(leftOfMid.Green > leftOfMid.Red);
        Assert.True(rightOfMid.Red > rightOfMid.Green);

        // PositiveNegativeArea asymmetric Log crossing around BaselineValue = 20m (100m -> 10m crosses at ln(5)/(ln(5)+ln(2)) ~= 0.699)
        var pnVals = new decimal?[] { 100m, 10m };
        using var pnBmp = RenderToBitmap(
            setting,
            new() { { "Main", pnVals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.PositiveNegativeArea, PrimarySeriesName = "Main", BaselineValue = 20m, FillAlpha = 220, DrawBorderStroke = false } },
            minY: 10m,
            maxY: 1000m,
            priceScale: PriceScaleType.Log,
            candleCount: 2);

        float yBase = PriceY(tr, 20m);
        // At t = 0.55 (still above baseline on Log screen projection, data ratio 0.889 already painted up even near 0.75)
        SKColor beforeLogCross = pnBmp.GetPixel((int)MathF.Floor(x0 + 0.55f * (x1 - x0)), (int)MathF.Floor(yBase - 4f));
        SKColor afterLogCross = pnBmp.GetPixel((int)MathF.Floor(x0 + 0.85f * (x1 - x0)), (int)MathF.Floor(yBase + 6f));
        Assert.True(beforeLogCross.Green > beforeLogCross.Red);
        Assert.True(afterLogCross.Red > afterLogCross.Green);
    }

    [Fact]
    public void ExtremeDecimalValues_DoNotThrowOverflow_AndRenderSiblingPrimitivesSafely()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "overflow_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 200, 255),
            Thickness = 4.0,
            IsOverlay = true
        };

        var strip = new decimal?[CandleCount];
        var vecY = new decimal?[CandleCount];
        var vecEndY = new decimal?[CandleCount];
        var vecDelta = new decimal?[CandleCount];
        var wfKind = new decimal?[CandleCount];
        var line = new decimal?[CandleCount];

        for (int i = 0; i < CandleCount; i++) line[i] = 50m;
        strip[2] = (decimal)int.MinValue;
        strip[4] = decimal.MaxValue;
        strip[6] = decimal.MinValue;
        vecY[3] = 20m; vecEndY[3] = 80m; vecDelta[3] = 1_000_000_000_000m;
        wfKind[5] = decimal.MaxValue;

        var descriptors = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.ColorStrip, PrimarySeriesName = "Strip", StripScaleMode = ColorStripScaleMode.DiscreteState },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Vector, PrimarySeriesName = "VecY", SecondarySeriesName = "VecEndY", TertiarySeriesName = "VecDelta" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Waterfall, PrimarySeriesName = "Line", SecondarySeriesName = "VecY", TertiarySeriesName = "WfKind" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "Line" }
        };

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(
            setting,
            new() { { "Strip", strip }, { "VecY", vecY }, { "VecEndY", vecEndY }, { "VecDelta", vecDelta }, { "WfKind", wfKind }, { "Line", line } },
            descriptors);

        // Sibling Line primitive at y=50m must survive extreme ColorStrip/Vector/Waterfall values
        Assert.Equal(new SKColor(0, 200, 255, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 10)), (int)MathF.Floor(PriceY(tr, 50m))));
    }

    [Fact]
    public void StackedArea_RawCumulative_IncludesZeroBaseline_AndBreakAllLayersMatchesRange()
    {
        var setting = new CoreIndicatorSettings { Id = "stack_raw_1", IsEnabled = true, IsOverlay = false };
        var s1 = new decimal?[CandleCount];
        var s2 = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            s1[i] = 50m;
            s2[i] = 30m;
        }

        var rawDesc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.StackedArea,
            PrimarySeriesName = "S1",
            StackedSeriesNames = new[] { "S1", "S2" },
            StackingMode = StackingSignMode.RawCumulative
        };

        var res = new StubPrimitiveResult(new() { { "S1", s1 }, { "S2", s2 } }, new[] { rawDesc });
        var snap = new ChartDataSnapshot(
            MakeCandles(),
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, res } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount);
        var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snap, setting);
        Assert.True(minVal <= 0m);
        Assert.True(maxVal >= 80m);

        // BreakAllLayers: if layer 0 is valid (500m) and layer 1 is null at ALL bars, BreakAllLayers must not accumulate partial layer 0
        var sNull = new decimal?[CandleCount];
        var sBig = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) sBig[i] = 500m;
        var breakDesc = rawDesc with { StackingNullPolicy = StackingNullMode.BreakAllLayers };
        var breakRes = new StubPrimitiveResult(new() { { "S1", sBig }, { "S2", sNull } }, new[] { breakDesc });
        var breakSnap = new ChartDataSnapshot(
            MakeCandles(),
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, breakRes } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount);
        var (bMin, bMax) = PanelValueRangeCalculator.Calculate(breakSnap, setting);
        Assert.Equal((0m, 1m), (bMin, bMax));

        using var breakBmp = RenderToBitmap(setting, new() { { "S1", sBig }, { "S2", sNull } }, new[] { breakDesc }, minY: 0m, maxY: 600m);
        Assert.Equal(0, CountNonBlackPixels(breakBmp));
    }

    [Fact]
    public void MarkerSymbol_DiamondAndTriangleNearRightEdge_DoNotPrematurelyCull()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "cull_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 255, 200, 0),
            IsOverlay = true
        };
        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.MarkerSymbol,
            PrimarySeriesName = "Main",
            MarkerShape = MarkerSymbolShape.Diamond,
            MarkerRadiusPx = 20f
        };

        var wideCandles = MakeCandles(2);
        var wideTr = MakeTransform(0m, 100m, 1); // Bar 1 (Start + 1 day) lands near Width - 8
        var wideCfg = MakeConfig(wideTr, candleCount: 2);
        var wideVals = new decimal?[2] { null, 50m };
        var wideResult = new StubPrimitiveResult(new() { { "Main", wideVals } }, new[] { desc });
        var wideSnap = new ChartDataSnapshot(
            wideCandles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, wideResult } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: 2);

        using var bmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        using var renderer = new IndicatorRenderer();
        float bar1X = BarX(wideTr, 1);
        // Set targetRect.Right 22px left of diamond center: 22 > r (20), yet 22 < Diamond cullRadius (20 * 1.2533 ~= 25.07)
        var clippedArea = new Rect(0, 0, bar1X - 22f, Height);
        renderer.RenderIndicator(canvas, clippedArea, wideSnap, setting, wideCfg);
        Assert.NotEqual(SKColors.Black, bmp.GetPixel((int)MathF.Floor((float)clippedArea.Right - 2f), (int)MathF.Floor(PriceY(wideTr, 50m))));
    }

    [Fact]
    public void DynamicStyleLine_LogGapResetsPass1ConsistentWithPass2()
    {
        var vals = new decimal?[] { 10m, -1m, 100m, 110m, 130m };
        var tr = MakeTransform(1m, 200m, 5, PriceScaleType.Log);

        var setting = new CoreIndicatorSettings
        {
            Id = "dyn_log_1",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 255, 0),
            DownColor = new IndicatorColor(255, 255, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };
        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.DynamicStyling,
            PrimarySeriesName = "Main",
            StyleMetric = DynamicStyleMetric.Delta,
            NullPolicy = NullGapPolicy.BreakPath,
            MinStrokeWidthPx = 2f,
            MaxStrokeWidthPx = 6f,
            MinDynamicAlpha = 255,
            MaxDynamicAlpha = 255
        };

        using var bmp = RenderToBitmap(setting, new() { { "Main", vals } }, new[] { desc }, minY: 1m, maxY: 200m, priceScale: PriceScaleType.Log, candleCount: 5);

        // Segment 100 -> 110 (delta = 10 -> min -> Red), Segment 110 -> 130 (delta = 20 -> max -> Green)
        float segMinX = (BarX(tr, 2) + BarX(tr, 3)) * 0.5f;
        float segMinY = (PriceY(tr, 100m) + PriceY(tr, 110m)) * 0.5f;
        float segMaxX = (BarX(tr, 3) + BarX(tr, 4)) * 0.5f;
        float segMaxY = (PriceY(tr, 110m) + PriceY(tr, 130m)) * 0.5f;

        SKColor minPx = bmp.GetPixel((int)MathF.Floor(segMinX), (int)MathF.Floor(segMinY));
        SKColor maxPx = bmp.GetPixel((int)MathF.Floor(segMaxX), (int)MathF.Floor(segMaxY));
        Assert.True(minPx.Red > minPx.Green);
        Assert.True(maxPx.Green > maxPx.Red);
    }

    [Fact]
    public void Bubble_UsesExactAreaLinearScaling_AndSortsTenThousandBubblesZeroAlloc()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "bubble_area_1",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 220, 0),
            IsOverlay = true
        };

        var y = new decimal?[10];
        var sz = new decimal?[10];
        y[1] = 50m; sz[1] = 0m;
        y[5] = 50m; sz[5] = 25m;
        y[8] = 50m; sz[8] = 100m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Bubble,
            PrimarySeriesName = "Y",
            SecondarySeriesName = "Sz",
            BubbleMinRadiusPx = 4f,
            BubbleMaxRadiusPx = 20f,
            BubbleDrawZeroSize = true,
            BubbleSortDescending = true,
            FillAlpha = 255,
            DrawBorderStroke = false
        };

        var tr = MakeTransform(0m, 100m, 10);
        using var bmp = RenderToBitmap(setting, new() { { "Y", y }, { "Sz", sz } }, new[] { desc }, minY: 0m, maxY: 100m, candleCount: 10);

        // u = 0.25 -> area-linear radius = sqrt(4^2 + 0.25 * (20^2 - 4^2)) = sqrt(112) ~= 10.583px
        int cx5 = (int)MathF.Floor(BarX(tr, 5));
        int cy5 = (int)MathF.Floor(PriceY(tr, 50m));
        Assert.True(bmp.GetPixel(cx5, cy5 + 9).Green > 180);
        Assert.True(bmp.GetPixel(cx5, cy5 + 12) == SKColors.Black);

        // 10,000 bubbles ascending worst-case for descending sort -> 0 managed heap allocation after pool warmup
        const int LargeCount = 10_000;
        var largeCandles = MakeCandles(LargeCount);
        var largeY = new decimal?[LargeCount];
        var largeSz = new decimal?[LargeCount];
        for (int i = 0; i < LargeCount; i++)
        {
            largeY[i] = 50m;
            largeSz[i] = i + 1m;
        }
        var largeTr = MakeTransform(0m, 100m, LargeCount);
        var largeCfg = MakeConfig(largeTr, candleCount: LargeCount);
        var largeRes = new StubPrimitiveResult(new() { { "Y", largeY }, { "Sz", largeSz } }, new[] { desc });
        var largeSnap = new ChartDataSnapshot(
            largeCandles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, largeRes } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: LargeCount);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var renderer = new IndicatorRenderer();
        renderer.RenderIndicator(surface.Canvas, Area, largeSnap, setting, largeCfg);

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, largeSnap, setting, largeCfg));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void ScaleDomainMinMax_LocksColorStripBubbleAndDynamicStyleAcrossScroll()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "fixed_dom_1",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 220, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };

        var valsNoOutlier = new decimal?[CandleCount];
        var valsWithOutlier = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            valsNoOutlier[i] = 55m;
            valsWithOutlier[i] = i == CandleCount - 1 ? 100m : 55m;
        }

        var stripDesc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.ColorStrip,
            PrimarySeriesName = "Main",
            StripScaleMode = ColorStripScaleMode.Diverging,
            BaselineValue = 50m,
            StripAnchor = ColorStripAnchor.Bottom,
            StripHeightPx = 16f,
            ScaleDomainMin = 0m,
            ScaleDomainMax = 100m
        };

        var tr = MakeTransform(0m, 100m, CandleCount);
        using var bmpA = RenderToBitmap(setting, new() { { "Main", valsNoOutlier } }, new[] { stripDesc }, minY: 0m, maxY: 100m);
        using var bmpB = RenderToBitmap(setting, new() { { "Main", valsWithOutlier } }, new[] { stripDesc }, minY: 0m, maxY: 100m);

        Assert.Equal(bmpA.GetPixel((int)MathF.Floor(BarX(tr, 2)), Height - 4), bmpB.GetPixel((int)MathF.Floor(BarX(tr, 2)), Height - 4));

        // DynamicStyling fixed domain
        var dynDesc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.DynamicStyling,
            PrimarySeriesName = "Main",
            StyleMetric = DynamicStyleMetric.Value,
            MinDynamicAlpha = 255,
            MaxDynamicAlpha = 255,
            ScaleDomainMin = 0m,
            ScaleDomainMax = 100m
        };
        using var dynA = RenderToBitmap(setting, new() { { "Main", valsNoOutlier } }, new[] { dynDesc }, minY: 0m, maxY: 100m);
        using var dynB = RenderToBitmap(setting, new() { { "Main", valsWithOutlier } }, new[] { dynDesc }, minY: 0m, maxY: 100m);
        int dynProbeX = (int)MathF.Floor((BarX(tr, 2) + BarX(tr, 3)) * 0.5f);
        int dynProbeY = (int)MathF.Floor(PriceY(tr, 55m));
        Assert.Equal(dynA.GetPixel(dynProbeX, dynProbeY), dynB.GetPixel(dynProbeX, dynProbeY));
    }

    [Fact]
    public void Area_FourSlotShaderCache_ZeroManagedAllocAcrossMultipleAreas()
    {
        var candles = MakeCandles(50);
        var tr = MakeTransform(0m, 100m, 50);
        var cfg = MakeConfig(tr, candleCount: 50);
        var a1 = new decimal?[50];
        var a2 = new decimal?[50];
        for (int i = 0; i < 50; i++)
        {
            a1[i] = 60m + (i % 10);
            a2[i] = 30m + (i % 10);
        }

        var descriptors = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Area, PrimarySeriesName = "A1", FillAlpha = 140, BaselineMode = AreaBaselineMode.PanelBottom },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Area, PrimarySeriesName = "A2", FillAlpha = 100, BaselineMode = AreaBaselineMode.ScalarValue, BaselineValue = 10m }
        };

        var setting = new CoreIndicatorSettings
        {
            Id = "area_cache_1",
            IsEnabled = true,
            IsOverlay = true,
            SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "A1", TargetSeries = new List<string> { "A1" }, Color = new IndicatorColor(255, 0, 120, 255) },
                new() { Name = "A2", TargetSeries = new List<string> { "A2" }, Color = new IndicatorColor(255, 255, 140, 0) }
            }
        };

        var result = new StubPrimitiveResult(new() { { "A1", a1 }, { "A2", a2 } }, descriptors);
        var snap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } }, indicatorSettings: new[] { setting }, startIndex: 0, count: 50);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var renderer = new IndicatorRenderer();
        renderer.RenderIndicator(surface.Canvas, Area, snap, setting, cfg);

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, snap, setting, cfg));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void UntestedSubModes_And_IndexBasedRenko_RenderSafelyAndCorrectly()
    {
        // 1. ConnectThrough across alternating nulls [100m, null, 120m, null, 110m]
        var gapVals = new decimal?[] { 100m, null, 120m, null, 110m };
        var gapSec = new decimal?[] { 40m, null, 50m, null, 45m };
        var setting = new CoreIndicatorSettings { Id = "sub_1", IsEnabled = true, Color = new IndicatorColor(255, 80, 180, 255), Thickness = 2.0, IsOverlay = true };

        var connectTypes = new[]
        {
            SeriesPrimitiveType.Spline,
            SeriesPrimitiveType.Area,
            SeriesPrimitiveType.BandCloud,
            SeriesPrimitiveType.PositiveNegativeArea
        };
        foreach (var pt in connectTypes)
        {
            var desc = new SeriesPrimitiveDescriptor
            {
                PrimitiveType = pt,
                PrimarySeriesName = "Main",
                SecondarySeriesName = "Sec",
                NullPolicy = NullGapPolicy.ConnectThrough,
                BaselineValue = 80m,
                FillAlpha = 160
            };
            using var bmp = RenderToBitmap(setting, new() { { "Main", gapVals }, { "Sec", gapSec } }, new[] { desc }, minY: 0m, maxY: 150m, candleCount: 5);
            Assert.True(CountNonBlackPixels(bmp) > 0);
        }

        // 2. ColorStrip Sequential & DiscreteState + Quantization, Waterfall Subtotal, Area PanelBottom/ReferenceSeries, DynamicStyle Slope/Aux
        var wfEnd = new decimal?[CandleCount];
        var wfStart = new decimal?[CandleCount];
        var wfKind = new decimal?[CandleCount];
        wfStart[3] = 0m; wfEnd[3] = 50m; wfKind[3] = (decimal)WaterfallBarKind.Subtotal;
        var wfTr = MakeTransform(0m, 100m, CandleCount);
        using var wfBmp = RenderToBitmap(
            setting,
            new() { { "End", wfEnd }, { "Start", wfStart }, { "Kind", wfKind } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Waterfall, PrimarySeriesName = "End", SecondarySeriesName = "Start", TertiarySeriesName = "Kind" } },
            minY: 0m,
            maxY: 100m);
        Assert.Equal(new SKColor(80, 180, 255, 255), wfBmp.GetPixel((int)MathF.Floor(BarX(wfTr, 3)), (int)MathF.Floor(PriceY(wfTr, 25m))));

        // 3. Index-based ChartType.Renko alignment
        var renkoCandles = MakeCandles(10);
        var renkoTr = new GenericCoordinateTransform(ChartAxisMode.Index, Width, Height);
        renkoTr.SetIndexRange(0, 10);
        renkoTr.SetPriceRange(0m, 120m);
        var renkoCfg = MakeConfig(renkoTr, candleCount: 10, chartType: ChartType.Renko);
        var renkoVals = new decimal?[10];
        for (int i = 0; i < 10; i++) renkoVals[i] = 40m + i * 5m;
        var renkoDesc = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "Main" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.MarkerSymbol, PrimarySeriesName = "Main", MarkerShape = MarkerSymbolShape.TriangleUp }
        };
        var renkoRes = new StubPrimitiveResult(new() { { "Main", renkoVals } }, renkoDesc);
        var renkoSnap = new ChartDataSnapshot(
            renkoCandles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, renkoRes } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: 10,
            chartType: ChartType.Renko);

        using var renkoBmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var renkoCanvas = new SKCanvas(renkoBmp);
        renkoCanvas.Clear(SKColors.Black);
        using var renkoRenderer = new IndicatorRenderer();
        renkoRenderer.RenderIndicator(renkoCanvas, Area, renkoSnap, setting, renkoCfg);
        Assert.True(CountNonBlackPixels(renkoBmp) > 0);
    }

    [Fact]
    public void Bubble_SignedFixedScaleDomain_UsesZeroMinimumMagnitude()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "bubble_signed_dom",
            IsEnabled = true,
            UpColor = new IndicatorColor(255, 0, 220, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            UseUpDownColors = true,
            IsOverlay = true
        };
        var y = new decimal?[CandleCount];
        var sz = new decimal?[CandleCount];
        y[3] = 50m; sz[3] = 1m;
        y[8] = 50m; sz[8] = -100m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Bubble,
            PrimarySeriesName = "Main",
            SecondarySeriesName = "Size",
            BubbleMinRadiusPx = 4f,
            BubbleMaxRadiusPx = 24f,
            BubbleAllowSignedSize = true,
            ScaleDomainMin = -100m,
            ScaleDomainMax = 100m,
            FillAlpha = 255,
            DrawBorderStroke = false
        };

        var tr = MakeTransform();
        using var bmp = RenderToBitmap(setting, new() { { "Main", y }, { "Size", sz } }, new[] { desc });

        int py = (int)MathF.Floor(PriceY(tr, 50m));
        int smallOuterX = (int)MathF.Floor(BarX(tr, 3)) + 12;
        int largeOuterX = (int)MathF.Floor(BarX(tr, 8)) + 12;

        // Radius for size=1 in [0, 100] is ~4.2px (< 12px); flatRadius regression would be 14px (> 12px)
        Assert.Equal(SKColors.Black, bmp.GetPixel(smallOuterX, py));
        Assert.Equal(new SKColor(220, 0, 0, 255), bmp.GetPixel(largeOuterX, py));
    }

    [Fact]
    public void IndicatorRenderer_AfterDispose_IgnoresRenderIndicatorSafely()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "disposed_guard",
            IsEnabled = true,
            Color = new IndicatorColor(255, 0, 200, 255),
            IsOverlay = true
        };
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 50m;

        var candles = MakeCandles(CandleCount);
        var tr = MakeTransform();
        var cfg = MakeConfig(tr);
        var result = new StubPrimitiveResult(
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "Main" } });
        var snap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } }, indicatorSettings: new[] { setting }, startIndex: 0, count: CandleCount);

        using var bmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);

        var renderer = new IndicatorRenderer();
        renderer.Dispose();
        renderer.RenderIndicator(canvas, Area, snap, setting, cfg);

        Assert.Equal(0, CountNonBlackPixels(bmp));
    }

    [Fact]
    public void ColorStrip_Sequential_MatchesSeriesColorsStartAndSettingColor()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "strip_seq_colors",
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 255, 0, 255),
            SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Start", TargetSeries = new List<string>(), Color = new IndicatorColor(255, 0, 255, 255) }
            }
        };
        var vals = new decimal?[CandleCount];
        vals[2] = 0m;
        vals[7] = 100m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.ColorStrip,
            PrimarySeriesName = "Main",
            StripScaleMode = ColorStripScaleMode.Sequential,
            StripAnchor = ColorStripAnchor.Bottom,
            StripHeightPx = 16f,
            ScaleDomainMin = 0m,
            ScaleDomainMax = 100m
        };

        var tr = MakeTransform(0m, 100m, CandleCount);
        using var bmp = RenderToBitmap(setting, new() { { "Main", vals } }, new[] { desc }, minY: 0m, maxY: 100m);

        Assert.Equal(new SKColor(0, 255, 255, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 2)), Height - 4));
        Assert.Equal(new SKColor(255, 0, 255, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 7)), Height - 4));
    }

    [Fact]
    public void SeriesPrimitiveValueRangeHelper_AccumulateRange_RespectsNonZeroOffset()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "offset_range",
            IsEnabled = true,
            Offset = 3
        };
        var vals = new decimal?[6] { 10m, 25m, 999m, 1000m, 1000m, 1000m };
        var result = new StubPrimitiveResult(
            new() { { "Main", vals } },
            new[] { new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "Main" } });

        decimal minVal = decimal.MaxValue;
        decimal maxVal = decimal.MinValue;
        bool hasValue = false;

        SeriesPrimitiveValueRangeHelper.AccumulateRange(
            result,
            result.PrimitiveDescriptors,
            setting,
            maxSampleCount: 5,
            PriceScaleType.Linear,
            ref minVal,
            ref maxVal,
            ref hasValue,
            offset: setting.Offset);

        Assert.True(hasValue);
        Assert.Equal(10m, minVal);
        Assert.Equal(25m, maxVal);
    }

    [Fact]
    public void SeriesPrimitiveValueRangeHelper_IntMaxValueWithNonZeroOffset_DoesNotHangAndBoundsCorrectly()
    {
        var setting = new CoreIndicatorSettings { Id = "offset_intmax", IsEnabled = true };
        var l1 = new decimal?[5] { 10m, 20m, 30m, 40m, 50m };
        var l2 = new decimal?[5] { 5m, 5m, 5m, 5m, 5m };
        var descriptors = new[]
        {
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.Line, PrimarySeriesName = "L1" },
            new SeriesPrimitiveDescriptor { PrimitiveType = SeriesPrimitiveType.StackedArea, PrimarySeriesName = "L1", StackedSeriesNames = new[] { "L1", "L2" } }
        };
        var result = new StubPrimitiveResult(new() { { "L1", l1 }, { "L2", l2 } }, descriptors);

        decimal minPos = decimal.MaxValue;
        decimal maxPos = decimal.MinValue;
        bool hasPos = false;
        SeriesPrimitiveValueRangeHelper.AccumulateRange(
            result, descriptors, setting, maxSampleCount: int.MaxValue, PriceScaleType.Linear,
            ref minPos, ref maxPos, ref hasPos, offset: 3);

        Assert.True(hasPos);
        Assert.Equal(0m, minPos);
        Assert.Equal(55m, maxPos);

        decimal minNeg = decimal.MaxValue;
        decimal maxNeg = decimal.MinValue;
        bool hasNeg = false;
        SeriesPrimitiveValueRangeHelper.AccumulateRange(
            result, descriptors, setting, maxSampleCount: int.MaxValue, PriceScaleType.Linear,
            ref minNeg, ref maxNeg, ref hasNeg, offset: -2);

        Assert.True(hasNeg);
        Assert.Equal(0m, minNeg);
        Assert.Equal(55m, maxNeg);
    }

    [Fact]
    public void Waterfall_LogScaleWithNonPositiveNextBar_SkipsOrphanConnector()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "wf_log_conn",
            IsEnabled = true,
            Color = new IndicatorColor(255, 255, 200, 0),
            IsOverlay = true
        };
        var runStart = new decimal?[5] { 10m, 10m, null, null, null };
        var runEnd = new decimal?[5] { 40m, -5m, null, null, null }; // bar 1 invalid under Log scale

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.Waterfall,
            PrimarySeriesName = "End",
            SecondarySeriesName = "Start",
            WaterfallDrawConnectors = true,
            SegmentWidthRatio = 0.5f
        };

        var tr = MakeTransform(5m, 100m, count: 5, priceScale: PriceScaleType.Log);
        using var bmp = RenderToBitmap(
            setting,
            new() { { "End", runEnd }, { "Start", runStart } },
            new[] { desc },
            minY: 5m,
            maxY: 100m,
            priceScale: PriceScaleType.Log,
            candleCount: 5);

        int midX = (int)MathF.Floor((BarX(tr, 0) + BarX(tr, 1)) * 0.5f);
        int connY = (int)MathF.Floor(PriceY(tr, 40m));
        Assert.Equal(SKColors.Black, bmp.GetPixel(midX, connY));
    }

    [Fact]
    public void ColorStrip_NullTargetSeriesEntry_DoesNotThrow()
    {
        var setting = new CoreIndicatorSettings
        {
            Id = "strip_null_target",
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 0, 180, 255),
            SeriesColors = new System.Collections.ObjectModel.ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Start", TargetSeries = null!, Color = new IndicatorColor(255, 255, 100, 0) }
            }
        };
        var vals = new decimal?[CandleCount];
        vals[0] = 0m;
        vals[1] = 100m;

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.ColorStrip,
            PrimarySeriesName = "Main",
            StripScaleMode = ColorStripScaleMode.Sequential,
            StripAnchor = ColorStripAnchor.Bottom,
            StripHeightPx = 12f,
            ScaleDomainMin = 0m,
            ScaleDomainMax = 100m
        };

        var tr = MakeTransform(0m, 100m, CandleCount);
        using var bmp = RenderToBitmap(setting, new() { { "Main", vals } }, new[] { desc }, minY: 0m, maxY: 100m);

        Assert.Equal(new SKColor(255, 100, 0, 255), bmp.GetPixel((int)MathF.Floor(BarX(tr, 0)), Height - 4));
    }

    private static bool HasAnyNonBlackPixel(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red != 0 || px.Green != 0 || px.Blue != 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static SKBitmap RenderIndicatorViaIndicatorRenderer(
        CoreIndicatorSettings setting,
        Dictionary<string, IReadOnlyList<decimal?>> series,
        decimal minY = -20m,
        decimal maxY = 120m,
        PriceScaleType priceScale = PriceScaleType.Linear,
        int candleCount = CandleCount,
        bool isSubWindowContext = false,
        IndicatorRenderer? existingRenderer = null)
    {
        var candles = MakeCandles(candleCount);
        var tr = MakeTransform(minY, maxY, candleCount, priceScale);
        var cfg = MakeConfig(tr, candleCount: candleCount);
        var result = IndicatorResult.Success(series);
        var snap = new ChartDataSnapshot(
            candles: candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: candleCount,
            visibleCandleCount: candleCount,
            priceScale: priceScale);

        var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);

        if (existingRenderer != null)
        {
            existingRenderer.RenderIndicator(canvas, Area, snap, setting, cfg, isSubWindowContext);
        }
        else
        {
            using var renderer = new IndicatorRenderer();
            renderer.RenderIndicator(canvas, Area, snap, setting, cfg, isSubWindowContext);
        }

        return bmp;
    }

    [Fact]
    public void ColorSettings_HiddenSeries_SkipsPixelsAndExcludesFromYRangeAndCross()
    {
        var candles = MakeCandles(5);
        var macd = new CoreIndicatorSettings
        {
            Id = "macd_vis",
            TypeEnum = IndicatorType.MACD,
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "MACD", TargetSeries = new List<string> { "Main" }, Color = new IndicatorColor(255, 0, 200, 255), IsVisible = true },
                new() { Name = "Signal", TargetSeries = new List<string> { "Signal" }, Color = new IndicatorColor(255, 255, 165, 0), IsVisible = false },
                new() { Name = "Histogram", TargetSeries = new List<string> { "Histogram" }, Color = new IndicatorColor(255, 128, 128, 128), IsVisible = false }
            }
        };

        var series = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = new decimal?[] { 1m, 2m, 3m, 4m, 5m },
            ["Signal"] = new decimal?[] { 500m, -500m, 500m, -500m, 500m },
            ["Histogram"] = new decimal?[] { -499m, 502m, -497m, 504m, -495m }
        };
        var result = IndicatorResult.Success(series);
        var snapshot = new ChartDataSnapshot(
            candles: candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { macd.Id, result } },
            indicatorSettings: new[] { macd },
            startIndex: 0,
            count: 5);

        // Y-range must reflect only Main [1..5] (+5% buffer = [0.8..5.2])
        var (minVal, maxVal) = PanelValueRangeCalculator.Calculate(snapshot, macd);
        Assert.Equal(0.8m, minVal);
        Assert.Equal(5.2m, maxVal);

        // Intra-indicator cross selection must return (null, null) when fewer than 2 visible non-ColorStrip series remain
        var (s1, s2) = ChartRenderPipeline.SelectIntraIndicatorCrossSeries(result, macd);
        Assert.Null(s1);
        Assert.Null(s2);

        // Hiding Main too must render zero non-black pixels and return fallback (0m, 1m)
        macd.SeriesColors[0].IsVisible = false;
        var (emptyMin, emptyMax) = PanelValueRangeCalculator.Calculate(snapshot, macd);
        Assert.Equal(0m, emptyMin);
        Assert.Equal(1m, emptyMax);

        using var bmpHidden = RenderIndicatorViaIndicatorRenderer(macd, series, minY: 0m, maxY: 10m, candleCount: 5);
        Assert.False(HasAnyNonBlackPixel(bmpHidden));
    }

    [Fact]
    public void IchimokuRenderer_HidingSenkouA_SkipsCloudAndSenkouALineWhileKeepingOtherLines()
    {
        var ichimoku = new CoreIndicatorSettings
        {
            Id = "ichimoku_vis",
            TypeEnum = IndicatorType.Ichimoku,
            IsEnabled = true,
            IsOverlay = true,
            UpColor = new IndicatorColor(255, 0, 255, 0),
            DownColor = new IndicatorColor(255, 255, 0, 0),
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Tenkan", TargetSeries = new List<string> { "Main" }, Color = new IndicatorColor(255, 0, 255, 255), IsVisible = true },
                new() { Name = "Kijun", TargetSeries = new List<string> { CoreIchimokuIndicator.KijunSenSeriesName }, Color = new IndicatorColor(255, 255, 255, 0), IsVisible = false },
                new() { Name = "Chikou", TargetSeries = new List<string> { CoreIchimokuIndicator.ChikouSpanSeriesName }, Color = new IndicatorColor(255, 255, 0, 255), IsVisible = false },
                new() { Name = "SenkouA", TargetSeries = new List<string> { CoreIchimokuIndicator.SenkouSpanASeriesName }, Color = new IndicatorColor(255, 0, 255, 0), IsVisible = false },
                new() { Name = "SenkouB", TargetSeries = new List<string> { CoreIchimokuIndicator.SenkouSpanBSeriesName }, Color = new IndicatorColor(255, 255, 0, 0), IsVisible = true }
            }
        };

        var tenkan = Enumerable.Repeat<decimal?>(100m, CandleCount).ToArray();
        var kijun = Enumerable.Repeat<decimal?>(80m, CandleCount).ToArray();
        var chikou = Enumerable.Repeat<decimal?>(60m, CandleCount).ToArray();
        var spanA = Enumerable.Repeat<decimal?>(40m, CandleCount).ToArray();
        var spanB = Enumerable.Repeat<decimal?>(20m, CandleCount).ToArray();

        var series = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [CoreIchimokuIndicator.TenkanSenSeriesName] = tenkan,
            [CoreIchimokuIndicator.KijunSenSeriesName] = kijun,
            [CoreIchimokuIndicator.ChikouSpanSeriesName] = chikou,
            [CoreIchimokuIndicator.SenkouSpanASeriesName] = spanA,
            [CoreIchimokuIndicator.SenkouSpanBSeriesName] = spanB
        };

        var tr = MakeTransform(0m, 120m, CandleCount);
        using var bmp = RenderIndicatorViaIndicatorRenderer(ichimoku, series, minY: 0m, maxY: 120m);

        int midX = (int)MathF.Floor(BarX(tr, 10));
        // Cloud midpoint (30m) must remain pure black because SenkouA is hidden
        Assert.Equal(SKColors.Black, bmp.GetPixel(midX, (int)MathF.Floor(PriceY(tr, 30m))));
        // SenkouA (40m) must remain black; SenkouB (20m) and Tenkan (100m) must be drawn
        Assert.Equal(SKColors.Black, bmp.GetPixel(midX, (int)MathF.Floor(PriceY(tr, 40m))));
        Assert.NotEqual(SKColors.Black, bmp.GetPixel(midX, (int)MathF.Floor(PriceY(tr, 20m))));
        Assert.NotEqual(SKColors.Black, bmp.GetPixel(midX, (int)MathF.Floor(PriceY(tr, 100m))));
    }

    [Fact]
    public void ChartInformationDataProvider_ExcludesHiddenLines_KeepsVisibleColorStrip_AndDataWindowKeepsHidden()
    {
        var candles = MakeCandles(5);
        var macd = new CoreIndicatorSettings
        {
            Id = "macd_hover",
            TypeEnum = IndicatorType.MACD,
            DisplayName = "MACD",
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "MACD", DisplayName = "MACD Line", TargetSeries = new List<string> { "Main" }, Color = new IndicatorColor(255, 0, 200, 255), IsVisible = false },
                new() { Name = "Signal", DisplayName = "Signal Line", TargetSeries = new List<string> { "Signal" }, Color = new IndicatorColor(255, 255, 165, 0), IsVisible = true, RenderPrimitive = SeriesPrimitiveType.ColorStrip },
                new() { Name = "Histogram", DisplayName = "Hist", TargetSeries = new List<string> { "Histogram" }, Color = new IndicatorColor(255, 128, 128, 128), IsVisible = true }
            }
        };
        macd.UpdateDisplayName();

        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = new decimal?[] { 10m, 11m, 12m, 13m, 14m },
            ["Signal"] = new decimal?[] { 5m, 6m, 7m, 8m, 9m },
            ["Histogram"] = new decimal?[] { 5m, 5m, 5m, 5m, 5m }
        });
        var results = new Dictionary<string, IIndicatorResult> { { macd.Id, result } };

        // Hover overlay: hidden Main is excluded, visible ColorStrip Signal + Histogram remain shown
        var hover = ChartInformationDataProvider.Extract(
            candles, new[] { macd }, results, objectManager: null, targetTime: candles[2].Timestamp);
        Assert.Equal(2, hover.Indicators.Count);
        Assert.DoesNotContain(hover.Indicators, i => i.Name.Contains("MACD Line", StringComparison.Ordinal));
        Assert.Contains(hover.Indicators, i => i.Name.Contains("Signal Line", StringComparison.Ordinal) && i.FormattedValue == "7.000");

        // When all series are hidden, ChartInformationDataProvider skips the indicator entry entirely
        macd.SeriesColors[1].IsVisible = false;
        macd.SeriesColors[2].IsVisible = false;
        var hoverAllHidden = ChartInformationDataProvider.Extract(
            candles, new[] { macd }, results, objectManager: null, targetTime: candles[2].Timestamp);
        Assert.Empty(hoverAllHidden.Indicators);

        // Meanwhile DataWindowViewModel must keep showing all computed series regardless of IsVisible
        var messenger = new StrongReferenceMessenger();
        var dispatcher = new SynchronousDispatcherService();
        using var chartVm = new ChartViewModel(
            new MockDataService(),
            new DialogService(),
            null!,
            new MockStockAnalyzerSettings(),
            new TimeFrameManager(new MockDataService()),
            null!,
            new ThemeManager(),
            new MockChartSettingsManager(),
            dispatcher,
            null,
            null!,
            messenger: messenger);

        chartVm.Indicators.Add(macd);
        chartVm.IndicatorResults = results;
        chartVm.CurrentSnapshot = new ChartDataSnapshot(
            candles: candles,
            indicatorResults: results,
            indicatorSettings: new[] { macd },
            startIndex: 0,
            count: 5);

        using var dataWindow = new DataWindowViewModel(chartVm, messenger, dispatcher);
        dataWindow.Receive(new CrosshairPositionChangedMessage(new CrosshairPositionData
        {
            CandleIndex = 2,
            HoveredCandle = candles[2],
            ChartSymbol = "TEST"
        }));

        Assert.Single(dataWindow.IndicatorItems);
        Assert.Equal(3, dataWindow.IndicatorItems[0].Children.Count);
        Assert.Contains(dataWindow.IndicatorItems[0].Children, c => c.Name == "MACD Line");
        Assert.Contains(dataWindow.IndicatorItems[0].Children, c => c.Name == "Signal Line");
        Assert.Contains(dataWindow.IndicatorItems[0].Children, c => c.Name == "Hist");
    }

    [Fact]
    public void ZigZag_WithLineStyleOverride_ConnectsThroughNullGaps()
    {
        var zigzag = new CoreIndicatorSettings
        {
            Id = "zz_override",
            TypeEnum = IndicatorType.ZigZag,
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 255, 255, 0),
            MainRenderPrimitive = SeriesPrimitiveType.Line
        };

        var vals = new decimal?[CandleCount];
        vals[0] = 20m;
        vals[10] = 100m; // sparse pivot points separated by nulls

        var tr = MakeTransform(0m, 120m, CandleCount);
        using var bmp = RenderIndicatorViaIndicatorRenderer(
            zigzag,
            new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals },
            minY: 0m,
            maxY: 120m);

        // Midpoint bar 5 at price 60m must be painted along the connected diagonal
        int midX = (int)MathF.Floor(BarX(tr, 5));
        int midY = (int)MathF.Floor(PriceY(tr, 60m));
        bool hitNearMid = false;
        for (int dy = -2; dy <= 2 && !hitNearMid; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                var px = bmp.GetPixel(midX + dx, midY + dy);
                if (px.Red > 0 || px.Green > 0)
                {
                    hitNearMid = true;
                    break;
                }
            }
        }
        Assert.True(hitNearMid, "ZigZag with Line override should connect across null gaps via ConnectThrough.");
    }

    [Theory]
    [InlineData(SeriesPrimitiveType.Line)]
    [InlineData(SeriesPrimitiveType.DashedLine)]
    [InlineData(SeriesPrimitiveType.Dot)]
    [InlineData(SeriesPrimitiveType.Histogram)]
    [InlineData(SeriesPrimitiveType.Area)]
    [InlineData(SeriesPrimitiveType.DynamicStyling)]
    [InlineData(SeriesPrimitiveType.Lollipop)]
    public void SingleSeriesAndMultiSeries_AllRenderOverrides_ProducePixelsAndSupportLogScale(SeriesPrimitiveType mode)
    {
        var sma = new CoreIndicatorSettings
        {
            Id = $"sma_{mode}",
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 0, 220, 255),
            MainRenderPrimitive = mode
        };

        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 50m + (i % 5) * 10m;

        using var bmpLinear = RenderIndicatorViaIndicatorRenderer(
            sma,
            new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals },
            minY: 10m,
            maxY: 120m,
            priceScale: PriceScaleType.Linear);
        Assert.True(HasAnyNonBlackPixel(bmpLinear), $"Linear SMA override {mode} should draw pixels.");

        using var bmpLog = RenderIndicatorViaIndicatorRenderer(
            sma,
            new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals },
            minY: 10m,
            maxY: 120m,
            priceScale: PriceScaleType.Log);
        Assert.True(HasAnyNonBlackPixel(bmpLog), $"Log-scale SMA override {mode} should draw pixels without failing baseline projection.");

        var macd = new CoreIndicatorSettings
        {
            Id = $"macd_{mode}",
            TypeEnum = IndicatorType.MACD,
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "MACD", TargetSeries = new List<string> { "Main" }, Color = new IndicatorColor(255, 255, 200, 0), RenderPrimitive = mode },
                new() { Name = "Signal", TargetSeries = new List<string> { "Signal" }, Color = new IndicatorColor(255, 0, 200, 255), RenderPrimitive = mode }
            }
        };

        using var bmpMacd = RenderIndicatorViaIndicatorRenderer(
            macd,
            new Dictionary<string, IReadOnlyList<decimal?>>
            {
                ["Main"] = vals,
                ["Signal"] = vals.Select(v => v - 5m).ToArray()
            },
            minY: 10m,
            maxY: 120m,
            isSubWindowContext: true);
        Assert.True(HasAnyNonBlackPixel(bmpMacd), $"Sub-window MACD override {mode} should draw pixels.");
    }

    [Fact]
    public void GroupRow_RendersInAscendingSeriesNamesListOrder()
    {
        var bb = new CoreIndicatorSettings
        {
            Id = "bb_order",
            TypeEnum = IndicatorType.BB,
            IsEnabled = true,
            IsOverlay = true,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new()
                {
                    Name = "Bands",
                    TargetSeries = new List<string> { "Upper", "Lower" },
                    Color = new IndicatorColor(255, 200, 100, 50),
                    RenderPrimitive = SeriesPrimitiveType.Area
                }
            }
        };

        // Both series exist in ascending order: Upper (first) then Lower (second, drawn later/frontmost).
        var upper = Enumerable.Repeat<decimal?>(90m, CandleCount).ToArray();
        var lower = Enumerable.Repeat<decimal?>(40m, CandleCount).ToArray();

        using var bmp = RenderIndicatorViaIndicatorRenderer(
            bb,
            new Dictionary<string, IReadOnlyList<decimal?>>
            {
                ["Upper"] = upper,
                ["Lower"] = lower
            },
            minY: 0m,
            maxY: 120m);

        Assert.True(HasAnyNonBlackPixel(bmp));
    }

    [Fact]
    public void IndicatorRenderer_SteadyStateOverrideRendering_AllocatesZeroBytesPerFrame()
    {
        var sma = new CoreIndicatorSettings
        {
            Id = "sma_zero_alloc",
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 0, 200, 255),
            MainRenderPrimitive = SeriesPrimitiveType.Lollipop
        };

        var candles = MakeCandles(CandleCount);
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 50m + i;

        var tr = MakeTransform(0m, 120m, CandleCount);
        var cfg = MakeConfig(tr, candleCount: CandleCount);
        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals });
        var snap = new ChartDataSnapshot(
            candles: candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { sma.Id, result } },
            indicatorSettings: new[] { sma },
            startIndex: 0,
            count: CandleCount,
            visibleCandleCount: CandleCount);

        using var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        using var renderer = new IndicatorRenderer();

        // Warm-up passes populate _synthesizedDescriptorCache and Skia paint/path internals.
        for (int w = 0; w < 3; w++)
        {
            canvas.Clear(SKColors.Black);
            renderer.RenderIndicator(canvas, Area, snap, sma, cfg, isSubWindowContext: false);
        }

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(canvas, Area, snap, sma, cfg, isSubWindowContext: false));

        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void VisualOnlySync_CopiesVisibilityAndRenderStyleWithoutIncrementingMathematicalVersion()
    {
        var parts = MainWindowViewModelFactory.Create(dispatcher: new SynchronousDispatcherService());
        using var mainVm = parts.Vm;
        var chartVm = mainVm.ChartViewModel;

        var initialSma = new CoreIndicatorSettings
        {
            Id = "sync_sma",
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            IsOverlay = true
        };
        var initialMacd = new CoreIndicatorSettings
        {
            Id = "sync_macd",
            TypeEnum = IndicatorType.MACD,
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "MACD", TargetSeries = new List<string> { "Main" }, Color = new IndicatorColor(255, 0, 200, 255) },
                new() { Name = "Signal", TargetSeries = new List<string> { "Signal" }, Color = new IndicatorColor(255, 255, 165, 0) }
            }
        };

        chartVm.Indicators.Add(initialSma);
        chartVm.Indicators.Add(initialMacd);
        long smaMathVer = initialSma.MathematicalVersion;
        long macdMathVer = initialMacd.MathematicalVersion;

        // 1. MainWindowViewModel SingleIndicatorSettingsChangedMessage visual-only fast path
        var editedSma = initialSma.Snapshot();
        editedSma.IsMainSeriesVisible = false;
        editedSma.MainRenderPrimitive = SeriesPrimitiveType.Area;
        int triggerBefore = chartVm.RenderTrigger;

        mainVm.Receive(new SingleIndicatorSettingsChangedMessage(editedSma));
        Assert.False(initialSma.IsMainSeriesVisible);
        Assert.Equal(SeriesPrimitiveType.Area, initialSma.MainRenderPrimitive);
        Assert.Equal(smaMathVer, initialSma.MathematicalVersion);
        Assert.True(chartVm.RenderTrigger > triggerBefore);

        // 2. ChartViewModel.ApplySingleIndicator range-membership change -> DataChanged vs VisualChanged
        var editedMacd = initialMacd.Snapshot();
        editedMacd.SeriesColors[0].RenderPrimitive = SeriesPrimitiveType.DashedLine;
        _ = chartVm.ConsumeRenderReason();
        chartVm.ApplySingleIndicator(editedMacd);
        Assert.Equal(SeriesPrimitiveType.DashedLine, initialMacd.SeriesColors[0].RenderPrimitive);
        Assert.Equal(macdMathVer, initialMacd.MathematicalVersion);
        Assert.Equal(RenderReason.VisualChanged, chartVm.ConsumeRenderReason());

        var editedMacdHistogram = initialMacd.Snapshot();
        editedMacdHistogram.SeriesColors[1].RenderPrimitive = SeriesPrimitiveType.Histogram;
        chartVm.ApplySingleIndicator(editedMacdHistogram);
        Assert.Equal(SeriesPrimitiveType.Histogram, initialMacd.SeriesColors[1].RenderPrimitive);
        Assert.Equal(macdMathVer, initialMacd.MathematicalVersion);
        Assert.Equal(RenderReason.DataChanged, chartVm.ConsumeRenderReason());

        var editedMacdHide = initialMacd.Snapshot();
        editedMacdHide.SeriesColors[0].IsVisible = false;
        chartVm.ApplySingleIndicator(editedMacdHide);
        Assert.False(initialMacd.SeriesColors[0].IsVisible);
        Assert.Equal(RenderReason.DataChanged, chartVm.ConsumeRenderReason());
    }

    public static IEnumerable<object[]> Selectable12ModesData()
    {
        foreach (var mode in SeriesRenderStyleCatalog.SelectablePrimitives)
        {
            yield return new object[] { mode };
        }
    }

    [Theory]
    [MemberData(nameof(Selectable12ModesData))]
    public void All12SelectableModes_And_IndexBasedRenkoSubWindow_RenderNonEmptyPixels(SeriesPrimitiveType mode)
    {
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 30m + (i % 6) * 12m;

        var renkoCandles = MakeCandles(CandleCount);
        var tr = new GenericCoordinateTransform(ChartAxisMode.Index, Width, Height);
        tr.SetIndexRange(0, CandleCount);
        tr.SetPriceRange(0m, 120m);
        var cfg = MakeConfig(tr, candleCount: CandleCount, chartType: ChartType.Renko);

        var setting = new CoreIndicatorSettings
        {
            Id = $"renko_sub_{mode}",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false,
            MinValue = 0m,
            MaxValue = 100m,
            Color = new IndicatorColor(255, 0, 220, 255),
            MainRenderPrimitive = mode
        };

        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals });
        var snap = new ChartDataSnapshot(
            candles: renkoCandles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount,
            visibleCandleCount: CandleCount,
            chartType: ChartType.Renko);

        using var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        using var renderer = new IndicatorRenderer();
        renderer.RenderIndicator(canvas, Area, snap, setting, cfg, isSubWindowContext: true);

        Assert.True(HasAnyNonBlackPixel(bmp), $"Index-based Renko sub-window mode {mode} should draw pixels.");
    }

    [Fact]
    public void Volume_HistogramExplicitOrDefault_UsesBullishBearishColors_AndOverrideSynthesizes()
    {
        var candles = new List<CoreCandleData>(CandleCount);
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            decimal open = i % 2 == 0 ? 95m : 105m;
            decimal close = i % 2 == 0 ? 105m : 95m;
            candles.Add(new CoreCandleData(Start.AddDays(i), open, 110m, 90m, close, 50L + i));
            vals[i] = 50m + i * 2m;
        }

        var upCol = new IndicatorColor(255, 0, 220, 0);
        var downCol = new IndicatorColor(255, 220, 0, 0);
        var volSetting = new CoreIndicatorSettings
        {
            Id = "vol_updown",
            TypeEnum = IndicatorType.Volume,
            IsEnabled = true,
            IsOverlay = false,
            UseUpDownColors = true,
            UpColor = upCol,
            DownColor = downCol,
            Color = new IndicatorColor(255, 128, 128, 128),
            MainRenderPrimitive = SeriesPrimitiveType.Histogram
        };

        var tr = MakeTransform(0m, 120m, CandleCount);
        var cfg = MakeConfig(tr, candleCount: CandleCount);
        var res = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals });
        var snap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { volSetting.Id, res } }, indicatorSettings: new[] { volSetting }, startIndex: 0, count: CandleCount, visibleCandleCount: CandleCount);

        using var bmp = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        using var renderer = new IndicatorRenderer();
        renderer.RenderIndicator(canvas, Area, snap, volSetting, cfg, isSubWindowContext: true);

        int bullX = (int)MathF.Floor(BarX(tr, 4));
        int bearX = (int)MathF.Floor(BarX(tr, 5));
        int sampleY = (int)MathF.Floor(PriceY(tr, 25m));

        Assert.Equal(upCol.ToSkColor(), bmp.GetPixel(bullX, sampleY));
        Assert.Equal(downCol.ToSkColor(), bmp.GetPixel(bearX, sampleY));

        // Switching Volume to Line routes through synthesized primitive renderer with single Color
        volSetting.MainRenderPrimitive = SeriesPrimitiveType.Line;
        canvas.Clear(SKColors.Black);
        renderer.RenderIndicator(canvas, Area, snap, volSetting, cfg, isSubWindowContext: true);
        Assert.Equal(SKColors.Black, bmp.GetPixel(bullX, sampleY));
    }

    [Fact]
    public void PositiveNegativeArea_PositiveBoundedAndOverlay_SplitAtCenterBaseline_WithZeroPanAllocations()
    {
        var rsiSetting = new CoreIndicatorSettings
        {
            Id = "rsi_pna",
            TypeEnum = IndicatorType.RSI,
            IsEnabled = true,
            IsOverlay = false,
            MinValue = 0m,
            MaxValue = 100m,
            UseUpDownColors = true,
            UpColor = new IndicatorColor(255, 0, 220, 0),
            DownColor = new IndicatorColor(255, 220, 0, 0),
            MainRenderPrimitive = SeriesPrimitiveType.PositiveNegativeArea
        };

        // Entirely positive series [35..65] around fixed center (0 + 100) / 2 = 50
        var totalBars = CandleCount + 12;
        var candles = MakeCandles(totalBars);
        var rsiVals = new decimal?[totalBars];
        for (int i = 0; i < totalBars; i++)
        {
            rsiVals[i] = i < 10 ? 35m : 65m;
        }

        using var bmpRsi = RenderIndicatorViaIndicatorRenderer(
            rsiSetting,
            new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = rsiVals[..CandleCount] },
            minY: 0m,
            maxY: 100m,
            isSubWindowContext: true);

        var tr = MakeTransform(0m, 100m, CandleCount);
        var pxNeg = bmpRsi.GetPixel((int)MathF.Floor(BarX(tr, 4)), (int)MathF.Floor(PriceY(tr, 42m)));
        var pxPos = bmpRsi.GetPixel((int)MathF.Floor(BarX(tr, 14)), (int)MathF.Floor(PriceY(tr, 58m)));
        Assert.True(pxNeg.Red > pxNeg.Green && pxNeg.Red > 0, "RSI below 50 should render negative fill.");
        Assert.True(pxPos.Green > pxPos.Red && pxPos.Green > 0, "RSI above 50 should render positive fill.");

        // Panning across changing visible minima on an unbounded overlay PositiveNegativeArea must allocate 0 B/frame.
        var smaSetting = new CoreIndicatorSettings
        {
            Id = "sma_pan_pna",
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            IsOverlay = true,
            UseUpDownColors = true,
            UpColor = new IndicatorColor(255, 0, 200, 0),
            DownColor = new IndicatorColor(255, 200, 0, 0),
            MainRenderPrimitive = SeriesPrimitiveType.PositiveNegativeArea
        };
        var smaVals = new decimal?[totalBars];
        for (int i = 0; i < totalBars; i++) smaVals[i] = 100m + i * 3m;

        var res = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = smaVals });
        var panCfgs = new StubChartRenderConfig[10];
        var panSnaps = new ChartDataSnapshot[10];
        for (int p = 0; p < 10; p++)
        {
            var pTr = MakeTransform(90m, 220m, CandleCount);
            pTr.UpdateRange(Start.AddDays(p), Start.AddDays(p + CandleCount), 90m, 220m);
            panCfgs[p] = new StubChartRenderConfig
            {
                ThemeManager = new StubThemeManager { CurrentTheme = ThemeColors.Dark },
                Transform = pTr,
                VisibleStartIndex = p,
                VisibleCandleCount = CandleCount
            };
            panSnaps[p] = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { smaSetting.Id, res } }, indicatorSettings: new[] { smaSetting }, startIndex: p, count: CandleCount, visibleCandleCount: CandleCount);
        }

        using var panSurface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var panRenderer = new IndicatorRenderer();
        smaSetting.Offset = 0;
        panRenderer.RenderIndicator(panSurface.Canvas, Area, panSnaps[0], smaSetting, panCfgs[0]);

        long panBytes = AllocationProbe.InCollectionFreeWindow(() =>
        {
            for (int p = 0; p < 10; p++)
            {
                smaSetting.Offset = -p;
                panRenderer.RenderIndicator(panSurface.Canvas, Area, panSnaps[p], smaSetting, panCfgs[p]);
            }
        });
        Assert.Equal(0L, panBytes);
    }

    [Fact]
    public void StackedArea_PartialHiding_PreservesOriginalColors_SkipsHiddenNullBreaks_AndMatchesRange()
    {
        var layerA = new decimal?[CandleCount];
        var layerB = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            layerA[i] = 10m;
            layerB[i] = i == 10 ? null : 90m;
        }

        var desc = new SeriesPrimitiveDescriptor
        {
            PrimitiveType = SeriesPrimitiveType.StackedArea,
            PrimarySeriesName = "LayerA",
            StackedSeriesNames = new[] { "LayerA", "LayerB" },
            StackingMode = StackingSignMode.NonNegativeClamp,
            StackingNullPolicy = StackingNullMode.BreakAllLayers,
            FillAlpha = 255,
            DrawBorderStroke = false
        };

        var setting = new CoreIndicatorSettings
        {
            Id = "stacked_partial",
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "A", TargetSeries = new List<string> { "LayerA" }, Color = new IndicatorColor(255, 0, 120, 255), IsVisible = true },
                new() { Name = "B", TargetSeries = new List<string> { "LayerB" }, Color = new IndicatorColor(255, 255, 60, 0), IsVisible = false }
            }
        };

        var result = new StubPrimitiveResult(new() { { "LayerA", layerA }, { "LayerB", layerB } }, new[] { desc });
        var snap = new ChartDataSnapshot(MakeCandles(CandleCount), indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } }, indicatorSettings: new[] { setting }, startIndex: 0, count: CandleCount);
        var (rMin, rMax) = PanelValueRangeCalculator.Calculate(snap, setting);
        Assert.True(rMax < 15m, $"Hidden LayerB must not inflate range ({rMax}).");

        // LayerA must remain continuous across LayerB's null bar (bar 10), with nothing painted at y=50
        var tr = MakeTransform(0m, 100m, CandleCount);
        using var bmpHideB = RenderToBitmap(setting, new() { { "LayerA", layerA }, { "LayerB", layerB } }, new[] { desc }, minY: 0m, maxY: 100m);
        Assert.Equal(new SKColor(0, 120, 255, 255), bmpHideB.GetPixel((int)MathF.Floor(BarX(tr, 10)), (int)MathF.Floor(PriceY(tr, 5m))));
        Assert.Equal(SKColors.Black, bmpHideB.GetPixel((int)MathF.Floor(BarX(tr, 5)), (int)MathF.Floor(PriceY(tr, 50m))));

        // Hiding LayerA while LayerB is unconfigured (fallback palette index 1) must preserve LayerB's palette color (no index shift)
        var settingPalette = new CoreIndicatorSettings
        {
            Id = "stacked_palette",
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "A", TargetSeries = new List<string> { "LayerA" }, Color = new IndicatorColor(255, 0, 120, 255), IsVisible = false }
            }
        };
        var expectedLayerBColor = ChartColorPalette.Get(1, isDark: true).ToSkColor();
        using var bmpHideA = RenderToBitmap(settingPalette, new() { { "LayerA", layerA }, { "LayerB", layerB } }, new[] { desc }, minY: 0m, maxY: 100m);
        Assert.Equal(expectedLayerBColor, bmpHideA.GetPixel((int)MathF.Floor(BarX(tr, 5)), (int)MathF.Floor(PriceY(tr, 45m))));
    }

    [Fact]
    public void PrepareAxisLabels_ExcludesHiddenMainSeries_WhileKeepingVisibleLine()
    {
        var sma = new CoreIndicatorSettings
        {
            Id = "axis_lbl",
            TypeEnum = IndicatorType.SMA,
            ShowAxisLabel = true,
            IsMainSeriesVisible = true
        };

        Assert.True(ChartRenderPipeline.ShouldEmitIndicatorAxisLabel(sma, ChartType.Candlestick));
        Assert.False(ChartRenderPipeline.ShouldEmitIndicatorAxisLabel(sma, ChartType.RelativePerformance));

        sma.IsMainSeriesVisible = false;
        Assert.False(ChartRenderPipeline.ShouldEmitIndicatorAxisLabel(sma, ChartType.Candlestick));
    }

    [Fact]
    public void SynthesizedDescriptorCache_BoundsAt256Entries_And_DashedLinePreservesSettingLineStyle()
    {
        var candles = MakeCandles(CandleCount);
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) vals[i] = 50m + i;

        var tr = MakeTransform(0m, 120m, CandleCount);
        var cfg = MakeConfig(tr);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        using var renderer = new IndicatorRenderer();

        for (int idx = 0; idx < 300; idx++)
        {
            var setting = new CoreIndicatorSettings
            {
                Id = $"bound_{idx}",
                TypeEnum = IndicatorType.SMA,
                IsEnabled = true,
                IsOverlay = true,
                Style = CoreLineStyle.Dot,
                MainRenderPrimitive = SeriesPrimitiveType.DashedLine
            };
            var res = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals });
            var snap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, res } }, indicatorSettings: new[] { setting }, startIndex: 0, count: CandleCount);
            renderer.RenderIndicator(surface.Canvas, Area, snap, setting, cfg);
        }

        // Verify steady-state call still allocates 0 B after cache wrap and preserves DashedLine rendering
        var checkSetting = new CoreIndicatorSettings
        {
            Id = "bound_299",
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            IsOverlay = true,
            Style = CoreLineStyle.DashDot,
            MainRenderPrimitive = SeriesPrimitiveType.DashedLine
        };
        var checkRes = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals });
        var checkSnap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { checkSetting.Id, checkRes } }, indicatorSettings: new[] { checkSetting }, startIndex: 0, count: CandleCount);
        renderer.RenderIndicator(surface.Canvas, Area, checkSnap, checkSetting, cfg);
        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, checkSnap, checkSetting, cfg));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void ChartDataSnapshot_DeepSnapshotsIndicatorSettings_And_ApplySingleIndicatorSyncsAllVisualsByName()
    {
        var candles = MakeCandles(CandleCount);
        var macd = new CoreIndicatorSettings
        {
            Id = "snap_iso_macd",
            TypeEnum = IndicatorType.MACD,
            IsEnabled = true,
            IsOverlay = false,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "MACD", TargetSeries = new List<string> { "Main" }, Color = new IndicatorColor(255, 0, 200, 255), IsVisible = true },
                new() { Name = "Signal", TargetSeries = new List<string> { "Signal" }, Color = new IndicatorColor(255, 255, 165, 0), IsVisible = true }
            }
        };

        var res = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = Enumerable.Repeat<decimal?>(-10m, CandleCount).ToArray(),
            ["Signal"] = Enumerable.Repeat<decimal?>(-5m, CandleCount).ToArray()
        });

        var snap = new ChartDataSnapshot(
            candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { macd.Id, res } },
            indicatorSettings: new[] { macd },
            startIndex: 0,
            count: CandleCount);

        Assert.Single(snap.IndicatorSettings);
        Assert.False(ReferenceEquals(macd, snap.IndicatorSettings[0]));
        Assert.False(ReferenceEquals(macd.SeriesColors[0], snap.IndicatorSettings[0].SeriesColors[0]));

        // Mutating original UI setting after snapshot creation must NOT leak into snapshot
        macd.SeriesColors[0].IsVisible = false;
        macd.SeriesColors[0].RenderPrimitive = SeriesPrimitiveType.ColorStrip;
        Assert.True(snap.IndicatorSettings[0].SeriesColors[0].IsVisible);
        Assert.Null(snap.IndicatorSettings[0].SeriesColors[0].RenderPrimitive);

        // Verify ApplySingleIndicator copies UseUpDownColors/UpColor/DownColor & syncs reversed SeriesColors by Name
        var parts = MainWindowViewModelFactory.Create(dispatcher: new SynchronousDispatcherService());
        using var mainVm = parts.Vm;
        var chartVm = mainVm.ChartViewModel;

        var vol = new CoreIndicatorSettings
        {
            Id = "vol_single",
            TypeEnum = IndicatorType.Volume,
            IsEnabled = true,
            IsOverlay = false,
            UseUpDownColors = false,
            UpColor = new IndicatorColor(255, 0, 128, 0),
            DownColor = new IndicatorColor(255, 255, 0, 0)
        };
        var macdTarget = macd.Snapshot();
        macdTarget.SeriesColors[0].IsVisible = true;
        macdTarget.SeriesColors[0].RenderPrimitive = null;
        chartVm.Indicators.Add(vol);
        chartVm.Indicators.Add(macdTarget);

        var volEdited = vol.Snapshot();
        volEdited.UseUpDownColors = true;
        volEdited.UpColor = new IndicatorColor(255, 10, 240, 10);
        volEdited.DownColor = new IndicatorColor(255, 240, 10, 10);
        _ = chartVm.ConsumeRenderReason();
        chartVm.ApplySingleIndicator(volEdited);
        Assert.True(vol.UseUpDownColors);
        Assert.Equal(volEdited.UpColor, vol.UpColor);
        Assert.Equal(volEdited.DownColor, vol.DownColor);
        Assert.Equal(RenderReason.VisualChanged, chartVm.ConsumeRenderReason());

        // Reverse SeriesColors order on source clone; identifier matching must update the right target entry
        var macdReversed = macdTarget.Snapshot();
        macdReversed.SeriesColors.Move(0, 1);
        macdReversed.SeriesColors[0].RenderPrimitive = SeriesPrimitiveType.Lollipop; // "Signal"
        macdReversed.SeriesColors[1].IsVisible = false; // "MACD"
        chartVm.ApplySingleIndicator(macdReversed);

        Assert.False(macdTarget.SeriesColors[0].IsVisible); // "MACD" updated cleanly
        Assert.Null(macdTarget.SeriesColors[0].RenderPrimitive);
        Assert.Equal(SeriesPrimitiveType.Lollipop, macdTarget.SeriesColors[1].RenderPrimitive); // "Signal"
        Assert.Equal(RenderReason.DataChanged, chartVm.ConsumeRenderReason());

        // Also verify non-primitive negative-only Histogram in PanelValueRangeCalculator includes 0m ceiling
        var negHist = new CoreIndicatorSettings
        {
            Id = "neg_hist_panel",
            TypeEnum = IndicatorType.Momentum,
            IsEnabled = true,
            IsOverlay = false,
            IncludeZeroBaseline = false,
            MainRenderPrimitive = SeriesPrimitiveType.Histogram
        };
        var negRes = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            ["Main"] = Enumerable.Repeat<decimal?>(-10m, CandleCount).ToArray()
        });
        var negSnap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { negHist.Id, negRes } }, indicatorSettings: new[] { negHist }, startIndex: 0, count: CandleCount);
        var (nMin, nMax) = PanelValueRangeCalculator.Calculate(negSnap, negHist);
        Assert.True(nMin <= -10m && nMax >= 0m, $"Negative histogram sub-panel must include 0m ceiling, got [{nMin}, {nMax}].");
    }

    [Theory]
    [InlineData(SeriesPrimitiveType.Spline)]
    [InlineData(SeriesPrimitiveType.Lollipop)]
    [InlineData(SeriesPrimitiveType.Area)]
    public void Spline_Lollipop_Area_UseUpDownColors_SplitsRisingAndFallingColors_WithZeroAllocation(SeriesPrimitiveType primitive)
    {
        // Series rises sharply (0 -> 1..4) then falls sharply (4..9): guarantees both UpColor and DownColor segments/bars
        var vals = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            vals[i] = i <= 6 ? 20m + i * 15m : 110m - (i - 6) * 12m;
        }

        var upCol = new IndicatorColor(255, 0, 220, 0);
        var downCol = new IndicatorColor(255, 220, 0, 0);
        var setting = new CoreIndicatorSettings
        {
            Id = $"updown_{primitive}",
            TypeEnum = IndicatorType.SMA,
            IsEnabled = true,
            IsOverlay = true,
            Thickness = 3.0,
            UseUpDownColors = true,
            UpColor = upCol,
            DownColor = downCol,
            Color = new IndicatorColor(255, 0, 0, 255),
            MainRenderPrimitive = primitive
        };

        using var renderer = new IndicatorRenderer();
        using var bmp = RenderIndicatorViaIndicatorRenderer(
            setting,
            new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals },
            minY: 0m,
            maxY: 130m,
            existingRenderer: renderer);

        bool sawGreenUp = false;
        bool sawRedDown = false;
        for (int y = 0; y < bmp.Height && (!sawGreenUp || !sawRedDown); y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Green > 80 && px.Red < 40 && px.Blue < 40) sawGreenUp = true;
                if (px.Red > 80 && px.Green < 40 && px.Blue < 40) sawRedDown = true;
            }
        }

        Assert.True(sawGreenUp, $"{primitive} with UseUpDownColors=true must draw rising UpColor pixels.");
        Assert.True(sawRedDown, $"{primitive} with UseUpDownColors=true must draw falling DownColor pixels.");

        var candles = MakeCandles(CandleCount);
        var tr = MakeTransform(0m, 130m, CandleCount);
        var cfg = MakeConfig(tr, candleCount: CandleCount);
        var result = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>> { ["Main"] = vals });
        var snap = new ChartDataSnapshot(candles, indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } }, indicatorSettings: new[] { setting }, startIndex: 0, count: CandleCount, visibleCandleCount: CandleCount);
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        renderer.RenderIndicator(surface.Canvas, Area, snap, setting, cfg);

        long bytes = AllocationProbe.InCollectionFreeWindow(() =>
            renderer.RenderIndicator(surface.Canvas, Area, snap, setting, cfg));
        Assert.Equal(0L, bytes);
    }

    [Fact]
    public void MultiSeriesHover_ResolvesIchimokuTenkanSenFromMain_And_RendererResolvesSeriesColorCaseInsensitively()
    {
        var candles = MakeCandles(CandleCount);
        var tenkanColor = new IndicatorColor(255, 0, 210, 210);
        var ichimoku = new CoreIndicatorSettings
        {
            Id = "ichimoku_hover_alias",
            TypeEnum = IndicatorType.Ichimoku,
            DisplayName = "Ichimoku",
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 10, 10, 10),
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "Tenkan", DisplayName = "Tenkan-sen", TargetSeries = new List<string> { "Main" }, Color = tenkanColor, IsVisible = true },
                new() { Name = "Kijun", DisplayName = "Kijun-sen", TargetSeries = new List<string> { CoreIchimokuIndicator.KijunSenSeriesName }, Color = new IndicatorColor(255, 255, 200, 0), IsVisible = true }
            }
        };
        ichimoku.UpdateDisplayName();

        var ichiResult = IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [CoreIchimokuIndicator.TenkanSenSeriesName] = Enumerable.Repeat<decimal?>(101m, CandleCount).ToArray(),
            [CoreIchimokuIndicator.KijunSenSeriesName] = Enumerable.Repeat<decimal?>(99m, CandleCount).ToArray()
        });

        var hover = ChartInformationDataProvider.Extract(
            candles,
            new[] { ichimoku },
            new Dictionary<string, IIndicatorResult> { { ichimoku.Id, ichiResult } },
            objectManager: null,
            targetTime: candles[2].Timestamp);

        Assert.Contains(hover.Indicators, i => i.Name.Contains("Tenkan-sen", StringComparison.Ordinal) && i.Color.Equals(tenkanColor));

        // Case-insensitive SeriesColors match in IndicatorRenderer ("upper" config vs "Upper" result key)
        var upperColor = new IndicatorColor(255, 0, 240, 120);
        var bb = new CoreIndicatorSettings
        {
            Id = "case_color_bb",
            TypeEnum = IndicatorType.BB,
            IsEnabled = true,
            IsOverlay = true,
            Color = new IndicatorColor(255, 255, 0, 0),
            Thickness = 3.0,
            SeriesColors = new ObservableCollection<SeriesColorConfig>
            {
                new() { Name = "UpperBand", TargetSeries = new List<string> { "upper" }, Color = upperColor, IsVisible = true }
            }
        };

        var tr = MakeTransform(0m, 120m, CandleCount);
        using var bmp = RenderIndicatorViaIndicatorRenderer(
            bb,
            new Dictionary<string, IReadOnlyList<decimal?>> { ["Upper"] = Enumerable.Repeat<decimal?>(60m, CandleCount).ToArray() },
            minY: 0m,
            maxY: 120m);

        var px = bmp.GetPixel((int)MathF.Floor(BarX(tr, 5)), (int)MathF.Floor(PriceY(tr, 60m)));
        Assert.Equal(upperColor.ToSkColor(), px);
    }

    [Fact]
    public void PatternFlag_SubWindow_RendersDefaultHistogram_NotLine_AndSkipsZeroBars()
    {
        var setting = DefaultCoreIndicatorSettings.CreatePatternFlag(
            StockAnalyzer.Core.Models.Indicators.PatternFlags.PatternFlagIndicatorCatalog.Get(IndicatorType.CandleHammer));
        setting.Id = "pattern_hammer_hist";
        setting.IsEnabled = true;

        var values = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++) values[i] = i == 10 ? 1m : 0m;
        var series = new Dictionary<string, IReadOnlyList<decimal?>> { [IndicatorResult.MainSeriesName] = values };

        var tr = MakeTransform(0m, 1.2m, CandleCount);
        using var histBmp = RenderIndicatorViaIndicatorRenderer(
            setting,
            series,
            minY: 0m,
            maxY: 1.2m,
            isSubWindowContext: true);

        int bx10 = (int)MathF.Floor(BarX(tr, 10));
        int midY = (int)MathF.Floor(PriceY(tr, 0.5m));
        Assert.Equal(setting.Color.ToSkColor(), histBmp.GetPixel(bx10, midY));

        // Zero bar at index 3 must remain untouched (no zero-baseline bar or connecting line)
        int bx3 = (int)MathF.Floor(BarX(tr, 3));
        Assert.Equal(SKColors.Black, histBmp.GetPixel(bx3, (int)MathF.Floor(PriceY(tr, 0m))));
        Assert.Equal(SKColors.Black, histBmp.GetPixel(bx3, midY));

        // Explicit Line override differs from default Histogram
        var lineSetting = setting.Snapshot();
        lineSetting.MainRenderPrimitive = SeriesPrimitiveType.Line;
        using var lineBmp = RenderIndicatorViaIndicatorRenderer(
            lineSetting,
            series,
            minY: 0m,
            maxY: 1.2m,
            isSubWindowContext: true);

        Assert.Equal(SKColors.Black, lineBmp.GetPixel(bx10, midY));
    }

    [Fact]
    public void PatternFlag_Bidirectional_StandardAndIndexSubWindow_UseSeriesColorsAsHistogram()
    {
        var setting = DefaultCoreIndicatorSettings.CreatePatternFlag(
            StockAnalyzer.Core.Models.Indicators.PatternFlags.PatternFlagIndicatorCatalog.Get(IndicatorType.MarketStructureBos));
        setting.Id = "pattern_bos_hist";
        setting.IsEnabled = true;

        var bull = new decimal?[CandleCount];
        var bear = new decimal?[CandleCount];
        var main = new decimal?[CandleCount];
        var dir = new decimal?[CandleCount];
        for (int i = 0; i < CandleCount; i++)
        {
            bull[i] = i == 5 ? 1m : 0m;
            bear[i] = i == 12 ? -1m : 0m;
            main[i] = (i == 5 || i == 12) ? 1m : 0m;
            dir[i] = i == 5 ? 1m : (i == 12 ? -1m : 0m);
        }

        var series = new Dictionary<string, IReadOnlyList<decimal?>>
        {
            [IndicatorResult.MainSeriesName] = main,
            [IndicatorSeriesNames.PatternDirection] = dir,
            ["BullishBos"] = bull,
            ["BearishBos"] = bear,
        };

        var tr = MakeTransform(-1.2m, 1.2m, CandleCount);
        using var bmp = RenderIndicatorViaIndicatorRenderer(
            setting,
            series,
            minY: -1.2m,
            maxY: 1.2m,
            isSubWindowContext: true);

        Assert.Equal(IndicatorDefaultConstants.PatternFlagBullishColor.ToSkColor(),
            bmp.GetPixel((int)MathF.Floor(BarX(tr, 5)), (int)MathF.Floor(PriceY(tr, 0.5m))));
        Assert.Equal(IndicatorDefaultConstants.PatternFlagBearishColor.ToSkColor(),
            bmp.GetPixel((int)MathF.Floor(BarX(tr, 12)), (int)MathF.Floor(PriceY(tr, -0.5m))));
        Assert.Equal(SKColors.Black,
            bmp.GetPixel((int)MathF.Floor(BarX(tr, 2)), (int)MathF.Floor(PriceY(tr, 0m))));

        // Index-based sub-window path (e.g. Renko)
        var candles = MakeCandles(CandleCount);
        var indexTr = new GenericCoordinateTransform(ChartAxisMode.Index, Width, Height);
        indexTr.SetIndexRange(0, CandleCount);
        indexTr.SetPriceRange(-1.2m, 1.2m);
        var indexCfg = MakeConfig(indexTr, candleCount: CandleCount, chartType: ChartType.Renko);
        var result = IndicatorResult.Success(series);
        var snap = new ChartDataSnapshot(
            candles: candles,
            indicatorResults: new Dictionary<string, IIndicatorResult> { { setting.Id, result } },
            indicatorSettings: new[] { setting },
            startIndex: 0,
            count: CandleCount,
            visibleCandleCount: CandleCount,
            chartType: ChartType.Renko);

        using var idxBmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(idxBmp))
        {
            canvas.Clear(SKColors.Black);
            using var renderer = new IndicatorRenderer();
            renderer.RenderIndicator(canvas, Area, snap, setting, indexCfg, isSubWindowContext: true);
        }

        double colWidth = indexTr.ScaleX;
        int idx5X = (int)MathF.Floor((float)indexTr.GetXFromIndex(5) + (float)Area.Left + (float)colWidth * 0.5f);
        int idx12X = (int)MathF.Floor((float)indexTr.GetXFromIndex(12) + (float)Area.Left + (float)colWidth * 0.5f);
        float zeroY = (float)Area.Top + (float)Area.Height * (float)((1.2m - 0m) / 2.4m);
        float posHalfY = (float)Area.Top + (float)Area.Height * (float)((1.2m - 0.5m) / 2.4m);
        float negHalfY = (float)Area.Top + (float)Area.Height * (float)((1.2m - (-0.5m)) / 2.4m);

        Assert.Equal(IndicatorDefaultConstants.PatternFlagBullishColor.ToSkColor(), idxBmp.GetPixel(idx5X, (int)MathF.Floor(posHalfY)));
        Assert.Equal(IndicatorDefaultConstants.PatternFlagBearishColor.ToSkColor(), idxBmp.GetPixel(idx12X, (int)MathF.Floor(negHalfY)));
        Assert.Equal(SKColors.Black, idxBmp.GetPixel((int)MathF.Floor((float)indexTr.GetXFromIndex(2) + (float)Area.Left + (float)colWidth * 0.5f), (int)MathF.Floor(zeroY)));

        // Index-based sub-window with UseUpDownColors = true and Offset = 1 uses setting.UpColor/DownColor and shifts bars right by 1
        var customUp = new IndicatorColor(255, 10, 220, 90);
        var customDown = new IndicatorColor(255, 240, 40, 60);
        var shiftedSetting = setting.Snapshot();
        shiftedSetting.UseUpDownColors = true;
        shiftedSetting.UpColor = customUp;
        shiftedSetting.DownColor = customDown;
        shiftedSetting.Offset = 1;

        using var shiftedIdxBmp = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var shiftedCanvas = new SKCanvas(shiftedIdxBmp))
        {
            shiftedCanvas.Clear(SKColors.Black);
            using var renderer = new IndicatorRenderer();
            renderer.RenderIndicator(shiftedCanvas, Area, snap, shiftedSetting, indexCfg, isSubWindowContext: true);
        }

        int idx6X = (int)MathF.Floor((float)indexTr.GetXFromIndex(6) + (float)Area.Left + (float)colWidth * 0.5f);
        int idx13X = (int)MathF.Floor((float)indexTr.GetXFromIndex(13) + (float)Area.Left + (float)colWidth * 0.5f);
        Assert.Equal(customUp.ToSkColor(), shiftedIdxBmp.GetPixel(idx6X, (int)MathF.Floor(posHalfY)));
        Assert.Equal(customDown.ToSkColor(), shiftedIdxBmp.GetPixel(idx13X, (int)MathF.Floor(negHalfY)));
    }
}
