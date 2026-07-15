using Xunit;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Core.Models;
using System;
using System.Linq;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public class SettingsViewModelTests
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ThemeManager _themeManager;
    private readonly SettingsViewModel _sut;

    public SettingsViewModelTests()
    {
        _themeManager = new ThemeManager();
        var services = new ServiceCollection();
        services.AddSingleton<IThemeManager>(_themeManager);
        services.AddTransient<ThemeSettingsViewModel>();
        _serviceProvider = services.BuildServiceProvider();

        _sut = new SettingsViewModel(_serviceProvider);
    }

    [Fact]
    public void OnClosing_WithoutSave_RevertsChanges()
    {
        // Arrange
        var themeCategory = SettingsConstants.Categories.First(c => c.Key == SettingsConstants.Keys.Theme);
        _sut.SelectedCategory = themeCategory;
        
        var themePage = (ThemeSettingsViewModel)_sut.CurrentPage!;
        var originalColor = _themeManager.GetCurrentColors()[ThemeColorKey.Background];
        
        // Act: Modify color (Live Preview)
        var newColor = new IndicatorColor(255, 255, 0, 0); // Red
        themePage.BackgroundColor = newColor;
        
        // Verify live preview applied to manager
        Assert.Equal(newColor, _themeManager.GetCurrentColors()[ThemeColorKey.Background]);
        
        // Act: Close window without saving
        _sut.OnClosing();

        // Assert: Reverted in manager
        Assert.Equal(originalColor, _themeManager.GetCurrentColors()[ThemeColorKey.Background]);
    }

    [Fact]
    public void OnClosing_AfterSave_DoesNotRevertChanges()
    {
        // Arrange
        var themeCategory = SettingsConstants.Categories.First(c => c.Key == SettingsConstants.Keys.Theme);
        _sut.SelectedCategory = themeCategory;
        
        var themePage = (ThemeSettingsViewModel)_sut.CurrentPage!;
        var originalColor = _themeManager.GetCurrentColors()[ThemeColorKey.Background];
        var newColor = new IndicatorColor(255, 255, 0, 0); // Red
        
        try
        {
            // Act: Modify and Save
            themePage.BackgroundColor = newColor;
            _sut.SaveAllChangesCommand.Execute(null);
            
            // Act: Close window
            _sut.OnClosing();
    
            // Assert: Not reverted (persists new color)
            Assert.Equal(newColor, _themeManager.GetCurrentColors()[ThemeColorKey.Background]);
        }
        finally
        {
            // Cleanup to prevent polluting user's local user_theme.json configuration
            _themeManager.UpdateSingleColor(ThemeColorKey.Background, originalColor);
            _themeManager.SaveAsync().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Dispose_UnsubscribesFromPageEvents()
    {
        // Arrange
        var themeCategory = SettingsConstants.Categories.First(c => c.Key == SettingsConstants.Keys.Theme);
        _sut.SelectedCategory = themeCategory;
        var themePage = (ThemeSettingsViewModel)_sut.CurrentPage!;
        
        bool isModifiedFired = false;
        _sut.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsAnyModified))
                isModifiedFired = true;
        };

        // Verify initial state
        themePage.BackgroundColor = new IndicatorColor(255, 255, 255, 255);
        Assert.True(isModifiedFired);
        isModifiedFired = false;

        // Act: Dispose SUT
        _sut.Dispose();

        // Act: Modify page again
        themePage.BackgroundColor = new IndicatorColor(0, 0, 0, 255);

        // Assert: IsAnyModified should NOT fire because handler was unhooked
        Assert.False(isModifiedFired);
    }
}
