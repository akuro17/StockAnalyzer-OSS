using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Drawing;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using Xunit;
using Point = Avalonia.Point;

namespace StockAnalyzer.Tests.Drawing;

public class IconDrawingTests
{
    private class DummyCoordinateTransform : ICoordinateTransform
    {
        public double CanvasWidth => 800;
        public double CanvasHeight => 600;
        public Rect ScreenRect => new Rect(0, 0, 800, 600);
        public double ViewportX => 0;
        public double ViewportWidth => 800;
        public double ScaleX => 1.0;
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => new TransformMetadata(false, true, ChartType.Line);
        public IReadOnlyList<DateTime>? TimeMap => null;

        public Point ChartToScreen(ChartPoint chartPoint)
        {
            double x = (chartPoint.Time - new DateTime(2025, 1, 1)).TotalDays * 10.0;
            double y = 600.0 - (double)chartPoint.Price;
            return new Point(x, y);
        }

        public ChartPoint ScreenToChart(Point screenPoint)
        {
            var time = new DateTime(2025, 1, 1).AddDays(screenPoint.X / 10.0);
            var price = (decimal)(600.0 - screenPoint.Y);
            return new ChartPoint(time, price);
        }

        public Point NumericToScreen(double x, double y) => new Point(x, y);
        public (double x, double y) ScreenToNumeric(Point screenPoint) => (screenPoint.X, screenPoint.Y);
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice, double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> timeMap) { }
        public double GetXFromIndex(double index) => index;
        public double GetYFromPrice(decimal price) => 600.0 - (double)price;
    }

    [Fact]
    public void DrawingToolCategoryService_ContainsIconCategoryRightAfterCycles()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        int cyclesIdx = -1;
        int iconIdx = -1;

        for (int i = 0; i < categories.Count; i++)
        {
            if (categories[i].NameKey == "DrawCat_Cycles") cyclesIdx = i;
            if (categories[i].NameKey == "DrawCat_Icon") iconIdx = i;
        }

        Assert.True(cyclesIdx >= 0, "DrawCat_Cycles must exist");
        Assert.True(iconIdx >= 0, "DrawCat_Icon must exist");
        Assert.Equal(cyclesIdx + 1, iconIdx);

        var iconCat = categories[iconIdx];
        Assert.Equal("\U0001F5BC\uFE0F", iconCat.Icon);
    }

    [Fact]
    public void DrawingToolBehaviorRegistry_ContainsIconBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.Icon);
        Assert.NotNull(behavior);
        Assert.IsType<IconBehavior>(behavior);
        Assert.Equal(1, behavior.RequiredSteps);
    }

    [Fact]
    public void IconObject_Initialization_HasCorrectDefaults()
    {
        var pt = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        using var iconObj = new IconObject(pt, "sub/test.png", "test");

        Assert.Equal(ChartObjectType.Icon, iconObj.Type);
        Assert.Single(iconObj.Points);
        Assert.Equal(pt, iconObj.Points[0]);
        Assert.Equal("test", iconObj.CustomName);
        Assert.Equal("sub/test.png", iconObj.IconPath);
        Assert.Equal(100.0, iconObj.FontOpacity);
        Assert.True(iconObj.FontSize > 0);
    }

    [Fact]
    public void IconObject_HitTest_AccurateBoundingCheck()
    {
        var transform = new DummyCoordinateTransform();
        // ChartToScreen: (2025-01-11, 500m) -> x = 100, y = 600 - 500 = 100
        var pt = new ChartPoint(new DateTime(2025, 1, 11), 500m);
        using var iconObj = new IconObject(pt, "test.png", "test")
        {
            FontSize = 40.0
        };

        // Center (100, 100) should hit
        Assert.True(iconObj.HitTest(new Point(100, 100), transform));

        // Point within 40x40 box (e.g. 115, 115) should hit
        Assert.True(iconObj.HitTest(new Point(115, 115), transform));

        // Far point (200, 200) should miss
        Assert.False(iconObj.HitTest(new Point(200, 200), transform));
    }

    [Fact]
    public void IconObject_Translate_MovesPositionCorrectly()
    {
        var pt = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        using var iconObj = new IconObject(pt, "test.png", "test");

        iconObj.Translate(TimeSpan.FromDays(2), 25.5m);

        Assert.Equal(new DateTime(2025, 1, 3), iconObj.Points[0].Time);
        Assert.Equal(125.5m, iconObj.Points[0].Price);
    }

    [Fact]
    public void IconObject_Render_ExecutesWithoutError()
    {
        var transform = new DummyCoordinateTransform();
        var pt = new ChartPoint(new DateTime(2025, 1, 11), 500m);
        using var iconObj = new IconObject(pt, "test.png", "test");

        // Supply a mock 16x16 SKBitmap
        using var bmp = new SKBitmap(16, 16);
        iconObj.SetBitmap(bmp);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        // Should render without throwing
        iconObj.Render(canvas, transform);
        iconObj.IsSelected = true;
        iconObj.Render(canvas, transform);
    }

    [Fact]
    public void IconSettingsPanelDefinition_CanHandle_OnlyIconObject()
    {
        var def = new IconSettingsPanelDefinition();
        var pt = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        using var iconObj = new IconObject(pt, "test.png", "test");

        Assert.True(def.CanHandle(iconObj));
    }

    [Fact]
    public void IcnsDecoder_ExtractsPngPayloadCorrectly()
    {
        // Build a mock ICNS container with 'icns' magic and 'ic07' chunk
        byte[] mockPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02 };
        int chunkLen = 8 + mockPng.Length;
        int totalLen = 8 + chunkLen;

        byte[] icnsData = new byte[totalLen];
        // 'icns'
        icnsData[0] = (byte)'i'; icnsData[1] = (byte)'c'; icnsData[2] = (byte)'n'; icnsData[3] = (byte)'s';
        icnsData[4] = (byte)(totalLen >> 24);
        icnsData[5] = (byte)(totalLen >> 16);
        icnsData[6] = (byte)(totalLen >> 8);
        icnsData[7] = (byte)totalLen;

        // 'ic07'
        icnsData[8] = (byte)'i'; icnsData[9] = (byte)'c'; icnsData[10] = (byte)'0'; icnsData[11] = (byte)'7';
        icnsData[12] = (byte)(chunkLen >> 24);
        icnsData[13] = (byte)(chunkLen >> 16);
        icnsData[14] = (byte)(chunkLen >> 8);
        icnsData[15] = (byte)chunkLen;

        Array.Copy(mockPng, 0, icnsData, 16, mockPng.Length);

        byte[]? extracted = IcnsDecoder.ExtractLargestPng(icnsData);
        Assert.NotNull(extracted);
        Assert.Equal(mockPng, extracted);
    }

    [Fact]
    public void SvgIconDecoder_ParsesPathAndRendersBitmap()
    {
        string svg = @"<svg viewBox=""0 0 100 100"" xmlns=""http://www.w3.org/2000/svg"">
            <path d=""M 10 10 L 90 90"" stroke=""black"" />
        </svg>";

        var bmp = SvgIconDecoder.RenderToSkBitmapFromXml(svg);
        Assert.NotNull(bmp);
        Assert.Equal(256, bmp.Width);
        Assert.Equal(256, bmp.Height);
        bmp.Dispose();
    }

    [Fact]
    public void IconDrawingService_DiscoversOnlySupported4FormatsAnd1stLevelFolders()
    {
        // Setup isolated test folder structure
        string tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer_IconTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Root valid files
            File.WriteAllBytes(Path.Combine(tempDir, "root_a.png"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(tempDir, "root_b.svg"), Encoding.UTF8.GetBytes("<svg></svg>"));
            File.WriteAllBytes(Path.Combine(tempDir, "root_c.ico"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(tempDir, "root_d.icns"), new byte[] { 1, 2, 3 });

            // Root INVALID files (should be ignored)
            File.WriteAllBytes(Path.Combine(tempDir, "root_invalid.jpg"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(tempDir, "root_invalid.txt"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(tempDir, "root_invalid.bmp"), new byte[] { 1, 2, 3 });

            // 1st level subfolder
            string subDir = Path.Combine(tempDir, "Arrows");
            Directory.CreateDirectory(subDir);
            File.WriteAllBytes(Path.Combine(subDir, "arrow_up.png"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(subDir, "arrow_down.svg"), Encoding.UTF8.GetBytes("<svg></svg>"));
            File.WriteAllBytes(Path.Combine(subDir, "arrow_invalid.gif"), new byte[] { 1, 2, 3 });

            // 2nd level subfolder (should be IGNORED completely)
            string deepDir = Path.Combine(subDir, "DeepSubfolder");
            Directory.CreateDirectory(deepDir);
            File.WriteAllBytes(Path.Combine(deepDir, "deep_arrow.png"), new byte[] { 1, 2, 3 });

            var service = new IconDrawingService(null, tempDir);

            // Verify groups
            var groups = service.GetGroups();
            Assert.Equal(2, groups.Count);
            Assert.Contains(groups, g => g.IsAll);
            Assert.Contains(groups, g => g.Key == "Arrows");

            // Verify "All Icons" group contains root 4 + subfolder 2 = 6 icons (ignoring invalid extensions and deep folder)
            var allIcons = service.GetIcons(null);
            Assert.Equal(6, allIcons.Count);
            Assert.DoesNotContain(allIcons, i => i.Name.Contains("invalid"));
            Assert.DoesNotContain(allIcons, i => i.Name.Contains("deep"));

            // Verify "Arrows" group contains 2 icons
            var arrowIcons = service.GetIcons("Arrows");
            Assert.Equal(2, arrowIcons.Count);
            Assert.All(arrowIcons, i => Assert.Equal("Arrows", i.GroupKey));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void BuildOutput_DefaultDrawingIcons_ArrowFolderCopiedToDataDirectory()
    {
        var service = new IconDrawingService();
        var groups = service.GetGroups();

        var arrowGroup = groups.FirstOrDefault(g => string.Equals(g.Key, "Arrow", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(arrowGroup);

        var arrowIcons = service.GetIcons("Arrow");
        Assert.Equal(8, arrowIcons.Count);
        Assert.Contains(arrowIcons, i => i.Name == "north");
        Assert.Contains(arrowIcons, i => i.Name == "south");
        Assert.Contains(arrowIcons, i => i.Name == "east");
        Assert.Contains(arrowIcons, i => i.Name == "west");
    }

    [Fact]
    public void DrawingToolSidebarViewModel_IconCategory_SwitchesGroupsAndLoadsArrowTools()
    {
        var iconService = new IconDrawingService();
        var chartVm = new ChartViewModel();
        var sidebarVm = new DrawingToolSidebarViewModel(
            chartVm,
            new DrawingObjectsViewModel(chartVm, new StockAnalyzer.Tests.Backtest.SynchronousDispatcherService()),
            iconService);

        var iconCat = sidebarVm.Categories.FirstOrDefault(c => c.IsIconCategory);
        Assert.NotNull(iconCat);

        // Open Icon category
        sidebarVm.OpenCategory(iconCat);
        Assert.True(sidebarVm.IsFlyoutOpen);
        Assert.Contains(sidebarVm.IconGroups, g => string.Equals(g.Key, "Arrow", StringComparison.OrdinalIgnoreCase));

        // Switch to "Arrow" group
        var arrowGroup = sidebarVm.IconGroups.First(g => string.Equals(g.Key, "Arrow", StringComparison.OrdinalIgnoreCase));
        sidebarVm.SelectedIconGroup = arrowGroup;

        Assert.Equal(8, iconCat.Tools.Count);
        Assert.Contains(iconCat.Tools, t => t.NameKey == "north");
        Assert.Contains(iconCat.Tools, t => t.NameKey == "south");
    }

    [Fact]
    public void DrawingToolSidebarViewModel_IconCategory_NoToolsSelectedInitiallyUntilExplicitSelection()
    {
        var iconService = new IconDrawingService();
        var chartVm = new ChartViewModel();
        var sidebarVm = new DrawingToolSidebarViewModel(
            chartVm,
            new DrawingObjectsViewModel(chartVm, new StockAnalyzer.Tests.Backtest.SynchronousDispatcherService()),
            iconService);

        var iconCat = sidebarVm.Categories.FirstOrDefault(c => c.IsIconCategory);
        Assert.NotNull(iconCat);

        // Open Icon category initially
        sidebarVm.OpenCategory(iconCat);
        Assert.True(sidebarVm.IsFlyoutOpen);

        // All tools must be unselected initially (none are blue)
        Assert.NotEmpty(iconCat.Tools);
        Assert.All(iconCat.Tools, t => Assert.False(t.IsSelected));

        // Select the second tool
        var targetTool = iconCat.Tools[1];
        sidebarVm.SelectTool(targetTool);
        Assert.True(targetTool.IsSelected);
        Assert.Equal(DrawingTool.Icon, chartVm.CurrentTool);
        Assert.NotNull(iconService.ActiveIcon);
        Assert.Equal(targetTool.IconInfo?.RelativePath, iconService.ActiveIcon.RelativePath);

        // Reopen Icon category: only the selected tool is marked as selected
        sidebarVm.OpenCategory(iconCat);
        Assert.True(iconCat.Tools.First(t => t.IconInfo?.RelativePath == targetTool.IconInfo?.RelativePath).IsSelected);
        Assert.Single(iconCat.Tools.Where(t => t.IsSelected));

        // Switch to a non-icon tool: all icons become unselected
        chartVm.CurrentTool = DrawingTool.TrendLine;
        sidebarVm.OpenCategory(iconCat);
        Assert.All(iconCat.Tools, t => Assert.False(t.IsSelected));
    }

    [Fact]
    public void DrawingThemeContext_DrawingIconFontSize_DefaultsTo32AndIconObjectInherits()
    {
        Assert.Equal(32.0f, ChartSettingsConstants.DefaultDrawingIconFontSize);
        Assert.Equal(32.0f, DrawingThemeContext.DrawingIconFontSize);

        var pt = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        using var iconObj = new IconObject(pt, "test.png", "test");
        Assert.Equal(32.0, iconObj.FontSize);
    }

    [Fact]
    public void DrawingSettingsViewModel_DrawingIconFontSize_TracksModification()
    {
        var settingsManager = new MockChartSettingsManager();
        var vm = new DrawingSettingsViewModel(settingsManager);
        Assert.Equal(32.0f, vm.DrawingIconFontSize);
        Assert.False(vm.IsModified);

        vm.DrawingIconFontSize = 48.0f;
        Assert.True(vm.IsModified);
    }

    [Fact]
    public void DrawingObjectItemViewModel_IsIconObject_IdentifiesIconAndNonIcon()
    {
        var manager = new ChartObjectManager();
        var pt = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        using var iconObj = new IconObject(pt, "test.png", "test");
        var iconItemVm = new DrawingObjectItemViewModel(iconObj, manager);

        Assert.True(iconItemVm.IsIconObject);

        var pt2 = new ChartPoint(new DateTime(2025, 1, 2), 105m);
        var trendLine = new TrendLineObject(pt, pt2);
        var trendLineItemVm = new DrawingObjectItemViewModel(trendLine, manager);

        Assert.False(trendLineItemVm.IsIconObject);
    }

    [Fact]
    public void Favorites_SelectionSync_UpdatesIsSelected()
    {
        var chartVm = new ChartViewModel();
        var sidebarVm = new DrawingToolSidebarViewModel(
            chartVm,
            new DrawingObjectsViewModel(chartVm, new StockAnalyzer.Tests.Backtest.SynchronousDispatcherService()),
            new IconDrawingService());

        var tool1 = sidebarVm.Categories[0].Tools[0];
        var tool2 = sidebarVm.Categories[0].Tools[1];

        // Favorite tool1
        tool1.ToggleFavoriteCommand.Execute(null);
        Assert.Contains(tool1, sidebarVm.Favorites);

        // Select tool1 via sidebar
        sidebarVm.SelectTool(tool1);
        Assert.True(tool1.IsSelected);
        Assert.True(sidebarVm.Favorites.First(f => f == tool1).IsSelected);

        // Select tool2
        sidebarVm.SelectTool(tool2);
        Assert.False(tool1.IsSelected);
        Assert.True(tool2.IsSelected);
        Assert.False(sidebarVm.Favorites.First(f => f == tool1).IsSelected);
    }

    [Fact]
    public void DrawingObjectItemViewModel_WithInjectedIconService_LoadsThumbnail()
    {
        var manager = new ChartObjectManager();
        var pt = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        using var iconObj = new IconObject(pt, "non_existent.png", "test");
        var iconService = new IconDrawingService();
        var iconItemVm = new DrawingObjectItemViewModel(iconObj, manager, null, null, iconService);

        Assert.True(iconItemVm.IsIconObject);
        Assert.Null(iconItemVm.IconThumbnail);
    }

    [Fact]
    public void DrawingThemeContext_IconDrawingService_IsAccessibleWithoutServiceLocator()
    {
        var service = new IconDrawingService();
        DrawingThemeContext.IconDrawingService = service;
        Assert.Same(service, DrawingThemeContext.IconDrawingService);
    }
}
