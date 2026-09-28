using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class IndicatorRegistrationViewModelTemplatesTests
{
    [Fact]
    public void NavGroups_WaveTheory_IsFollowedByDividerAndTemplates()
    {
        // Arrange
        var vm = new IndicatorRegistrationViewModel();

        // Act
        var navGroups = vm.NavGroups.ToList();
        int waveTheoryIndex = navGroups.FindIndex(g =>
            !g.IsHeader && !g.IsDivider && !g.IsTemplates &&
            string.Equals(g.Group?.Name, "Wave Theory", StringComparison.OrdinalIgnoreCase));

        // Assert
        Assert.True(waveTheoryIndex >= 0, "Wave Theory group must exist in NavGroups.");
        Assert.True(waveTheoryIndex + 2 < navGroups.Count, "Wave Theory must be followed by at least two items (Divider and Templates).");

        var dividerItem = navGroups[waveTheoryIndex + 1];
        Assert.True(dividerItem.IsDivider, "Item directly after Wave Theory must be a Divider.");
        Assert.False(dividerItem.CanSelect, "Divider item must not be selectable.");

        var templatesItem = navGroups[waveTheoryIndex + 2];
        Assert.True(templatesItem.IsTemplates, "Item directly after Divider must be Templates.");
        Assert.True(templatesItem.CanSelect, "Templates item must be selectable.");
    }

    [Fact]
    public void ScreenerGroupDisplayItem_CanSelect_HeaderAndDividerAreFalse_TemplatesAndStandardAreTrue()
    {
        var header = new ScreenerGroupDisplayItem { IsHeader = true };
        var divider = new ScreenerGroupDisplayItem { IsDivider = true };
        var templates = new ScreenerGroupDisplayItem { IsTemplates = true };
        var standard = new ScreenerGroupDisplayItem { IsHeader = false, IsDivider = false, IsTemplates = false };

        Assert.False(header.CanSelect);
        Assert.False(divider.CanSelect);
        Assert.True(templates.CanSelect);
        Assert.True(standard.CanSelect);
    }

    [Fact]
    public void SelectedGroupItem_WhenTemplatesSelected_TogglesIsTemplatesSelected()
    {
        // Arrange
        var vm = new IndicatorRegistrationViewModel();
        var templatesItem = vm.NavGroups.First(g => g.IsTemplates);
        var standardItem = vm.NavGroups.First(g => g.IsStandardItem);

        // Initial state: standard item selected
        Assert.False(vm.IsTemplatesSelected);
        Assert.True(vm.IsNotTemplatesSelected);

        // Act: select templates
        vm.SelectedGroupItem = templatesItem;

        // Assert
        Assert.True(vm.IsTemplatesSelected);
        Assert.False(vm.IsNotTemplatesSelected);

        // Act: select standard item
        vm.SelectedGroupItem = standardItem;

        // Assert
        Assert.False(vm.IsTemplatesSelected);
        Assert.True(vm.IsNotTemplatesSelected);
    }

    [Fact]
    public async Task SaveTemplateCommand_PersistsEntriesAsScreenerIndicatorTemplate()
    {
        // Arrange
        var mockTemplateService = new Mock<ITemplateService>();
        var mockToast = new Mock<IToastNotificationService>();
        ScreenerIndicatorTemplate? savedTemplate = null;

        mockTemplateService
            .Setup(s => s.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener))
            .ReturnsAsync(new List<ScreenerIndicatorTemplate>());
        mockTemplateService
            .Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>()))
            .ReturnsAsync(TemplateValidationResult.Success());
        mockTemplateService
            .Setup(s => s.SaveAsync(It.IsAny<TemplateBase>()))
            .Callback<TemplateBase>(t => savedTemplate = t as ScreenerIndicatorTemplate)
            .Returns(Task.CompletedTask);

        var vm = new IndicatorRegistrationViewModel(
            templateService: mockTemplateService.Object,
            toastService: mockToast.Object);

        // Add a dummy entry to RegisteredEntries
        var entry = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.RSI,
                TimeFrame = TimeFrame.D1
            },
            Operator = ComparisonOperator.LessThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 30m,
            IsEnabled = true
        };
        vm.RegisteredEntries.Add(entry);

        vm.NewTemplateName = "Oversold RSI";

        // Act
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(savedTemplate);
        Assert.Equal("Oversold RSI", savedTemplate.Name);
        Assert.Equal(TemplateType.Screener, savedTemplate.TemplateType);
        Assert.Single(savedTemplate.Entries);
        Assert.Equal(IndicatorType.RSI, savedTemplate.Entries[0].LeftHand.IndicatorType);
        Assert.Equal(30m, savedTemplate.Entries[0].RightNumericValue);
        Assert.Empty(vm.NewTemplateName);
        Assert.Contains(savedTemplate, vm.Templates);
        Assert.Same(savedTemplate, vm.SelectedTemplate);
        Assert.Single(vm.SelectedTemplateIndicatorNames);
    }

    [Fact]
    public async Task LoadTemplateCommand_ReplacesRegisteredEntries()
    {
        // Arrange
        var mockTemplateService = new Mock<ITemplateService>();
        var mockToast = new Mock<IToastNotificationService>();

        mockTemplateService
            .Setup(s => s.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener))
            .ReturnsAsync(new List<ScreenerIndicatorTemplate>());
        mockTemplateService
            .Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>()))
            .ReturnsAsync(TemplateValidationResult.Success());

        var vm = new IndicatorRegistrationViewModel(
            templateService: mockTemplateService.Object,
            toastService: mockToast.Object);

        // Current entry
        vm.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 100m
        });

        // Template with different entry
        var template = new ScreenerIndicatorTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Template 1"
        };
        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.MACD },
                Operator = ComparisonOperator.GreaterThan,
                RightNumericValue = 0m
            }
        });

        // Act: Load template (Replace)
        await vm.LoadTemplateCommand.ExecuteAsync(template);

        // Assert: Old entry replaced with template entry
        Assert.Single(vm.RegisteredEntries);
        Assert.Equal(IndicatorType.MACD, vm.RegisteredEntries[0].LeftHand.IndicatorType);
    }

    [Fact]
    public async Task AppendTemplateCommand_AddsEntriesToExistingRegisteredEntries()
    {
        // Arrange
        var mockTemplateService = new Mock<ITemplateService>();
        var mockToast = new Mock<IToastNotificationService>();

        mockTemplateService
            .Setup(s => s.GetAllAsync<ScreenerIndicatorTemplate>(TemplateType.Screener))
            .ReturnsAsync(new List<ScreenerIndicatorTemplate>());
        mockTemplateService
            .Setup(s => s.ValidateAsync(It.IsAny<TemplateBase>()))
            .ReturnsAsync(TemplateValidationResult.Success());

        var vm = new IndicatorRegistrationViewModel(
            templateService: mockTemplateService.Object,
            toastService: mockToast.Object);

        // Current entry
        vm.RegisteredEntries.Add(new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA },
            Operator = ComparisonOperator.GreaterThan,
            RightNumericValue = 100m
        });

        // Template
        var template = new ScreenerIndicatorTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Template 1"
        };
        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.RSI },
                Operator = ComparisonOperator.LessThan,
                RightNumericValue = 30m
            }
        });

        // Act: Append template
        await vm.AppendTemplateCommand.ExecuteAsync(template);

        // Assert: Both entries present
        Assert.Equal(2, vm.RegisteredEntries.Count);
        Assert.Equal(IndicatorType.SMA, vm.RegisteredEntries[0].LeftHand.IndicatorType);
        Assert.Equal(IndicatorType.RSI, vm.RegisteredEntries[1].LeftHand.IndicatorType);
    }

    [Fact]
    public void RefreshSelectedTemplatePreview_UpdatesSelectedTemplateIndicatorNames()
    {
        // Arrange
        var vm = new IndicatorRegistrationViewModel();
        var template = new ScreenerIndicatorTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Template Preview Test"
        };
        template.SetEntries(new[]
        {
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.RSI },
                Operator = ComparisonOperator.LessThan,
                RightNumericValue = 30m
            },
            new ScreenerIndicatorEntry
            {
                LeftHand = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA },
                Operator = ComparisonOperator.GreaterThan,
                RightNumericValue = 50m
            }
        });

        // Act: Set SelectedTemplate
        vm.SelectedTemplate = template;

        // Assert
        Assert.Equal(2, vm.SelectedTemplateIndicatorNames.Count);
        Assert.Contains(vm.SelectedTemplateIndicatorNames, name => name.Contains("RSI"));
        Assert.Contains(vm.SelectedTemplateIndicatorNames, name => name.Contains("SMA"));
    }

    [Fact]
    public void ScreenerIndicatorSideConfig_Clone_WithNullParameters_CreatesEmptyDictionaryWithoutThrowing()
    {
        var config = new ScreenerIndicatorSideConfig
        {
            IndicatorType = IndicatorType.RSI,
            Parameters = null!
        };

        var clone = config.Clone();

        Assert.NotNull(clone.Parameters);
        Assert.Empty(clone.Parameters);
        Assert.Equal(IndicatorType.RSI, clone.IndicatorType);
    }

    [Fact]
    public void ScreenerIndicatorEntry_Clone_PreservesLabelAndDeepClonesConfigs()
    {
        var entry = new ScreenerIndicatorEntry
        {
            Label = "B",
            LeftHand = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.MACD,
                Parameters = new Dictionary<string, object> { ["FastPeriod"] = 12 }
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
            IsEnabled = true
        };

        var clone = entry.Clone();

        Assert.Equal("B", clone.Label);
        Assert.NotSame(entry.LeftHand, clone.LeftHand);
        Assert.Equal(IndicatorType.MACD, clone.LeftHand.IndicatorType);
        Assert.Equal(12, clone.LeftHand.Parameters["FastPeriod"]);
        Assert.NotSame(entry.LeftHand.Parameters, clone.LeftHand.Parameters);
    }

    [Fact]
    public async Task TemplateService_ValidateAsync_WithEmptyScreenerIndicatorTemplate_ReturnsWarning()
    {
        var service = new TemplateService();
        var emptyTemplate = new ScreenerIndicatorTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Empty Template"
        };

        var result = await service.ValidateAsync(emptyTemplate);

        Assert.True(result.IsValid);
        Assert.Single(result.Warnings);
        Assert.Contains("contains no entries", result.Warnings[0]);

        // Template with an entry passes cleanly with zero warnings
        emptyTemplate.SetEntries(new[] { new ScreenerIndicatorEntry() });
        var validResult = await service.ValidateAsync(emptyTemplate);
        Assert.True(validResult.IsValid);
        Assert.Empty(validResult.Warnings);
    }
}
