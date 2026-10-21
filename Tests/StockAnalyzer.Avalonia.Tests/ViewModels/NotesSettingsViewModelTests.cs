using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Notes専用「外観」設定, docs/sa_implementation_plan_notes_appearance Task 2):
/// covers NotesSettingsViewModel's new BodyFontSize/BodyTextColor properties - IsModified tracking,
/// persistence via the injected INotesSettingsManager, RevertChanges, and ResetToDefault - mirroring
/// the existing coverage pattern for sibling Notes settings (e.g. ThreadCollapseThreshold).</summary>
public class NotesSettingsViewModelTests
{
    [Fact]
    public void Constructor_InitializesFromManager()
    {
        var manager = new FakeNotesSettingsManager();
        manager.SetBodyFontSize(20.0);
        manager.SetBodyTextColor(IndicatorColor.FromRgb(0x11, 0x22, 0x33));

        var vm = new NotesSettingsViewModel(manager);

        Assert.Equal(20.0, vm.SelectedBodyFontSize);
        Assert.Equal(IndicatorColor.FromRgb(0x11, 0x22, 0x33), vm.SelectedBodyTextColor);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ChangingBodyFontSize_UpdatesManagerAndIsModified()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);

        vm.SelectedBodyFontSize = 18.0;

        Assert.True(vm.IsModified);
        Assert.Equal(18.0, manager.BodyFontSize);
    }

    [Fact]
    public void ChangingBodyTextColor_UpdatesManagerAndIsModified()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var newColor = IndicatorColor.FromRgb(0xAA, 0xBB, 0xCC);

        vm.SelectedBodyTextColor = newColor;

        Assert.True(vm.IsModified);
        Assert.Equal(newColor, manager.BodyTextColor);
    }

    [Fact]
    public void RevertChanges_RestoresBodyFontSizeAndColorToSnapshot()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var originalColor = vm.SelectedBodyTextColor;
        var originalFontSize = vm.SelectedBodyFontSize;

        vm.SelectedBodyFontSize = 22.0;
        vm.SelectedBodyTextColor = IndicatorColor.FromRgb(0x01, 0x02, 0x03);

        vm.RevertChanges();

        Assert.Equal(originalFontSize, vm.SelectedBodyFontSize);
        Assert.Equal(originalColor, vm.SelectedBodyTextColor);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void ResetToDefault_SetsBodyFontSizeAndColorToBuiltInDefaults()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        vm.SelectedBodyFontSize = 24.0;
        vm.SelectedBodyTextColor = IndicatorColor.FromRgb(0x01, 0x02, 0x03);

        vm.ResetToDefault();

        Assert.Equal(16.0, vm.SelectedBodyFontSize);
        Assert.Equal(IndicatorColor.FromUInt(0xFFE0E0E0), vm.SelectedBodyTextColor);
    }

    /// <summary>sa_minimal_fix (Notes専用「外観」設定の再構成, docs/sa_fix_plan_notes_appearance_restructure):
    /// covers the new BodyBackgroundColor property, added alongside BodyFontSize/BodyTextColor in the
    /// "Appearance" section.</summary>
    [Fact]
    public void ChangingBodyBackgroundColor_UpdatesManagerAndIsModified()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var newColor = IndicatorColor.FromRgb(0x10, 0x20, 0x30);

        vm.SelectedBodyBackgroundColor = newColor;

        Assert.True(vm.IsModified);
        Assert.Equal(newColor, manager.BodyBackgroundColor);
    }

    /// <summary>sa_minimal_fix (URL/ハッシュタグ色トグル廃止, docs/sa_fix_plan_notes_url_hashtag_toggle_removal):
    /// there is no on/off toggle - changing SelectedUrlColor/SelectedHashtagColor must always apply
    /// directly to the manager (fix request: "チェックボックスのON/OFFが不要...トグル設定は不要").</summary>
    [Fact]
    public void ChangingUrlColor_AlwaysAppliesToManager()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var newColor = IndicatorColor.FromRgb(0x99, 0x88, 0x77);

        vm.SelectedUrlColor = newColor;

        Assert.True(vm.IsModified);
        Assert.Equal(newColor, manager.UrlColor);
    }

    [Fact]
    public void ChangingHashtagColor_AlwaysAppliesToManager()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var newColor = IndicatorColor.FromRgb(0xAA, 0xBB, 0xCC);

        vm.SelectedHashtagColor = newColor;

        Assert.True(vm.IsModified);
        Assert.Equal(newColor, manager.HashtagColor);
    }

    [Fact]
    public void ChangingUpcomingCatalystsLimit_UpdatesManagerAndIsModified()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);

        vm.SelectedUpcomingCatalystsLimit = 15;

        Assert.True(vm.IsModified);
        Assert.Equal(15, manager.UpcomingCatalystsLimit);
    }

    [Fact]
    public void ChangingUpcomingCatalystsPastDaysRetention_UpdatesManagerAndIsModified()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);

        vm.SelectedUpcomingCatalystsPastDaysRetention = -3;

        Assert.True(vm.IsModified);
        Assert.Equal(-3, manager.UpcomingCatalystsPastDaysRetention);
    }

    [Fact]
    public void RevertChanges_RestoresUpcomingCatalystsSettingsToSnapshot()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);

        vm.SelectedUpcomingCatalystsLimit = 20;
        vm.SelectedUpcomingCatalystsPastDaysRetention = -5;
        Assert.True(vm.IsModified);

        vm.RevertChanges();

        Assert.False(vm.IsModified);
        Assert.Equal(7, vm.SelectedUpcomingCatalystsLimit);
        Assert.Equal(-1, vm.SelectedUpcomingCatalystsPastDaysRetention);
        Assert.Equal(7, manager.UpcomingCatalystsLimit);
        Assert.Equal(-1, manager.UpcomingCatalystsPastDaysRetention);
    }

    // ---- Highlight color (Settings > Notes > Appearance) ----

    [Fact]
    public void ChangingHighlightColor_UpdatesManagerAndIsModified()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var newColor = IndicatorColor.FromRgb(0x20, 0x60, 0xC0);

        vm.SelectedHighlightColor = newColor;

        Assert.True(vm.IsModified);
        Assert.Equal(newColor, manager.HighlightColor);
    }

    [Fact]
    public void Constructor_InitializesHighlightColorFromManager()
    {
        var manager = new FakeNotesSettingsManager();
        manager.SetHighlightColor(IndicatorColor.FromRgb(0x01, 0x02, 0x03));

        var vm = new NotesSettingsViewModel(manager);

        Assert.Equal(IndicatorColor.FromRgb(0x01, 0x02, 0x03), vm.SelectedHighlightColor);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void RevertChanges_RestoresTheHighlightColor()
    {
        var manager = new FakeNotesSettingsManager();
        var vm = new NotesSettingsViewModel(manager);
        var original = vm.SelectedHighlightColor;
        vm.SelectedHighlightColor = IndicatorColor.FromRgb(0x20, 0x60, 0xC0);

        vm.RevertChanges();

        Assert.Equal(original, vm.SelectedHighlightColor);
        Assert.Equal(original, manager.HighlightColor);
        Assert.False(vm.IsModified);
    }

    /// <summary>Reset restores the built-in highlight of the theme in effect (asked from the manager), not a fixed Dark value.</summary>
    [Fact]
    public void ResetToDefault_RestoresTheCurrentThemesHighlightColor()
    {
        var manager = new FakeNotesSettingsManager { DefaultHighlightColor = IndicatorColor.FromUInt(0xFFB3D4FF) };
        var vm = new NotesSettingsViewModel(manager);
        vm.SelectedHighlightColor = IndicatorColor.FromRgb(0x01, 0x02, 0x03);

        vm.ResetToDefault();

        Assert.Equal(IndicatorColor.FromUInt(0xFFB3D4FF), vm.SelectedHighlightColor);
        Assert.Equal(IndicatorColor.FromUInt(0xFFB3D4FF), manager.HighlightColor);
    }

    [Fact]
    public void ResetToDefault_WithTheRealManagerInTheLightTheme_UsesTheLightBlue()
    {
        var themeManager = new FakeThemeManager { CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light };
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        vm.SelectedHighlightColor = IndicatorColor.FromRgb(0x01, 0x02, 0x03);

        vm.ResetToDefault();

        Assert.Equal(IndicatorColor.FromUInt(0xFFB3D4FF), vm.SelectedHighlightColor);
    }

    /// <summary>Switching Light/Dark while the page is open: the page shows the new mode's colors straight away (it used to keep
    /// the previous mode's colors until Reset), and that is not a pending edit.</summary>
    [Fact]
    public void SwitchingTheMode_WhileThePageIsOpen_ShowsTheNewModesDefaultColors_WithoutAPendingEdit()
    {
        var themeManager = new FakeThemeManager();
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);

        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light;

        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Light.ShellBackground, vm.SelectedBodyBackgroundColor);
        Assert.Equal(manager.BodyTextColor, vm.SelectedBodyTextColor);
        Assert.Equal(manager.UrlColor, vm.SelectedUrlColor);
        Assert.Equal(manager.HashtagColor, vm.SelectedHashtagColor);
        Assert.Equal(manager.HighlightColor, vm.SelectedHighlightColor);
        Assert.False(vm.IsModified);
    }

    /// <summary>Cancelling after a mode switch must not re-apply the colors of the mode the page was opened in.</summary>
    [Fact]
    public void RevertChanges_AfterSwitchingTheMode_KeepsTheNewModesColors()
    {
        var themeManager = new FakeThemeManager();
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light;

        vm.RevertChanges();

        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);
        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Light.ShellBackground, vm.SelectedBodyBackgroundColor);
    }

    /// <summary>An unsaved pick made in one mode stays a pending edit after the mode is switched inside the same Settings dialog,
    /// and Cancel removes it (it used to survive because the baseline was replaced by the other mode's color).</summary>
    [Fact]
    public void RevertChanges_UndoesAnUnsavedPickMadeBeforeTheModeWasSwitched()
    {
        var themeManager = new FakeThemeManager();
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        var darkPick = IndicatorColor.FromRgb(0x07, 0x10, 0x20);
        vm.SelectedBodyBackgroundColor = darkPick;

        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light;
        Assert.True(vm.IsModified);

        vm.RevertChanges();
        Assert.False(vm.IsModified);
        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Dark;

        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Dark.ShellBackground, manager.BodyBackgroundColor);
        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Dark.ShellBackground, vm.SelectedBodyBackgroundColor);
    }

    /// <summary>Reset puts both modes back on the theme background, not only the mode being shown.</summary>
    [Fact]
    public void ResetToDefault_ClearsThePicksOfBothModes()
    {
        var themeManager = new FakeThemeManager();
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        manager.SetBodyBackgroundColor(IndicatorColor.FromRgb(0x07, 0x10, 0x20));
        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light;
        var vm = new NotesSettingsViewModel(manager);
        vm.SelectedBodyBackgroundColor = IndicatorColor.FromRgb(0xFA, 0xF0, 0xE6);

        vm.ResetToDefault();
        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Dark;

        foreach (var key in System.Enum.GetValues<StockAnalyzer.Core.Models.Settings.NotesColorKey>())
        {
            Assert.Equal(default, manager.GetColorPicks(key));
        }
        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Dark.ShellBackground, manager.BodyBackgroundColor);
    }

    /// <summary>The unsaved-pick rule covers every Appearance color, not only the background (owner decision D-3).</summary>
    [Fact]
    public void RevertChanges_UndoesAnUnsavedTextAndHighlightPickMadeBeforeTheModeWasSwitched()
    {
        var themeManager = new FakeThemeManager();
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        vm.SelectedBodyTextColor = IndicatorColor.FromRgb(0x07, 0x10, 0x20);
        vm.SelectedHighlightColor = IndicatorColor.FromRgb(0x30, 0x40, 0x50);

        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light;
        Assert.True(vm.IsModified);
        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Light.ShellText, vm.SelectedBodyTextColor);

        vm.RevertChanges();
        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Dark;

        Assert.False(vm.IsModified);
        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Dark.ShellText, manager.BodyTextColor);
        Assert.Equal(manager.DefaultHighlightColor, manager.HighlightColor);
    }

    [Fact]
    public void Dispose_StopsFollowingTheManager()
    {
        var themeManager = new FakeThemeManager();
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        var before = vm.SelectedBodyBackgroundColor;

        vm.Dispose();
        themeManager.CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light;

        Assert.Equal(before, vm.SelectedBodyBackgroundColor);
    }

    /// <summary>Reset restores the text, URL and hashtag colors to the default of the theme in effect, not always the Dark one.</summary>
    [Fact]
    public void ResetToDefault_InTheLightTheme_RestoresTheLightTextUrlAndHashtagColors()
    {
        var themeManager = new FakeThemeManager { CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light };
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        var picked = IndicatorColor.FromRgb(0x01, 0x02, 0x03);
        vm.SelectedBodyTextColor = picked;
        vm.SelectedUrlColor = picked;
        vm.SelectedHashtagColor = picked;

        vm.ResetToDefault();

        var lightText = StockAnalyzer.Core.Theme.ThemeColors.Light.ShellText;
        Assert.Equal(lightText, vm.SelectedBodyTextColor);
        Assert.Equal(lightText, vm.SelectedUrlColor);
        Assert.Equal(lightText, vm.SelectedHashtagColor);
        Assert.Equal(lightText, manager.BodyTextColor);
    }

    /// <summary>Reset restores the Notes background to the Theme "Background" of the theme in effect.</summary>
    [Fact]
    public void ResetToDefault_RestoresTheCurrentThemesBackground()
    {
        var themeManager = new FakeThemeManager { CurrentTheme = StockAnalyzer.Core.Theme.ThemeColors.Light };
        var manager = new StockAnalyzer.Avalonia.Services.NotesSettingsManager(themeManager);
        var vm = new NotesSettingsViewModel(manager);
        vm.SelectedBodyBackgroundColor = IndicatorColor.FromRgb(0x01, 0x02, 0x03);

        vm.ResetToDefault();

        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Light.ShellBackground, vm.SelectedBodyBackgroundColor);
        Assert.Equal(StockAnalyzer.Core.Theme.ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);
    }
}
