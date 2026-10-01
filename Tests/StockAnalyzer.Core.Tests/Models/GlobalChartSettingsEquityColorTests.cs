using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Configuration;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>The Backtest equity curve color settings of <see cref="GlobalChartSettings"/> and their resolved <see cref="BacktestEquityLineStyle"/>.</summary>
public class GlobalChartSettingsEquityColorTests
{
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"chart-settings-equity-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_EqualTheConstants_AndTheModeIsDrawdown()
    {
        var settings = new GlobalChartSettings();

        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityColorMode, settings.BacktestEquityColorMode);
        Assert.Equal(BacktestEquityColorMode.Drawdown, settings.BacktestEquityColorMode);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, settings.BacktestEquityLineColor);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityUpColor, settings.BacktestEquityUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityDownColor, settings.BacktestEquityDownColor);
        Assert.Equal("#FF2962FF", settings.BacktestEquityLineColor);
    }

    [Fact]
    public void ModeNumbers_ArePersistedContract_NeverRenumbered()
    {
        Assert.Equal(0, (int)BacktestEquityColorMode.Single);
        Assert.Equal(1, (int)BacktestEquityColorMode.PreviousBar);
        Assert.Equal(2, (int)BacktestEquityColorMode.Drawdown);
        Assert.Equal(3, Enum.GetValues<BacktestEquityColorMode>().Length);
    }

    [Fact]
    public void SchemaVersion_StaysOne()
    {
        Assert.Equal(1, new GlobalChartSettings().SchemaVersion);
        Assert.Equal(1, new GlobalChartSettings().Validate().SchemaVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-color")]
    [InlineData("#12345")]
    [InlineData("2962FF")]
    [InlineData("#GG0000")]
    public void Validate_InvalidColor_FallsBackToTheDefault(string invalid)
    {
        var healed = new GlobalChartSettings
        {
            BacktestEquityLineColor = invalid,
            BacktestEquityUpColor = invalid,
            BacktestEquityDownColor = invalid,
        }.Validate();

        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, healed.BacktestEquityLineColor);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityUpColor, healed.BacktestEquityUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityDownColor, healed.BacktestEquityDownColor);
    }

    [Fact]
    public void Validate_ValidColors_AreKeptUnchanged()
    {
        var kept = new GlobalChartSettings
        {
            BacktestEquityLineColor = "#102030",
            BacktestEquityUpColor = "#80112233",
            BacktestEquityDownColor = "#FFAABBCC",
        }.Validate();

        Assert.Equal("#102030", kept.BacktestEquityLineColor);
        Assert.Equal("#80112233", kept.BacktestEquityUpColor);
        Assert.Equal("#FFAABBCC", kept.BacktestEquityDownColor);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(3)]
    public void Validate_UndefinedMode_FallsBackToDrawdown(int number)
    {
        var healed = new GlobalChartSettings { BacktestEquityColorMode = (BacktestEquityColorMode)number }.Validate();

        Assert.Equal(BacktestEquityColorMode.Drawdown, healed.BacktestEquityColorMode);
    }

    [Theory]
    [InlineData(BacktestEquityColorMode.Single)]
    [InlineData(BacktestEquityColorMode.PreviousBar)]
    [InlineData(BacktestEquityColorMode.Drawdown)]
    public void Validate_DefinedMode_IsKept(BacktestEquityColorMode mode)
    {
        Assert.Equal(mode, new GlobalChartSettings { BacktestEquityColorMode = mode }.Validate().BacktestEquityColorMode);
    }

    // The persistence tests go through the real ChartSettingsManager: it is the production reader/writer (reflection serializer with
    // camelCase names), whereas the source-generated GlobalChartSettingsJsonContext is not used to load the file.
    [Fact]
    public async Task SavedFile_RoundTripsAllFour_AndStoresTheModeAsANumber()
    {
        string path = NewTempPath();
        try
        {
            var writer = new ChartSettingsManager(path);
            await writer.UpdateAsync(new GlobalChartSettings
            {
                BacktestEquityColorMode = BacktestEquityColorMode.PreviousBar,
                BacktestEquityLineColor = "#FF010203",
                BacktestEquityUpColor = "#FF040506",
                BacktestEquityDownColor = "#FF070809",
            });

            Assert.Contains("\"backtestEquityColorMode\": 1", await File.ReadAllTextAsync(path));

            var reader = new ChartSettingsManager(path);
            await reader.LoadAsync();
            Assert.Equal(BacktestEquityColorMode.PreviousBar, reader.Current.BacktestEquityColorMode);
            Assert.Equal("#FF010203", reader.Current.BacktestEquityLineColor);
            Assert.Equal("#FF040506", reader.Current.BacktestEquityUpColor);
            Assert.Equal("#FF070809", reader.Current.BacktestEquityDownColor);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OldFileWithoutTheFourKeys_LoadsThemAsDefaults_AndKeepsExistingValues()
    {
        string path = NewTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"defaultStrokeThickness\":2.5,\"lineColor\":\"#123456\",\"topMargin\":12,\"schemaVersion\":1}");

            var manager = new ChartSettingsManager(path);
            await manager.LoadAsync();

            GlobalChartSettings loaded = manager.Current;
            Assert.Equal(2.5, loaded.DefaultStrokeThickness);
            Assert.Equal("#123456", loaded.LineColor);
            Assert.Equal(12f, loaded.TopMargin);
            Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, loaded.BacktestEquityLineColor);
            Assert.Equal(BacktestEquityColorMode.Drawdown, loaded.BacktestEquityColorMode);
            Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityUpColor, loaded.BacktestEquityUpColor);
            Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityDownColor, loaded.BacktestEquityDownColor);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task FileWithAnUndefinedModeNumber_LoadsAsDrawdown()
    {
        string path = NewTempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{\"backtestEquityColorMode\":99,\"backtestEquityLineColor\":\"bad\"}");

            var manager = new ChartSettingsManager(path);
            await manager.LoadAsync();

            Assert.Equal(BacktestEquityColorMode.Drawdown, manager.Current.BacktestEquityColorMode);
            Assert.Equal(ChartSettingsConstants.DefaultBacktestEquityLineColor, manager.Current.BacktestEquityLineColor);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FromSettings_Default_ResolvesTheExpectedColors()
    {
        BacktestEquityLineStyle style = BacktestEquityLineStyle.FromSettings(new GlobalChartSettings());

        Assert.Equal(BacktestEquityColorMode.Drawdown, style.Mode);
        Assert.Equal(new IndicatorColor(0xFF, 0x29, 0x62, 0xFF), style.LineColor);
        Assert.Equal(new IndicatorColor(0xFF, 0x4C, 0xAF, 0x50), style.UpColor);
        Assert.Equal(new IndicatorColor(0xFF, 0xF4, 0x43, 0x36), style.DownColor);
    }

    [Fact]
    public void FromSettings_InvalidHex_FallsBackToTheDefaultColor()
    {
        BacktestEquityLineStyle style = BacktestEquityLineStyle.FromSettings(new GlobalChartSettings
        {
            BacktestEquityLineColor = "nope",
            BacktestEquityUpColor = "#12",
            BacktestEquityDownColor = string.Empty,
        });

        BacktestEquityLineStyle defaults = BacktestEquityLineStyle.FromSettings(new GlobalChartSettings());
        Assert.Equal(defaults, style);
    }

    [Fact]
    public void FromSettings_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BacktestEquityLineStyle.FromSettings(null!));
    }

    [Fact]
    public void FromSettings_SixDigitHex_IsOpaque_AndAlphaIsKept()
    {
        BacktestEquityLineStyle style = BacktestEquityLineStyle.FromSettings(new GlobalChartSettings
        {
            BacktestEquityLineColor = "#102030",
            BacktestEquityUpColor = "#80112233",
        });

        Assert.Equal(new IndicatorColor(0xFF, 0x10, 0x20, 0x30), style.LineColor);
        Assert.Equal(0x80, style.UpColor.A);
    }

    [Fact]
    public void ResolveHsv_ToHtml_IsIdempotent_ForSampledColors()
    {
        // The settings page treats "stored hex" and "hex after one picker round trip" as the same value; that only holds when
        // hex -> HSV -> hex is stable after its first pass.
        string fallback = ChartSettingsConstants.DefaultBacktestEquityLineColor;
        var channels = Enumerable.Range(0, 18).Select(i => Math.Min(i * 15, 255)).Append(255).Distinct().ToArray();
        foreach (int alpha in new[] { 0x00, 0x01, 0x80, 0xFF })
        foreach (int r in channels)
        foreach (int g in channels)
        foreach (int b in channels)
        {
            string hex = $"#{alpha:X2}{r:X2}{g:X2}{b:X2}";
            string once = BacktestEquityLineStyle.ResolveHsv(hex, fallback).ToHtml();
            string twice = BacktestEquityLineStyle.ResolveHsv(once, fallback).ToHtml();
            Assert.True(once == twice, $"{hex} -> {once} -> {twice}");
        }
    }

    [Fact]
    public void ResolveHsv_UnparsableValue_ResolvesToTheParsedDefault()
    {
        Assert.Equal(
            HsvData.FromHtmlSafe(ChartSettingsConstants.DefaultBacktestEquityUpColor),
            BacktestEquityLineStyle.ResolveHsv("nope", ChartSettingsConstants.DefaultBacktestEquityUpColor));
    }

    [Theory]
    [InlineData("#FF2962FF")]
    [InlineData("#FF4CAF50")]
    [InlineData("#FFF44336")]
    public void DefaultColors_SurviveTheHsvRoundTripExactly(string html)
    {
        Assert.Equal(html, HsvData.FromHtmlSafe(html).ToHtml());
    }
}
