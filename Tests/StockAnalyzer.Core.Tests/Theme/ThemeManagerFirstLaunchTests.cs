using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Theme;

/// <summary>
/// Regression coverage for the first-launch theme bug: with no user_theme.json the manager's field
/// default (Dark) made SetThemeMode(Dark) return early, so the UI variant was never applied and stayed
/// on the OS setting. Uses an isolated theme file path (never the real Data/Config).
/// </summary>
public sealed class ThemeManagerFirstLaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"theme-manager-{Guid.NewGuid():N}");
    private readonly string _themeFilePath;

    public ThemeManagerFirstLaunchTests()
    {
        Directory.CreateDirectory(_directory);
        _themeFilePath = Path.Combine(_directory, "user_theme.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task LoadAsync_WithoutThemeFile_AppliesDarkVariantOnce_AndPersistsDefault()
    {
        var dispatcher = new RecordingDispatcher();
        var manager = new ThemeManager(dispatcher, _themeFilePath);

        await manager.LoadAsync();

        Assert.Equal(new[] { AppThemeMode.Dark }, dispatcher.AppliedModes);
        Assert.Equal(AppThemeMode.Dark, manager.CurrentMode);
        await WaitForFileAsync(_themeFilePath);
        Assert.True(File.Exists(_themeFilePath));
    }

    [Fact]
    public async Task LoadAsync_WithThemeFile_AppliesThePersistedModeOnce()
    {
        // AppThemeMode.Light = 1 (persisted numerically); Colors omitted -> falls back to the Dark palette.
        await File.WriteAllTextAsync(_themeFilePath, "{ \"Mode\": 1 }");
        var dispatcher = new RecordingDispatcher();
        var manager = new ThemeManager(dispatcher, _themeFilePath);

        await manager.LoadAsync();

        Assert.Equal(new[] { AppThemeMode.Light }, dispatcher.AppliedModes);
        Assert.Equal(AppThemeMode.Light, manager.CurrentMode);
    }

    [Fact]
    public async Task LoadAsync_WithUnreadableThemeFile_AppliesVariantButNeverOverwritesTheFile()
    {
        const string corrupt = "{ this is not valid json";
        await File.WriteAllTextAsync(_themeFilePath, corrupt);
        var dispatcher = new RecordingDispatcher();
        var manager = new ThemeManager(dispatcher, _themeFilePath);

        await manager.LoadAsync();
        // A regression would persist through a fire-and-forget save; give it time to surface.
        await Task.Delay(300);

        Assert.Equal(new[] { AppThemeMode.Dark }, dispatcher.AppliedModes);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(_themeFilePath));
        Assert.False(File.Exists(_themeFilePath + ".bak"));
    }

    private static async Task WaitForFileAsync(string path)
    {
        // The default is saved by a fire-and-forget SaveAsync; poll with a generous bound.
        for (var i = 0; i < 500 && !File.Exists(path); i++)
        {
            await Task.Delay(20);
        }
    }

    private sealed class RecordingDispatcher : IThemeVariantDispatcher
    {
        public List<AppThemeMode> AppliedModes { get; } = new();

        public void ApplyTheme(AppThemeMode mode) => AppliedModes.Add(mode);

        public AppThemeMode GetActualThemeMode() => AppThemeMode.Dark;
    }
}
