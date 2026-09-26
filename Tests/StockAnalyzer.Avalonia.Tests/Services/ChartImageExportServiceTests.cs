using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.Services.Export;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

public class ChartImageExportServiceTests
{
    private static (ChartDataSnapshot, ChartLayoutContext, ICoordinateTransform, IChartRenderConfig) CreateTestContext()
    {
        var candles = new List<CoreCandleData>();
        var baseDate = new DateTime(2026, 1, 1);
        for (int i = 0; i < 20; i++)
        {
            candles.Add(new CoreCandleData(
                baseDate.AddDays(i),
                100m + i,
                110m + i,
                90m + i,
                105m + i,
                1000 + i * 100));
        }

        var snapshot = new ChartDataSnapshot(
            candles: candles,
            startIndex: 0,
            count: 20);

        var bounds = new Rect(0, 0, 800, 600);
        var layout = ChartLayoutService.CreateLayout(bounds, ChartType.Candlestick);

        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        var themeManager = new ThemeManager();

        var config = new CandlestickRenderConfig(
            ThemeManager: themeManager,
            ChartType: ChartType.Candlestick,
            CurrentPrice: 105.0,
            BullishColor: IndicatorColor.FromRgb(0, 200, 0),
            BearishColor: IndicatorColor.FromRgb(200, 0, 0),
            NeutralColor: IndicatorColor.FromRgb(128, 128, 128),
            ReversalLabelColor: new NamedColor("Green", "#00FF00"),
            PriceLabelColor: new NamedColor("White", "#FFFFFF"),
            VisibleStartIndex: 0,
            VisibleCandleCount: 20,
            Transform: transform,
            MousePosition: new StockAnalyzer.Core.Models.Point(0, 0),
            ShowMultiWavePatterns: false,
            ShowGhostProjections: false,
            GhostProjectionFontSize: 12f,
            ShowGhostLabelsOnHoverOnly: false,
            RenderScaling: 1.0,
            IsSubWindowVisible: false,
            InvertOscillator: false,
            DefaultDrawingThickness: 1.0,
            CrosshairLabelVisible: false,
            MultiWavePatternMaxLines: 0,
            MultiWaveBullishColor: null,
            MultiWaveBearishColor: null);

        return (snapshot, layout, transform, config);
    }

    [Fact]
    public async Task RenderChartImageAsync_ReturnsValidPng_WithEmbeddedMetadata()
    {
        // Arrange
        var (snapshot, layout, transform, config) = CreateTestContext();
        var sut = new ChartImageExportService();
        var objectManager = new ChartObjectManager();
        var renderer = new CandleStickRenderer();
        var rulerRenderer = new RulerRenderer();
        var themeManager = new ThemeManager();

        var options = new ChartImageExportOptions
        {
            Format = ImageExportFormat.Png,
            Scale = 1.0f,
            IncludeVisualHeader = true,
            EmbedFileMetadata = true,
            IncludeCrosshair = false
        };

        var metadata = new ChartImageMetadata
        {
            Symbol = "7203",
            CompanyName = "トヨタ自動車",
            Timeframe = "Daily",
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 1, 20),
            IndicatorsSummary = "SMA(20)",
            GeneratedAt = new DateTime(2026, 8, 20)
        };

        // Act
        var result = await sut.RenderChartImageAsync(
            snapshot, layout, transform, options, themeManager,
            objectManager, renderer, config, ChartType.Candlestick, rulerRenderer, metadata);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Length > 100);

        // Verify PNG signature: 89 50 4E 47
        Assert.Equal(0x89, result[0]);
        Assert.Equal(0x50, result[1]);
        Assert.Equal(0x4E, result[2]);
        Assert.Equal(0x47, result[3]);

        // Verify metadata was injected
        var text = Encoding.UTF8.GetString(result);
        Assert.Contains("7203", text);
        Assert.Contains("トヨタ自動車", text);
        Assert.Contains("SMA(20)", text);
    }

    [Fact]
    public async Task RenderChartImageAsync_WithDarkAndLightThemes_Succeeds()
    {
        // Arrange
        var (snapshot, layout, transform, config) = CreateTestContext();
        var sut = new ChartImageExportService();
        var objectManager = new ChartObjectManager();
        var renderer = new CandleStickRenderer();
        var rulerRenderer = new RulerRenderer();
        var themeManager = new ThemeManager();
        var metadata = new ChartImageMetadata { Symbol = "AAPL" };

        var darkOptions = new ChartImageExportOptions
        {
            ThemeMode = ChartImageExportThemeMode.Dark,
            Format = ImageExportFormat.Png
        };
        var lightOptions = new ChartImageExportOptions
        {
            ThemeMode = ChartImageExportThemeMode.Light,
            Format = ImageExportFormat.Png
        };

        // Act
        var darkResult = await sut.RenderChartImageAsync(
            snapshot, layout, transform, darkOptions, themeManager,
            objectManager, renderer, config, ChartType.Candlestick, rulerRenderer, metadata);

        var lightResult = await sut.RenderChartImageAsync(
            snapshot, layout, transform, lightOptions, themeManager,
            objectManager, renderer, config, ChartType.Candlestick, rulerRenderer, metadata);

        // Assert
        Assert.NotNull(darkResult);
        Assert.NotNull(lightResult);
        Assert.True(darkResult.Length > 0);
        Assert.True(lightResult.Length > 0);
    }

    [Fact]
    public async Task RenderChartPreviewAsync_ReturnsThumbnailBytes()
    {
        // Arrange
        var (snapshot, layout, transform, config) = CreateTestContext();
        var sut = new ChartImageExportService();
        var objectManager = new ChartObjectManager();
        var renderer = new CandleStickRenderer();
        var rulerRenderer = new RulerRenderer();
        var themeManager = new ThemeManager();
        var metadata = new ChartImageMetadata { Symbol = "AAPL" };
        var options = new ChartImageExportOptions();

        // Act
        var preview = await sut.RenderChartPreviewAsync(
            snapshot, layout, transform, options, themeManager,
            objectManager, renderer, config, ChartType.Candlestick, rulerRenderer, metadata, previewMaxWidth: 300);

        // Assert
        Assert.NotNull(preview);
        Assert.True(preview.Length > 0);
    }

    private static readonly ChartImageExportOptions DeterministicOptions = new()
    {
        Format = ImageExportFormat.Png,
        Scale = 1.0f,
        IncludeVisualHeader = false,
        EmbedFileMetadata = false,
        IncludeCrosshair = false
    };

    private sealed record LayeredFixture(
        ChartObjectManager Manager,
        DrawingLayerService Layers,
        Guid LowerLayerId,
        Guid UpperLayerId,
        TrendLineObject LowerLine,
        TrendLineObject UpperLine,
        GenericCoordinateTransform Transform,
        ChartDataSnapshot Snapshot,
        ChartLayoutContext Layout,
        IChartRenderConfig Config);

    /// <summary>Two overlapping identical-geometry thick lines (red below, blue above) in two Main-panel layers.</summary>
    private static LayeredFixture CreateLayeredFixture()
    {
        var (snapshot, layout, _, config) = CreateTestContext();

        var transform = new GenericCoordinateTransform(ChartAxisMode.Time, 800, 600);
        transform.UpdateCanvasSize(layout.TotalBounds.Width, layout.TotalBounds.Height,
            layout.ChartArea.X, layout.ChartArea.Y, layout.ChartArea.Width, layout.ChartArea.Height);
        transform.SetTimeRange(snapshot.Candles[0].Timestamp, snapshot.Candles[^1].Timestamp);
        transform.SetPriceRange(80m, 140m);

        var p1 = new ChartPoint(snapshot.Candles[3].Timestamp, 100m);
        var p2 = new ChartPoint(snapshot.Candles[15].Timestamp, 120m);
        var lower = new TrendLineObject(p1, p2) { Color = global::Avalonia.Media.Colors.Red, Thickness = 8 };
        var upper = new TrendLineObject(p1, p2) { Color = global::Avalonia.Media.Colors.Blue, Thickness = 8 };

        var manager = new ChartObjectManager();
        manager.AddObject(lower);
        manager.AddObject(upper);

        var layers = new DrawingLayerService();
        var lowerLayerId = layers.GetLayersForPanel(StockAnalyzer.Core.Models.Drawing.PanelKey.Main)[0].LayerId;
        layers.CreateLayer(StockAnalyzer.Core.Models.Drawing.PanelKey.Main, "Upper");
        var upperLayerId = layers.GetLayersForPanel(StockAnalyzer.Core.Models.Drawing.PanelKey.Main)[1].LayerId;
        layers.AddObjectToLayer(lower.Id, StockAnalyzer.Core.Models.Drawing.PanelKey.Main, lowerLayerId);
        layers.AddObjectToLayer(upper.Id, StockAnalyzer.Core.Models.Drawing.PanelKey.Main, upperLayerId);

        return new LayeredFixture(manager, layers, lowerLayerId, upperLayerId, lower, upper, transform, snapshot, layout, config);
    }

    private static Task<byte[]> RenderLayered(LayeredFixture f, ChartObjectManager manager, LayeredDrawingScene? scene)
        => new ChartImageExportService().RenderLayeredChartImageAsync(
            f.Snapshot, f.Layout, f.Transform, DeterministicOptions, new ThemeManager(),
            manager, new CandleStickRenderer(), f.Config, ChartType.Candlestick, new RulerRenderer(),
            new ChartImageMetadata { Symbol = "TEST" }, scene);

    private static LayeredDrawingScene BuildScene(LayeredFixture f)
    {
        Assert.True(LayeredDrawingSceneFactory.TryBuildSnapshotScene(
            f.Manager, f.Layers, null, false, out var scene, out _));
        return scene!;
    }

    private static global::SkiaSharp.SKColor PixelAtLineMidpoint(LayeredFixture f, byte[] png)
    {
        using var bitmap = global::SkiaSharp.SKBitmap.Decode(png);
        var mid = f.Transform.ChartToScreen(new ChartPoint(
            f.Snapshot.Candles[9].Timestamp, 110m));
        return bitmap.GetPixel((int)Math.Round(mid.X), (int)Math.Round(mid.Y));
    }

    [Fact]
    public async Task RenderLayeredChartImageAsync_NullScene_IsByteIdenticalToLegacyRender()
    {
        var f = CreateLayeredFixture();
        var legacy = await new ChartImageExportService().RenderChartImageAsync(
            f.Snapshot, f.Layout, f.Transform, DeterministicOptions, new ThemeManager(),
            f.Manager, new CandleStickRenderer(), f.Config, ChartType.Candlestick, new RulerRenderer(),
            new ChartImageMetadata { Symbol = "TEST" });

        var layered = await RenderLayered(f, f.Manager, null);

        Assert.Equal(legacy, layered);
    }

    [Fact]
    public async Task RenderLayeredChartImageAsync_HiddenLayer_DoesNotDrawItsObjects()
    {
        var f = CreateLayeredFixture();
        var baseline = await RenderLayered(f, new ChartObjectManager(), null);

        f.Layers.SetLayerVisibility(f.LowerLayerId, false);
        f.Layers.SetLayerVisibility(f.UpperLayerId, false);
        var allHidden = await RenderLayered(f, f.Manager, BuildScene(f));

        f.Layers.SetLayerVisibility(f.UpperLayerId, true);
        var upperVisible = await RenderLayered(f, f.Manager, BuildScene(f));

        Assert.Equal(baseline, allHidden);
        Assert.NotEqual(baseline, upperVisible);
    }

    [Fact]
    public async Task RenderLayeredChartImageAsync_LayerOrder_MatchesScreenFrontMostLayer()
    {
        var f = CreateLayeredFixture();

        var blueOnTop = await RenderLayered(f, f.Manager, BuildScene(f));
        f.Layers.MoveLayer(f.LowerLayerId, +1); // red layer moves above blue
        var redOnTop = await RenderLayered(f, f.Manager, BuildScene(f));

        var blue = new global::SkiaSharp.SKColor(0, 0, 255, 255);
        var red = new global::SkiaSharp.SKColor(255, 0, 0, 255);
        Assert.Equal(blue, PixelAtLineMidpoint(f, blueOnTop));
        Assert.Equal(red, PixelAtLineMidpoint(f, redOnTop));
    }

    [Fact]
    public async Task ExportToFileAsync_WritesFileAtomically()
    {
        // Arrange
        var sut = new ChartImageExportService();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_chart_export_{Guid.NewGuid():N}.png");
        byte[] dummyData = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        try
        {
            // Act
            var success = await sut.ExportToFileAsync(tempFile, dummyData);

            // Assert
            Assert.True(success);
            Assert.True(File.Exists(tempFile));
            var readBytes = await File.ReadAllBytesAsync(tempFile);
            Assert.Equal(dummyData, readBytes);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
