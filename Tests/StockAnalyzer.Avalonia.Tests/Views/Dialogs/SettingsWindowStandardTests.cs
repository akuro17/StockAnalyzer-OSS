using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// The Settings window against SA_UI_SUBWINDOW_STANDARD: footer order and pinning, Enter = OK (5.1),
/// a confirmation before unsaved changes are discarded (5.2), permanent scrollbars (2.3) and no hard-coded font sizes in the settings pages (3).
/// Apply is the owner's exception: it stays enabled.
/// Nothing here saves: a save would write the owner's local theme file.
/// </summary>
public sealed class SettingsWindowStandardTests
{
    private static readonly IndicatorColor Red = new(255, 255, 0, 0);

    private sealed record Rig(SettingsWindow Window, SettingsViewModel Vm, ThemeManager Theme, Mock<IDialogService> Dialogs)
    {
        public ThemeSettingsViewModel ThemePage => (ThemeSettingsViewModel)Vm.CurrentPage!;
        public void Edit() => ThemePage.BackgroundColor = Red;
    }

    private static Rig Mount(bool answer = true)
    {
        var theme = new ThemeManager();
        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(answer);
        var services = new ServiceCollection();
        services.AddSingleton<IThemeManager>(theme);
        services.AddTransient<ThemeSettingsViewModel>();
        services.AddSingleton(dialogs.Object);
        var vm = new SettingsViewModel(services.BuildServiceProvider());
        vm.SelectedCategory = SettingsConstants.Categories.First(c => c.Key == SettingsConstants.Keys.Theme);
        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return new Rig(window, vm, theme, dialogs);
    }

    private static Button FooterButton(SettingsWindow window, object command) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, command));

    [AvaloniaFact]
    public void TheFooterIsPinnedAndOrdered_ResetLeft_ThenOkCancelApplyOnTheRight()
    {
        var rig = Mount();
        try
        {
            var reset = FooterButton(rig.Window, rig.Vm.ResetCurrentPageCommand);
            var ok = FooterButton(rig.Window, rig.Vm.SaveAndCloseCommand);
            var cancel = FooterButton(rig.Window, rig.Vm.DiscardAndCloseCommand);
            var apply = FooterButton(rig.Window, rig.Vm.SaveAllChangesCommand);
            double X(Button b) => b.TranslatePoint(new global::Avalonia.Point(0, 0), rig.Window)!.Value.X;
            double Y(Button b) => b.TranslatePoint(new global::Avalonia.Point(0, 0), rig.Window)!.Value.Y;

            Assert.True(X(reset) < X(ok), "auxiliary Reset stays left");
            Assert.True(X(ok) < X(cancel) && X(cancel) < X(apply), "OK, Cancel, Apply from left to right");
            Assert.All(new[] { reset, ok, cancel, apply }, b => Assert.True(Y(b) > rig.Window.Bounds.Height - 64, "inside the 64 px footer"));
            Assert.All(new[] { ok, cancel, apply }, b => Assert.Equal(global::Avalonia.Layout.HorizontalAlignment.Center, b.HorizontalContentAlignment));
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public void ApplyStaysEnabled_WhetherOrNotAnythingIsModified()
    {
        // Owner decision: Apply is always enabled (a deliberate departure from SA_UI_SUBWINDOW_STANDARD 5.2).
        var rig = Mount();
        try
        {
            var apply = FooterButton(rig.Window, rig.Vm.SaveAllChangesCommand);
            Assert.False(rig.Vm.IsAnyModified, "start: nothing modified");
            Assert.True(rig.Vm.SaveAllChangesCommand.CanExecute(null), "start: command");
            Assert.True(apply.IsEffectivelyEnabled, "start: button");

            rig.Edit();
            Assert.True(rig.Vm.SaveAllChangesCommand.CanExecute(null), "edited: command");
            Assert.True(apply.IsEffectivelyEnabled, "edited: button");

            rig.Vm.RevertAllChangesCommand.Execute(null);
            Assert.False(rig.Vm.IsAnyModified, "reverted: nothing modified");
            Assert.True(apply.IsEffectivelyEnabled, "reverted: button");
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public void EnterIsBoundToOk_AndEscapeToTheClosePath()
    {
        var rig = Mount();
        try
        {
            var enter = Assert.Single(rig.Window.KeyBindings, k => k.Gesture.Key == Key.Enter);
            Assert.Same(rig.Vm.SaveAndCloseCommand, enter.Command);
            Assert.Single(rig.Window.KeyBindings, k => k.Gesture.Key == Key.Escape);

            enter.Command!.Execute(null); // nothing modified: OK just closes
            Dispatcher.UIThread.RunJobs();
            Assert.False(rig.Window.IsVisible);
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelWithUnsavedChanges_AsksFirst_AndANoKeepsTheWindowAndTheChanges()
    {
        var rig = Mount(answer: false);
        try
        {
            rig.Edit();
            await rig.Vm.DiscardAndCloseCommand.ExecuteAsync(null);

            rig.Dialogs.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            Assert.True(rig.Window.IsVisible);
            Assert.True(rig.Vm.IsAnyModified);
            Assert.Equal(Red, rig.Theme.GetCurrentColors()[ThemeColorKey.Background]);
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelWithUnsavedChanges_AYesRevertsAndCloses()
    {
        var rig = Mount(answer: true);
        var original = rig.Theme.GetCurrentColors()[ThemeColorKey.Background];
        try
        {
            rig.Edit();
            await rig.Vm.DiscardAndCloseCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(rig.Window.IsVisible);
            Assert.False(rig.Vm.IsAnyModified);
            Assert.Equal(original, rig.Theme.GetCurrentColors()[ThemeColorKey.Background]);
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelWithoutChanges_ClosesWithoutAsking()
    {
        var rig = Mount();
        try
        {
            await rig.Vm.DiscardAndCloseCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            rig.Dialogs.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.False(rig.Window.IsVisible);
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public void TheCloseButtonAndEscapeShareTheConfirmation_AndANoKeepsTheWindowOpen()
    {
        var rig = Mount(answer: false);
        try
        {
            rig.Edit();

            rig.Window.Close(); // what Escape and the title-bar close do
            Dispatcher.UIThread.RunJobs();

            rig.Dialogs.Verify(d => d.ShowConfirmationAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            Assert.True(rig.Window.IsVisible);
            Assert.True(rig.Vm.IsAnyModified);
        }
        finally { rig.Window.Close(); }
    }

    [AvaloniaFact]
    public void EveryScrollViewerInTheWindowKeepsAPermanentScrollbar()
    {
        var rig = Mount();
        try
        {
            var viewers = rig.Window.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
            Assert.NotEmpty(viewers);
            Assert.All(viewers, v => Assert.False(ScrollViewer.GetAllowAutoHide(v), "SA_UI_SUBWINDOW_STANDARD 2.3: no overlay / auto-hide scrollbar"));
        }
        finally { rig.Window.Close(); }
    }

    private static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockAnalyzer.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relative);
    }

    [Fact]
    public void NoSettingsPageHardCodesAFontSize_OrAnAutoHidingScrollbar_AndTheWindowUsesTheSpacingScale()
    {
        var views = Directory.GetFiles(RepoFile(Path.Combine("StockAnalyzer.Avalonia", "Views", "Dialogs")), "*SettingsView.axaml", SearchOption.AllDirectories);
        Assert.True(views.Length >= 20);
        foreach (var view in views)
        {
            var text = File.ReadAllText(view);
            Assert.False(Regex.IsMatch(text, "FontSize=\"[0-9]"), $"{Path.GetFileName(view)} hard-codes a font size (SA_UI_SUBWINDOW_STANDARD 3)");
            Assert.DoesNotContain("AllowAutoHide=\"True\"", text);
        }

        var window = File.ReadAllText(RepoFile(Path.Combine("StockAnalyzer.Avalonia", "Views", "Dialogs", "SettingsWindow.axaml")));
        // 4 / 8 / 12 / 16 / 20 / 24 (SA_UI_SUBWINDOW_STANDARD 1.1): the former 40 px gutters are gone
        Assert.Empty(Regex.Matches(window, "(Margin|Padding)=\"[^\"]*\"").Select(m => m.Value).Where(v => Regex.IsMatch(v, "\\b40\\b")));
    }
}
