#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Screener;
using Xunit;
using Kit = StockAnalyzer.Avalonia.Tests.TestHelpers.ScatterMetricRegistrationTestKit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>The scatter metric registration window: the frame the sub-window standard asks for, the buttons' theme binding, and how the window closes.</summary>
public sealed class ScatterMetricRegistrationWindowTests
{
    /// <summary>Waits for the Cancel command that a key press or the window's close button started (it is async: it may ask for a confirmation).</summary>
    private static Task CancelSettledAsync(ScatterMetricRegistrationViewModel viewModel)
        => viewModel.CancelCommand.ExecutionTask ?? Task.CompletedTask;

    private static string AxamlText() => File.ReadAllText(Path.Combine(
        StockAnalyzer.TestSupport.TestRepositoryRoot.Find(), "StockAnalyzer.Avalonia", "Views", "Dialogs", "ScatterMetricRegistrationWindow.axaml"));

    private static async Task<(ScatterMetricRegistrationWindow Window, Kit.Harness Harness, FakeScatterMetricRegistry Registry)> ShowAsync(
        IEnumerable<ScatterMetricDefinition>? confirmed = null, bool confirmDiscard = false)
    {
        var (harness, registry) = await Kit.OpenAsync(confirmed, confirmDiscard: confirmDiscard);
        var window = new ScatterMetricRegistrationWindow();
        harness.ViewModel.RequestClose = committed =>
        {
            harness.Closed.Add(committed);
            window.CloseApproved();
        };
        window.DataContext = harness.ViewModel;
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, harness, registry);
    }

    /// <summary>Shows the catalog with an indicator chosen, so every control of the catalog mode and of the detail column is realised.</summary>
    private static void ShowCatalogWithEditor(Window window, ScatterMetricRegistrationViewModel viewModel)
    {
        Kit.Select(viewModel, "RSI");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static T ByAutomationId<T>(Window window, string id) where T : Control
        => window.GetVisualDescendants().OfType<T>().First(c => AutomationProperties.GetAutomationId(c) == id);

    // ===== The frame =====

    [AvaloniaFact]
    public async Task TheWindow_HasA56PxHeader_A64PxFooter_AndFiveBodyColumns()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                var rows = window.GetVisualDescendants().OfType<Grid>().First(g => g.RowDefinitions.Count == 3);
                Assert.Equal(56, rows.RowDefinitions[0].Height.Value);
                Assert.Equal(64, rows.RowDefinitions[2].Height.Value);
                Assert.Equal(56, rows.Children.OfType<Border>().First(b => Grid.GetRow(b) == 0).Bounds.Height, 0.5);
                Assert.Equal(64, rows.Children.OfType<Border>().First(b => Grid.GetRow(b) == 2).Bounds.Height, 0.5);

                var body = window.GetVisualDescendants().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 5);
                Assert.Equal(new[] { 220d, 260d, 450d }, new[] { 0, 2, 4 }.Select(i => body.ColumnDefinitions[i].Width.Value).ToArray());
                Assert.All(new[] { 0, 2, 4 }, i => Assert.True(body.ColumnDefinitions[i].Width.IsStar));
                Assert.All(new[] { 1, 3 }, i => Assert.True(body.ColumnDefinitions[i].Width.IsAuto));
                Assert.Equal(2, body.Children.OfType<GridSplitter>().Count());
                Assert.Equal(1100, window.Width);
                Assert.Equal(650, window.Height);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task TheFooter_ShowsOkThenCancel_RightAligned_WithTheDocumentedIdentifiers()
    {
        var (window, harness, _) = await ShowAsync(new[] { Kit.Indicator(IndicatorType.RSI) });
        using (harness)
        {
            try
            {
                var ok = ByAutomationId<Button>(window, "ScatterMetric_OkButton");
                var cancel = ByAutomationId<Button>(window, "ScatterMetric_CancelButton");

                Assert.True(ok.IsDefault);
                Assert.False(cancel.IsCancel);
                Assert.Equal(100, ok.Bounds.Width, 0.5);
                Assert.Equal(100, cancel.Bounds.Width, 0.5);
                global::Avalonia.Point okOrigin = ok.TranslatePoint(default, window)!.Value;
                global::Avalonia.Point cancelOrigin = cancel.TranslatePoint(default, window)!.Value;
                Assert.True(okOrigin.X < cancelOrigin.X, "[OK] must stand left of [Cancel]");
                Assert.Equal(12, Assert.IsType<StackPanel>(ok.Parent).Spacing);
                Assert.True(okOrigin.X > window.Bounds.Width / 2, "the buttons are on the right half");
                Assert.True(window.Bounds.Width - (cancelOrigin.X + cancel.Bounds.Width) <= 40, "[Cancel] ends at the right edge (24 px bar padding plus the button margin)");
                Assert.Equal(ok.Bounds.Height, cancel.Bounds.Height);
                Assert.True(ok.TabIndex < cancel.TabIndex);
                Assert.Equal(HorizontalAlignment.Center, ok.HorizontalContentAlignment);
                Assert.Equal(VerticalAlignment.Center, ok.VerticalContentAlignment);

                // Active mode (something is registered): the navigation and the list of the draft.
                foreach (string id in new[] { "ScatterMetric_Nav", "ScatterMetric_ActiveList" })
                {
                    Assert.Contains(window.GetVisualDescendants().OfType<Control>(), c => AutomationProperties.GetAutomationId(c) == id);
                }

                // Catalog mode with a chosen item: search, list, Offset and Add.
                ShowCatalogWithEditor(window, harness.ViewModel);
                foreach (string id in new[] { "ScatterMetric_Search", "ScatterMetric_Catalog", "ScatterMetric_Offset", "ScatterMetric_AddButton" })
                {
                    Assert.Contains(window.GetVisualDescendants().OfType<Control>(), c => AutomationProperties.GetAutomationId(c) == id);
                }
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task TheTabOrder_IsNavigation_Search_Catalog_Detail_Ok_Cancel()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                ShowCatalogWithEditor(window, harness.ViewModel);
                int Index(string id) => ByAutomationId<Control>(window, id) is InputElement element ? element.TabIndex : -1;

                int[] order =
                {
                    Index("ScatterMetric_Nav"), Index("ScatterMetric_Search"), Index("ScatterMetric_Catalog"),
                    Index("ScatterMetric_TimeFrame"), Index("ScatterMetric_Offset"), Index("ScatterMetric_AddButton"), Index("ScatterMetric_OkButton"), Index("ScatterMetric_CancelButton"),
                };
                Assert.Equal(order.OrderBy(i => i), order);
                Assert.Equal(order.Length, order.Distinct().Count());
            }
            finally { window.Close(); }
        }
    }

    // ===== Time frame box =====

    [AvaloniaFact]
    public async Task TheTimeFrameBox_ListsDayWeekMonth_AndWritesTheChoiceToTheMetric()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                ShowCatalogWithEditor(window, harness.ViewModel);
                var box = ByAutomationId<ComboBox>(window, "ScatterMetric_TimeFrame");

                Assert.Equal(3, box.ItemCount);
                Assert.Equal(TimeFrame.D1, box.SelectedValue);
                Assert.True(box.IsEnabled);
                Assert.Null(ToolTip.GetTip(box));

                box.SelectedValue = TimeFrame.MN1;
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(TimeFrame.MN1, harness.ViewModel.PendingTarget!.TimeFrame);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task TheTimeFrameBox_IsDisabledForAFundamental_AndSaysWhy()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                ShowCatalogWithEditor(window, harness.ViewModel);
                Kit.SelectColumn(harness.ViewModel, "MarketCap");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var box = ByAutomationId<ComboBox>(window, "ScatterMetric_TimeFrame");

                Assert.False(box.IsEnabled);
                Assert.Equal(TimeFrame.D1, box.SelectedValue);
                Assert.Equal("ScatterMetric_Tip_TimeFrameFixedDay", ToolTip.GetTip(box));
                Assert.True(ToolTip.GetShowOnDisabled(box));
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task TheToastOverlay_IsOneControl_BoundToTheToastService_AtTheBottomCentre()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                var overlay = Assert.Single(window.GetVisualDescendants().OfType<ToastOverlay>());

                Assert.Same(harness.ViewModel.ToastService, overlay.Service);
                Assert.Equal(VerticalAlignment.Bottom, overlay.VerticalAlignment);
                Assert.Equal(HorizontalAlignment.Center, overlay.HorizontalAlignment);
                Assert.Equal(16, overlay.Margin.Bottom);
                Assert.Equal(1000, overlay.ZIndex);
            }
            finally { window.Close(); }
        }
    }

    // ===== Buttons follow Settings > Theme > Button =====

    [AvaloniaFact]
    public async Task OkUsesThePrimaryTheme_CancelTheSecondary_AndTheTextColourIsTheButtonTextToken()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                var ok = ByAutomationId<Button>(window, "ScatterMetric_OkButton");
                var cancel = ByAutomationId<Button>(window, "ScatterMetric_CancelButton");

                Assert.True(window.TryFindResource("PrimaryButtonStyle", window.ActualThemeVariant, out object? primary));
                Assert.True(window.TryFindResource("SecondaryButtonStyle", window.ActualThemeVariant, out object? secondary));
                Assert.Same(primary, ok.Theme);
                Assert.Same(secondary, cancel.Theme);
                Assert.True(window.TryFindResource("Brush.Button.Text", window.ActualThemeVariant, out object? buttonText));
                Assert.Equal(((global::Avalonia.Media.ISolidColorBrush)buttonText!).Color, ((global::Avalonia.Media.ISolidColorBrush)ok.Foreground!).Color);
            }
            finally { window.Close(); }
        }
    }

    [Fact]
    public void TheMarkup_BindsTheButtonThemes_AndHardCodesNoColourOrGlobalButtonStyle()
    {
        string axaml = AxamlText();

        Assert.Matches(new Regex(@"Content=""\{l:Localize Btn_OK\}""[^>]*?Theme=""\{DynamicResource PrimaryButtonStyle\}""[^>]*?Foreground=""\{DynamicResource Brush\.Button\.Text\}""", RegexOptions.Singleline), axaml);
        Assert.Matches(new Regex(@"Content=""\{l:Localize Btn_Cancel\}""[^>]*?Theme=""\{DynamicResource SecondaryButtonStyle\}""", RegexOptions.Singleline), axaml);
        Assert.Matches(new Regex(@"Content=""\{l:Localize ScatterMetric_Btn_Add\}""[^>]*?Theme=""\{DynamicResource PrimaryButtonStyle\}""", RegexOptions.Singleline), axaml);
        Assert.DoesNotMatch(new Regex(@"(Background|Foreground|BorderBrush)\s*=\s*""#", RegexOptions.IgnoreCase), axaml);
        Assert.DoesNotContain("Style Selector=\"Button\"", axaml);
        Assert.DoesNotContain("$parent[Window].Close", axaml);
        Assert.DoesNotContain("IsCancel", axaml);
        // The accent colour is not a button colour: it may colour text and the header icon, never a button's background.
        Assert.DoesNotMatch(new Regex(@"<Button[^>]*Background=""\{DynamicResource Brush\.Accent\.Primary\}""", RegexOptions.Singleline), axaml);
    }

    // ===== Closing =====

    [AvaloniaFact]
    public async Task Escape_RunsTheViewModelsCancelCommand()
    {
        var (window, harness, registry) = await ShowAsync(new[] { Kit.Indicator(IndicatorType.RSI) });
        using (harness)
        {
            try
            {
                var escape = Assert.Single(window.KeyBindings, k => k.Gesture.Key == Key.Escape);
                Assert.Same(harness.ViewModel.CancelCommand, escape.Command);

                escape.Command!.Execute(null);
                await CancelSettledAsync(harness.ViewModel);

                Assert.Equal(new[] { false }, harness.Closed);
                Assert.False(window.IsVisible);
                Assert.Empty(registry.ReplaceCalls);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task EnterInTheSearchBox_IsHandled_SoItNeverReachesTheDefaultButton()
    {
        var (window, harness, _) = await ShowAsync();
        using (harness)
        {
            try
            {
                var search = ByAutomationId<TextBox>(window, "ScatterMetric_Search");
                var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };

                search.RaiseEvent(args);

                Assert.True(args.Handled);
                Assert.Empty(harness.Closed);
                Assert.True(window.IsVisible);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task TheWindowsCloseButton_RunsTheCancelRoute_AndStaysOpenUntilTheDiscardIsApproved()
    {
        var (window, harness, registry) = await ShowAsync(new[] { Kit.Indicator(IndicatorType.RSI) }, confirmDiscard: false);
        using (harness)
        {
            try
            {
                Kit.Select(harness.ViewModel, "SMA");
                harness.ViewModel.AddCommand.Execute(null);

                window.Close();
                await CancelSettledAsync(harness.ViewModel);

                Assert.True(window.IsVisible, "a refused discard keeps the window");
                Assert.Equal(1, harness.Dialog.ConfirmationCallCount);
                Assert.Empty(harness.Closed);
                Assert.Equal(2, harness.ViewModel.Draft.Count);

                harness.Dialog.ConfirmationResult = true;
                window.Close();
                await CancelSettledAsync(harness.ViewModel);

                Assert.False(window.IsVisible);
                Assert.Equal(new[] { false }, harness.Closed);
                Assert.Empty(registry.ReplaceCalls);
            }
            finally { if (window.IsVisible) window.CloseApproved(); }
        }
    }

    [AvaloniaFact]
    public async Task TheWindowsCloseButton_IsIgnoredWhileTheDraftIsBeingSaved()
    {
        var (window, harness, registry) = await ShowAsync(new[] { Kit.Indicator(IndicatorType.RSI) });
        using (harness)
        {
            try
            {
                Kit.Select(harness.ViewModel, "SMA");
                harness.ViewModel.AddCommand.Execute(null);
                registry.Gate = new TaskCompletionSource();
                Task saving = harness.ViewModel.OkCommand.ExecuteAsync(null);

                window.Close(); // the Closing handler runs synchronously and starts nothing while the draft is being saved

                Assert.True(window.IsVisible);
                Assert.Equal(0, harness.Dialog.ConfirmationCallCount);

                registry.Gate.SetResult();
                await saving; // the view model closes the window before OK completes
                Assert.False(window.IsVisible);
                Assert.Equal(new[] { true }, harness.Closed);
            }
            finally { if (window.IsVisible) window.CloseApproved(); }
        }
    }

    // ===== The detail column follows the mode =====

    [AvaloniaFact]
    public async Task TheDetailColumn_ShowsTheHintsAndTheEditor_ForTheModeAndTheSelection()
    {
        var (window, harness, _) = await ShowAsync(new[] { Kit.Indicator(IndicatorType.RSI) });
        using (harness)
        {
            try
            {
                var vm = harness.ViewModel;
                Assert.True(vm.IsActiveMode);
                Assert.True(vm.ShowActiveHint);
                Assert.False(vm.HasEditor);

                vm.SelectDraftItemCommand.Execute(vm.Draft[0]);
                Assert.True(vm.HasEditor);
                Assert.False(vm.ShowActiveHint);
                Assert.True(vm.Draft[0].IsSelected);

                vm.SelectedNavItem = vm.NavItems.First(n => n.Kind == ScatterMetricNavKind.AllFilters);
                Assert.True(vm.ShowCatalogHint);
                Assert.False(vm.HasEditor);

                Kit.Select(vm, "RSI");
                Assert.True(vm.HasEditor);
                Assert.Same(vm.PendingTarget, vm.CurrentEditor);
                Dispatcher.UIThread.RunJobs();
                var add = ByAutomationId<Button>(window, "ScatterMetric_AddButton");
                Assert.True(add.IsVisible);
                Assert.True(add.IsEffectivelyEnabled);
            }
            finally { window.Close(); }
        }
    }

    [AvaloniaFact]
    public async Task ADoubleTapOnACatalogRow_RunsTheAddCommand()
    {
        var (window, harness, registry) = await ShowAsync();
        using (harness)
        {
            try
            {
                Kit.Select(harness.ViewModel, "RSI");
                var handler = typeof(ScatterMetricRegistrationWindow).GetMethod("OnCatalogItemDoubleTapped", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

                handler.Invoke(window, new object?[] { null, null });

                Assert.Single(harness.ViewModel.Draft);
                Assert.Empty(registry.ReplaceCalls);
            }
            finally { window.Close(); }
        }
    }
}
