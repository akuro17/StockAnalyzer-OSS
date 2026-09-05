using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

/// <summary>sa_implement (Notes専用「外観」設定, Y:\Temp\sa_implementation_plan_notes_appearance.md Task 2):
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

    /// <summary>sa_minimal_fix (Notes専用「外観」設定の再構成, Y:\Temp\sa_fix_plan_notes_appearance_restructure.md):
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

    /// <summary>sa_minimal_fix (URL/ハッシュタグ色トグル廃止, Y:\Temp\sa_fix_plan_notes_url_hashtag_toggle_removal.md):
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
}
