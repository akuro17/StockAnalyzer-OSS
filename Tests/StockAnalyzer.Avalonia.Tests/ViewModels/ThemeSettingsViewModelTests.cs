using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class ThemeSettingsViewModelTests
{
    private readonly ThemeManager _themeManager;
    private readonly Mock<ITemplateService> _mockTemplateService;
    private readonly Mock<IDialogService> _mockDialogService;

    public ThemeSettingsViewModelTests()
    {
        _themeManager = new ThemeManager();
        _mockTemplateService = new Mock<ITemplateService>();
        _mockDialogService = new Mock<IDialogService>();

        _mockTemplateService
            .Setup(s => s.GetAllAsync<ThemeTemplate>(TemplateType.Theme))
            .ReturnsAsync(new List<ThemeTemplate>());
    }

    [Fact]
    public async Task SaveCustomThemeCommand_ShouldSaveNewTheme_WhenValidName()
    {
        // Arrange
        ThemeTemplate? savedTemplate = null;
        _mockTemplateService
            .Setup(s => s.SaveAsync(It.IsAny<ThemeTemplate>()))
            .Callback<ThemeTemplate>(t => savedTemplate = t)
            .Returns(Task.CompletedTask);

        var vm = new ThemeSettingsViewModel(_themeManager, _mockTemplateService.Object, _mockDialogService.Object);
        vm.NewThemeName = "Midnight Blue";

        // Act
        await vm.SaveCustomThemeCommand.ExecuteAsync(null);

        // Assert
        _mockTemplateService.Verify(s => s.SaveAsync(It.IsAny<ThemeTemplate>()), Times.Once);
        Assert.NotNull(savedTemplate);
        Assert.Equal("Midnight Blue", savedTemplate.Name);
        Assert.Single(vm.SavedThemes);
        Assert.Equal("Midnight Blue", vm.SavedThemes[0].Name);
        Assert.Equal(string.Empty, vm.NewThemeName);
        _mockDialogService.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveCustomThemeCommand_ShouldNoOp_WhenNameIsEmpty()
    {
        // Arrange
        var vm = new ThemeSettingsViewModel(_themeManager, _mockTemplateService.Object, _mockDialogService.Object);
        vm.NewThemeName = "   ";

        // Act
        await vm.SaveCustomThemeCommand.ExecuteAsync(null);

        // Assert
        _mockTemplateService.Verify(s => s.SaveAsync(It.IsAny<ThemeTemplate>()), Times.Never);
        Assert.Empty(vm.SavedThemes);
    }

    [Fact]
    public async Task SaveCustomThemeCommand_ShouldPromptConfirmation_AndOverwrite_WhenUserConfirms()
    {
        // Arrange
        var existingTemplate = new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Existing Theme",
            Colors = ThemeColors.Light
        };

        _mockTemplateService
            .Setup(s => s.GetAllAsync<ThemeTemplate>(TemplateType.Theme))
            .ReturnsAsync(new List<ThemeTemplate> { existingTemplate });

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true); // User confirms overwrite

        var vm = new ThemeSettingsViewModel(_themeManager, _mockTemplateService.Object, _mockDialogService.Object);
        await vm.LoadSavedThemesAsync();

        Assert.Single(vm.SavedThemes);
        vm.NewThemeName = "Existing Theme";

        // Act
        await vm.SaveCustomThemeCommand.ExecuteAsync(null);

        // Assert
        _mockDialogService.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockTemplateService.Verify(s => s.SaveAsync(It.Is<ThemeTemplate>(t => t.Id == existingTemplate.Id)), Times.Once);
        Assert.Single(vm.SavedThemes);
    }

    [Fact]
    public async Task SaveCustomThemeCommand_ShouldPromptConfirmation_AndAbort_WhenUserDeclines()
    {
        // Arrange
        var existingTemplate = new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Existing Theme",
            Colors = ThemeColors.Light
        };

        _mockTemplateService
            .Setup(s => s.GetAllAsync<ThemeTemplate>(TemplateType.Theme))
            .ReturnsAsync(new List<ThemeTemplate> { existingTemplate });

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false); // User cancels overwrite

        var vm = new ThemeSettingsViewModel(_themeManager, _mockTemplateService.Object, _mockDialogService.Object);
        await vm.LoadSavedThemesAsync();

        vm.NewThemeName = "Existing Theme";

        // Act
        await vm.SaveCustomThemeCommand.ExecuteAsync(null);

        // Assert
        _mockDialogService.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockTemplateService.Verify(s => s.SaveAsync(It.IsAny<ThemeTemplate>()), Times.Never);
        Assert.Equal("Existing Theme", vm.NewThemeName); // Name not cleared
    }

    [Fact]
    public void ApplyCustomThemeCommand_ShouldApplyColorsToThemeManager()
    {
        // Arrange
        var customColors = ThemeColors.Dark with
        {
            ChartBackground = IndicatorColor.FromUInt(0xFF334455)
        };

        var template = new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Custom Blue",
            Colors = customColors
        };

        var vm = new ThemeSettingsViewModel(_themeManager, _mockTemplateService.Object, _mockDialogService.Object);

        // Act
        vm.ApplyCustomThemeCommand.Execute(template);

        // Assert
        Assert.Equal(customColors.ChartBackground, _themeManager.CurrentTheme.ChartBackground);
        Assert.Equal(customColors.ChartBackground, vm.BackgroundColor);
        Assert.Equal(template, vm.SelectedThemeTemplate);
    }

    [Fact]
    public async Task DeleteCustomThemeCommand_ShouldPromptConfirmation_AndDeleteTemplate()
    {
        // Arrange
        var template = new ThemeTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Theme to Delete"
        };

        _mockTemplateService
            .Setup(s => s.GetAllAsync<ThemeTemplate>(TemplateType.Theme))
            .ReturnsAsync(new List<ThemeTemplate> { template });

        _mockTemplateService
            .Setup(s => s.DeleteAsync(TemplateType.Theme, template.Id))
            .ReturnsAsync(true);

        _mockDialogService
            .Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true); // User confirms delete

        var vm = new ThemeSettingsViewModel(_themeManager, _mockTemplateService.Object, _mockDialogService.Object);
        await vm.LoadSavedThemesAsync();
        Assert.Single(vm.SavedThemes);

        // Act
        await vm.DeleteCustomThemeCommand.ExecuteAsync(template);

        // Assert
        _mockDialogService.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _mockTemplateService.Verify(s => s.DeleteAsync(TemplateType.Theme, template.Id), Times.Once);
        Assert.Empty(vm.SavedThemes);
    }
}
