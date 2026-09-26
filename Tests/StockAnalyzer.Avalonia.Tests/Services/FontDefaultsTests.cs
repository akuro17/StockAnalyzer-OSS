using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>
/// Improvement task (FontSizeDefaults16): Detail/Helper default is 16 and comes from the single
/// <see cref="FontDefaults"/> source for every consumer.
/// </summary>
public sealed class FontDefaultsTests
{
    [Fact]
    public void DetailAndHelperDefaults_Are16()
    {
        Assert.Equal(16.0, FontDefaults.Detail);
        Assert.Equal(16.0, FontDefaults.Helper);
    }

    [Fact]
    public void NewFontSettingsManager_StartsAtTheDefaults()
    {
        var manager = new FontSettingsManager();

        Assert.Equal(FontDefaults.Base, manager.BaseFontSize);
        Assert.Equal(FontDefaults.Title, manager.TitleFontSize);
        Assert.Equal(FontDefaults.Detail, manager.DetailFontSize);
        Assert.Equal(FontDefaults.Helper, manager.HelperFontSize);
        Assert.Equal(FontDefaults.Tooltip, manager.TooltipFontSize);
    }

    [Fact]
    public void ResetToDefault_RestoresDetailAndHelperTo16()
    {
        var manager = new FontSettingsManager();
        var vm = new FontsSettingsViewModel(manager);
        vm.SelectedDetailFontSize = 20.0;
        vm.SelectedHelperFontSize = 22.0;

        vm.ResetToDefault();

        Assert.Equal(FontDefaults.Detail, vm.SelectedDetailFontSize);
        Assert.Equal(FontDefaults.Helper, vm.SelectedHelperFontSize);
        Assert.Equal(FontDefaults.Detail, manager.DetailFontSize);
        Assert.Equal(FontDefaults.Helper, manager.HelperFontSize);
    }

    // ResourceDictionary is an AvaloniaObject (UI-thread affinity), hence [AvaloniaFact].
    [AvaloniaFact]
    public void SeedDefaultResources_PublishesEverySemanticFontSizeFromTheSingleSource()
    {
        var resources = new global::Avalonia.Controls.ResourceDictionary();

        FontSettingsManager.SeedDefaultResources(resources);

        Assert.Equal(FontDefaults.Base, (double)resources["BaseFontSize"]!);
        Assert.Equal(FontDefaults.Title, (double)resources["TitleFontSize"]!);
        Assert.Equal(FontDefaults.Detail, (double)resources["DetailFontSize"]!);
        Assert.Equal(FontDefaults.Helper, (double)resources["HelperFontSize"]!);
        Assert.Equal(FontDefaults.Tooltip, (double)resources["TooltipFontSize"]!);
    }
}
