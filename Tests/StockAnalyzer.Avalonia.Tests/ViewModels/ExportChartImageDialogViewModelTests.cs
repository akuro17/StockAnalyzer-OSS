using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Moq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Services.Export;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Export;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class ExportChartImageDialogViewModelTests
{
    private static (ExportChartImageDialogViewModel, Mock<IChartImageExportService>, Mock<IDialogService>, Mock<ITemplateService>) CreateSut(
        string symbol = "7203",
        string company = "トヨタ自動車",
        string timeframe = "Daily",
        bool simulateFileExists = false)
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 10; i++)
        {
            candles.Add(new CoreCandleData(DateTime.Today.AddDays(i), 100m, 110m, 90m, 105m, 1000));
        }

        var snapshot = new ChartDataSnapshot(candles: candles, startIndex: 0, count: 10);
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
            VisibleCandleCount: 10,
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

        var mockExportService = new Mock<IChartImageExportService>();
        mockExportService
            .Setup(s => s.RenderChartImageAsync(
                It.IsAny<ChartDataSnapshot>(),
                It.IsAny<ChartLayoutContext>(),
                It.IsAny<ICoordinateTransform>(),
                It.IsAny<ChartImageExportOptions>(),
                It.IsAny<IThemeManager>(),
                It.IsAny<ChartObjectManager>(),
                It.IsAny<IChartRenderer>(),
                It.IsAny<IChartRenderConfig>(),
                It.IsAny<ChartType>(),
                It.IsAny<RulerRenderer>(),
                It.IsAny<ChartImageMetadata>()))
            .ReturnsAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        mockExportService
            .Setup(s => s.ExportToFileAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync(true);

        var mockDialogService = new Mock<IDialogService>();
        var mockTemplateService = new Mock<ITemplateService>();

        var vm = new ExportChartImageDialogViewModel(
            snapshot,
            layout,
            transform,
            new ChartObjectManager(),
            new CandleStickRenderer(),
            config,
            ChartType.Candlestick,
            new RulerRenderer(),
            themeManager,
            mockExportService.Object,
            mockDialogService.Object,
            mockTemplateService.Object,
            symbol,
            company,
            timeframe);

        return (vm, mockExportService, mockDialogService, mockTemplateService);
    }

    [Fact]
    public void Constructor_InitializesDefaultValues()
    {
        var (sut, _, _, _) = CreateSut();

        Assert.Equal("7203", sut.Symbol);
        Assert.Equal("トヨタ自動車", sut.CompanyName);
        Assert.Equal("Daily", sut.Timeframe);
        Assert.Equal(ImageExportFormat.Png, sut.SelectedFormat);
        Assert.Equal(ChartExportSizeMode.CurrentWindow, sut.SelectedSizeMode);
        Assert.NotNull(sut.SelectedTheme);
        Assert.Equal("Dark", sut.SelectedTheme?.Name);
        Assert.Equal(5, sut.AvailableSizeModes.Count);
        Assert.True(sut.AvailableThemes.Count >= 2);
        Assert.True(sut.IncludeVisualHeader);
        Assert.True(sut.IncludeSymbol);
        Assert.True(sut.IncludeCompanyName);
        Assert.True(sut.IncludeTimeframe);
        Assert.True(sut.IncludeDateRange);
        Assert.True(sut.IncludeIndicators);
        Assert.True(sut.IncludeBrand);
        Assert.True(sut.EmbedFileMetadata);
        Assert.False(sut.IncludeCrosshair);
        Assert.False(sut.IsQualityVisible);
        Assert.False(sut.IsCustomSize);
        Assert.NotEmpty(sut.SaveDirectory);
        Assert.NotEmpty(sut.FileName);
        Assert.Contains(".png", sut.FilePath);
        Assert.NotEmpty(sut.OutputDimensionsText);
    }

    [Fact]
    public void ChangingFormat_UpdatesExtensionAndQualityVisibility()
    {
        var (sut, _, _, _) = CreateSut();

        sut.SelectedFormat = ImageExportFormat.Jpeg;
        Assert.True(sut.IsQualityVisible);
        Assert.EndsWith(".jpg", sut.FilePath);

        sut.SelectedFormat = ImageExportFormat.Webp;
        Assert.True(sut.IsQualityVisible);
        Assert.EndsWith(".webp", sut.FilePath);

        sut.SelectedFormat = ImageExportFormat.Png;
        Assert.False(sut.IsQualityVisible);
        Assert.EndsWith(".png", sut.FilePath);
    }

    [Fact]
    public void ChangingSizeMode_UpdatesOutputDimensionsTextAndScale()
    {
        var (sut, _, _, _) = CreateSut();

        sut.SelectedSizeMode = ChartExportSizeMode.Preset1280x720;
        var dimHD = sut.OutputDimensionsText;
        var (wHD, hHD, scaleHD) = sut.CalculateOutputDimensions();
        Assert.True(wHD <= 1280 && hHD <= 720);
        Assert.True(scaleHD > 0);

        sut.SelectedSizeMode = ChartExportSizeMode.Preset1920x1080;
        var dimFHD = sut.OutputDimensionsText;
        var (wFHD, hFHD, scaleFHD) = sut.CalculateOutputDimensions();
        Assert.True(wFHD <= 1920 && hFHD <= 1080);
        Assert.True(scaleFHD > scaleHD);

        sut.SelectedSizeMode = ChartExportSizeMode.Preset3840x2160;
        var dim4K = sut.OutputDimensionsText;
        var (w4K, h4K, scale4K) = sut.CalculateOutputDimensions();
        Assert.True(w4K <= 3840 && h4K <= 2160);
        Assert.True(scale4K > scaleFHD);

        Assert.NotEqual(dimHD, dimFHD);
        Assert.NotEqual(dimFHD, dim4K);
    }

    [Fact]
    public void CustomSize_LockAspectRatio_AutomaticallySynchronizesDimensions()
    {
        var (sut, _, _, _) = CreateSut();
        sut.SelectedSizeMode = ChartExportSizeMode.Custom;
        Assert.True(sut.IsCustomSize);
        Assert.True(sut.LockAspectRatio);

        // Change width -> height automatically updates
        sut.CustomWidth = 1600;
        Assert.True(sut.CustomHeight > 0);

        var previousHeight = sut.CustomHeight;

        // Change height -> width automatically updates
        sut.CustomHeight = 652;
        Assert.NotEqual(1600, sut.CustomWidth);
    }

    [Fact]
    public async Task InitializeAsync_LoadsCustomThemesFromTemplateService()
    {
        var (sut, _, _, mockTemplate) = CreateSut();

        var customTemplate = new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Cyberpunk Neon",
            Colors = ThemeColors.Dark with { ChartBackground = IndicatorColor.FromRgb(10, 10, 30) }
        };

        mockTemplate
            .Setup(t => t.GetAllAsync<ThemeTemplate>(TemplateType.Theme))
            .ReturnsAsync(new List<ThemeTemplate> { customTemplate });

        await sut.InitializeAsync();

        Assert.Contains(sut.AvailableThemes, t => t.Name == "Cyberpunk Neon" && t.IsCustom);
    }

    [Fact]
    public async Task SaveCommand_WhenFileDoesNotExist_SavesDirectlyAndCloses()
    {
        var (sut, mockExport, mockDialog, _) = CreateSut();
        sut.SaveDirectory = Path.GetTempPath();
        sut.FileName = $"TestExport_{Guid.NewGuid():N}";
        bool? closedResult = null;
        sut.RequestClose = res => closedResult = res;

        await sut.SaveCommand.ExecuteAsync(null);

        Assert.True(closedResult);
        mockExport.Verify(s => s.ExportToFileAsync(sut.FilePath, It.IsAny<byte[]>()), Times.Once);
        mockDialog.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveCommand_WhenFileExists_AndUserConfirms_OverwritesAndCloses()
    {
        var (sut, mockExport, mockDialog, _) = CreateSut();
        var tempPng = Path.Combine(Path.GetTempPath(), $"TestExport_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPng, [1, 2, 3]);
        try
        {
            sut.SaveDirectory = Path.GetDirectoryName(tempPng)!;
            sut.FileName = Path.GetFileNameWithoutExtension(tempPng);
            sut.SelectedFormat = ImageExportFormat.Png;

            // User confirms overwrite
            mockDialog
                .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            bool? closedResult = null;
            sut.RequestClose = res => closedResult = res;

            await sut.SaveCommand.ExecuteAsync(null);

            Assert.True(closedResult);
            mockDialog.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            mockExport.Verify(s => s.ExportToFileAsync(sut.FilePath, It.IsAny<byte[]>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    [Fact]
    public async Task SaveCommand_WhenFileExists_AndUserDeclines_AbortsSaveWithoutClosing()
    {
        var (sut, mockExport, mockDialog, _) = CreateSut();
        var tempPng = Path.Combine(Path.GetTempPath(), $"TestExport_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tempPng, [1, 2, 3]);
        try
        {
            sut.SaveDirectory = Path.GetDirectoryName(tempPng)!;
            sut.FileName = Path.GetFileNameWithoutExtension(tempPng);
            sut.SelectedFormat = ImageExportFormat.Png;

            // User declines overwrite
            mockDialog
                .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(false);

            bool? closedResult = null;
            sut.RequestClose = res => closedResult = res;

            await sut.SaveCommand.ExecuteAsync(null);

            Assert.Null(closedResult);
            mockDialog.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            mockExport.Verify(s => s.ExportToFileAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
            Assert.Equal("Save cancelled.", sut.StatusMessage);
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    [Fact]
    public async Task BrowseFolderCommand_UpdatesSaveDirectory()
    {
        var (sut, _, mockDialog, _) = CreateSut();
        mockDialog
            .Setup(d => d.ShowOpenFolderDialogAsync("Select Save Folder", sut.SaveDirectory))
            .ReturnsAsync(@"C:\CustomExports");

        await sut.BrowseFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\CustomExports", sut.SaveDirectory);
        Assert.StartsWith(@"C:\CustomExports", sut.FilePath);
    }

    [Fact]
    public void CancelCommand_ClosesDialogWithFalse()
    {
        var (sut, _, _, _) = CreateSut();
        bool? closedResult = null;
        sut.RequestClose = res => closedResult = res;

        sut.CancelCommand.Execute(null);

        Assert.False(closedResult);
    }
}
