using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class TemplateServiceTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly TemplateService _service;

    public TemplateServiceTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "StockAnalyzerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testBaseDir);
        _service = new TemplateService();
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, true);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    [Fact]
    public async Task SaveAsync_And_GetAsync_ShouldPersistAndRetrieveIndicatorTemplate()
    {
        // Arrange
        var template = new IndicatorTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Multi-MA Strategy",
            Description = "5/25/75 SMA combination",
            IsFavorite = true,
            Indicators = new List<CoreIndicatorSettings>
            {
                new()
                {
                    Id = "ind-1",
                    DisplayName = "SMA(5)",
                    TypeEnum = IndicatorType.SMA,
                    ParameterObject = new CoreSmaParameter { Period = 5 }
                },
                new()
                {
                    Id = "ind-2",
                    DisplayName = "SMA(25)",
                    TypeEnum = IndicatorType.SMA,
                    ParameterObject = new CoreSmaParameter { Period = 25 }
                }
            }
        };

        // Act
        await _service.SaveAsync(template);
        var loaded = await _service.GetAsync<IndicatorTemplate>(TemplateType.Indicator, template.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(template.Id, loaded.Id);
        Assert.Equal("Multi-MA Strategy", loaded.Name);
        Assert.Equal("5/25/75 SMA combination", loaded.Description);
        Assert.True(loaded.IsFavorite);
        Assert.Equal(2, loaded.Indicators.Count);
        Assert.Equal("Simple Moving Average", loaded.Indicators[0].DisplayName);
        Assert.Equal("SMA(5)", loaded.Indicators[0].ShortDisplayName);
        Assert.IsType<CoreSmaParameter>(loaded.Indicators[0].ParameterObject);
        Assert.Equal(5, ((CoreSmaParameter)loaded.Indicators[0].ParameterObject).Period);

        // Cleanup
        await _service.DeleteAsync(TemplateType.Indicator, template.Id);
    }

    [Fact]
    public async Task SaveAsync_And_GetAsync_ShouldPersistAndRetrieveColumnTemplate()
    {
        // Arrange
        var template = new ColumnTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Basic Overview",
            ColumnNames = new List<string> { "Symbol", "Name", "Close", "Volume" }
        };

        // Act
        await _service.SaveAsync(template);
        var loaded = await _service.GetAsync<ColumnTemplate>(TemplateType.Column, template.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(template.Id, loaded.Id);
        Assert.Equal("Basic Overview", loaded.Name);
        Assert.Equal(4, loaded.ColumnNames.Count);
        Assert.Equal("Volume", loaded.ColumnNames[3]);

        // Cleanup
        await _service.DeleteAsync(TemplateType.Column, template.Id);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnSavedTemplatesOrderedByFavoriteThenName()
    {
        // Arrange
        var t1 = new ColumnTemplate { Id = Guid.NewGuid(), Name = "Zebra Layout", IsFavorite = false, ColumnNames = new List<string> { "Symbol" } };
        var t2 = new ColumnTemplate { Id = Guid.NewGuid(), Name = "Alpha Layout", IsFavorite = false, ColumnNames = new List<string> { "Symbol" } };
        var t3 = new ColumnTemplate { Id = Guid.NewGuid(), Name = "Beta Layout", IsFavorite = true, ColumnNames = new List<string> { "Symbol" } };

        try
        {
            await _service.SaveAsync(t1);
            await _service.SaveAsync(t2);
            await _service.SaveAsync(t3);

            // Act
            var all = await _service.GetAllAsync<ColumnTemplate>(TemplateType.Column);

            // Assert
            Assert.True(all.Count >= 3);
            var testItems = all.Where(t => t.Id == t1.Id || t.Id == t2.Id || t.Id == t3.Id).ToList();
            Assert.Equal(3, testItems.Count);
            // Favorite should come first
            Assert.Equal(t3.Id, testItems[0].Id);
            // Non-favorites should be sorted by Name alphabetically
            Assert.Equal(t2.Id, testItems[1].Id);
            Assert.Equal(t1.Id, testItems[2].Id);
        }
        finally
        {
            await _service.DeleteAsync(TemplateType.Column, t1.Id);
            await _service.DeleteAsync(TemplateType.Column, t2.Id);
            await _service.DeleteAsync(TemplateType.Column, t3.Id);
        }
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTemplateFile()
    {
        // Arrange
        var template = new ColumnTemplate
        {
            Id = Guid.NewGuid(),
            Name = "To Be Deleted",
            ColumnNames = new List<string> { "Symbol" }
        };

        await _service.SaveAsync(template);
        var existsBefore = await _service.GetAsync<ColumnTemplate>(TemplateType.Column, template.Id);
        Assert.NotNull(existsBefore);

        // Act
        var deleted = await _service.DeleteAsync(TemplateType.Column, template.Id);

        // Assert
        Assert.True(deleted);
        var existsAfter = await _service.GetAsync<ColumnTemplate>(TemplateType.Column, template.Id);
        Assert.Null(existsAfter);
    }

    [Fact]
    public async Task ValidateAsync_ShouldCatchInvalidProperties()
    {
        // Null template
        var nullResult = await _service.ValidateAsync<IndicatorTemplate>(null!);
        Assert.False(nullResult.IsValid);

        // Empty Name
        var emptyName = new IndicatorTemplate { Name = "   " };
        var emptyNameResult = await _service.ValidateAsync(emptyName);
        Assert.False(emptyNameResult.IsValid);

        // Empty Guid
        var emptyGuid = new IndicatorTemplate { Id = Guid.Empty, Name = "Valid Name" };
        var emptyGuidResult = await _service.ValidateAsync(emptyGuid);
        Assert.False(emptyGuidResult.IsValid);

        // Valid with empty indicators (warning)
        var emptyIndicators = new IndicatorTemplate { Name = "Valid Name", Indicators = new List<CoreIndicatorSettings>() };
        var warningResult = await _service.ValidateAsync(emptyIndicators);
        Assert.True(warningResult.IsValid);
        Assert.Equal(TemplateValidationSeverity.Warning, warningResult.Severity);

        // Completely valid
        var valid = new IndicatorTemplate
        {
            Name = "Valid Template",
            Indicators = new List<CoreIndicatorSettings> { new CoreIndicatorSettings { DisplayName = "SMA" } }
        };
        var validResult = await _service.ValidateAsync(valid);
        Assert.True(validResult.IsValid);
        Assert.Equal(TemplateValidationSeverity.Valid, validResult.Severity);

        // Theme template with null colors
        var nullColorsTheme = new ThemeTemplate { Name = "Invalid Theme", Colors = null! };
        var nullColorsResult = await _service.ValidateAsync(nullColorsTheme);
        Assert.False(nullColorsResult.IsValid);
        Assert.Equal(TemplateValidationSeverity.Invalid, nullColorsResult.Severity);

        // Valid Theme template
        var validTheme = new ThemeTemplate { Name = "Valid Theme", Colors = ThemeColors.Dark };
        var validThemeResult = await _service.ValidateAsync(validTheme);
        Assert.True(validThemeResult.IsValid);
        Assert.Equal(TemplateValidationSeverity.Valid, validThemeResult.Severity);
    }

    [Fact]
    public async Task SaveAsync_And_GetAsync_ShouldPersistAndRetrieveThemeTemplate()
    {
        // Arrange
        var customColors = ThemeColors.Dark with
        {
            ChartBackground = IndicatorColor.FromUInt(0xFF223344),
            ShellAccent = IndicatorColor.FromUInt(0xFFAABBCC)
        };

        var template = new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Nordic Slate",
            Description = "Custom cold theme",
            IsFavorite = true,
            Colors = customColors
        };

        // Act
        await _service.SaveAsync(template);
        var loaded = await _service.GetAsync<ThemeTemplate>(TemplateType.Theme, template.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(template.Id, loaded.Id);
        Assert.Equal("Nordic Slate", loaded.Name);
        Assert.Equal("Custom cold theme", loaded.Description);
        Assert.True(loaded.IsFavorite);
        Assert.NotNull(loaded.Colors);
        Assert.Equal(customColors.ChartBackground, loaded.Colors.ChartBackground);
        Assert.Equal(customColors.ShellAccent, loaded.Colors.ShellAccent);

        // Cleanup
        await _service.DeleteAsync(TemplateType.Theme, template.Id);
    }

    [Fact]
    public async Task GetAllAsync_ShouldReturnSavedThemeTemplatesOrderedByFavoriteThenName()
    {
        // Arrange
        var t1 = new ThemeTemplate { Id = Guid.NewGuid(), Name = "Zebra Theme", IsFavorite = false };
        var t2 = new ThemeTemplate { Id = Guid.NewGuid(), Name = "Alpha Theme", IsFavorite = true };
        var t3 = new ThemeTemplate { Id = Guid.NewGuid(), Name = "Beta Theme", IsFavorite = false };

        await _service.SaveAsync(t1);
        await _service.SaveAsync(t2);
        await _service.SaveAsync(t3);

        // Act
        var all = await _service.GetAllAsync<ThemeTemplate>(TemplateType.Theme);

        // Assert
        Assert.NotNull(all);
        var filtered = all.Where(t => t.Id == t1.Id || t.Id == t2.Id || t.Id == t3.Id).ToList();
        Assert.Equal(3, filtered.Count);
        Assert.Equal(t2.Id, filtered[0].Id); // Favorite first
        Assert.Equal(t3.Id, filtered[1].Id); // "Beta Theme" before "Zebra Theme"
        Assert.Equal(t1.Id, filtered[2].Id);

        // Cleanup
        await _service.DeleteAsync(TemplateType.Theme, t1.Id);
        await _service.DeleteAsync(TemplateType.Theme, t2.Id);
        await _service.DeleteAsync(TemplateType.Theme, t3.Id);
    }

    [Fact]
    public async Task SaveAsync_And_GetAsync_ShouldPersistAndRetrieveFeatureSpecTemplate()
    {
        // Arrange: a Price + Indicator channel mix, exactly what the Training Wizard's
        // FeatureChannelPickerViewModel composes as one FeatureSpec (Price/Selected/Indicator
        // rows folded into a single ordered channel list per D-3).
        var template = new StockAnalyzer.Core.Models.Templates.FeatureSpecTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Close + RSI(14)",
            Spec = new StockAnalyzer.Core.Models.Training.FeatureSpec
            {
                Channels = new List<StockAnalyzer.Core.Models.Training.FeatureChannel>
                {
                    new()
                    {
                        Kind = StockAnalyzer.Core.Models.Training.FeatureChannelKind.Price,
                        Price = PriceType.Close,
                        Normalization = StockAnalyzer.Core.Models.Training.ChannelNormalization.WindowMinMax,
                    },
                    new()
                    {
                        Kind = StockAnalyzer.Core.Models.Training.FeatureChannelKind.Indicator,
                        Indicator = IndicatorType.RSI,
                        Params = new Dictionary<string, string> { ["period"] = "14" },
                        Normalization = StockAnalyzer.Core.Models.Training.ChannelNormalization.None,
                    },
                },
            },
        };

        // Act
        await _service.SaveAsync(template);
        var loaded = await _service.GetAsync<StockAnalyzer.Core.Models.Templates.FeatureSpecTemplate>(TemplateType.Feature, template.Id);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(template.Id, loaded.Id);
        Assert.Equal("Close + RSI(14)", loaded.Name);
        Assert.Equal(2, loaded.Spec.Channels.Count);
        Assert.Equal(PriceType.Close, loaded.Spec.Channels[0].Price);
        Assert.Equal(IndicatorType.RSI, loaded.Spec.Channels[1].Indicator);
        Assert.Equal("14", loaded.Spec.Channels[1].Params["period"]);

        // Cleanup
        await _service.DeleteAsync(TemplateType.Feature, template.Id);
    }

    [Fact]
    public async Task ValidateAsync_FeatureSpecTemplateWithNoChannels_ReturnsWarning()
    {
        var template = new StockAnalyzer.Core.Models.Templates.FeatureSpecTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Empty",
            Spec = new StockAnalyzer.Core.Models.Training.FeatureSpec { Channels = Array.Empty<StockAnalyzer.Core.Models.Training.FeatureChannel>() },
        };

        var result = await _service.ValidateAsync(template);

        Assert.True(result.IsValid);
        Assert.NotEmpty(result.Warnings);
    }
}
