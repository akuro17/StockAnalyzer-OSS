using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Avalonia.Views.Dialogs;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Rendering;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Tests.Common;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

[Collection("MessengerSharedState")]
public sealed class IndicatorSettingsColorStyleUiTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
        _disposables.Clear();
    }

    private IndicatorSettingsDialogViewModel CreateViewModel()
    {
        var mockDialogService = new Mock<IDialogService>();
        var mockToastService = new Mock<IToastNotificationService>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService
            .Setup(s => s.GetAllAsync<IndicatorTemplate>(TemplateType.Indicator))
            .ReturnsAsync(new List<IndicatorTemplate>());

        var mockUserDefaultService = new Mock<IIndicatorUserDefaultService>();
        mockUserDefaultService
            .Setup(s => s.LoadUserDefaults())
            .Returns(new Dictionary<IndicatorType, CoreIndicatorSettings>());

        var vm = new IndicatorSettingsDialogViewModel(
            mockDialogService.Object,
            IndicatorFactory.Default,
            mockToastService.Object,
            mockTemplateService.Object,
            mockUserDefaultService.Object);
        _disposables.Add(vm);
        return vm;
    }

    private static void PumpLayout()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static IndicatorSettingsWindow MountWindow(IndicatorSettingsDialogViewModel vm, double width = 1000, double height = 700)
    {
        var window = new IndicatorSettingsWindow
        {
            DataContext = vm,
            Width = width,
            Height = height
        };
        window.Show();
        PumpLayout();
        return window;
    }

    private static CoreIndicatorSettings CreateSma(string id = "sma_ui") => new()
    {
        Id = id,
        TypeEnum = IndicatorType.SMA,
        Category = CoreIndicatorCategory.Trend,
        IsEnabled = true,
        IsOverlay = true,
        Color = new IndicatorColor(255, 0, 200, 255),
        ParameterObject = new CoreSmaParameter { Period = 20 }
    };

    private static CoreIndicatorSettings CreateMacd(string id = "macd_ui") => new()
    {
        Id = id,
        TypeEnum = IndicatorType.MACD,
        Category = CoreIndicatorCategory.Oscillator,
        IsEnabled = true,
        IsOverlay = false,
        Color = new IndicatorColor(255, 0, 200, 255),
        ParameterObject = new CoreMacdParameter(),
        SeriesColors = new ObservableCollection<SeriesColorConfig>
        {
            new() { Name = "MACD", DisplayName = "MACD Line", Color = new IndicatorColor(255, 0, 200, 255), TargetSeries = new List<string> { "Main" } },
            new() { Name = "Signal", DisplayName = "Signal Line", Color = new IndicatorColor(255, 255, 165, 0), TargetSeries = new List<string> { "Signal" } },
            new() { Name = "Histogram", DisplayName = "Histogram", Color = new IndicatorColor(255, 128, 128, 128), TargetSeries = new List<string> { "Histogram" } }
        }
    };

    [AvaloniaFact]
    public void SingleSeries_And_MultiSeries_RenderCheckBoxComboBoxColorPickerLeftToRight_AndPreserveTwoColumns()
    {
        var sma = CreateSma();
        var macd = CreateMacd();
        var vm = CreateViewModel();
        vm.Initialize(new[] { sma, macd });

        var window = MountWindow(vm, width: 1000, height: 720);
        try
        {
            // 1. Single-series (SMA)
            vm.SelectedIndicator = vm.Indicators[0];
            PumpLayout();

            var mainCheck = window.FindControl<CheckBox>("MainSeriesVisibleCheckBox");
            var mainCombo = window.FindControl<ComboBox>("MainSeriesRenderStyleComboBox");
            var mainColor = window.FindControl<ColorPicker>("MainSeriesColorPicker");

            Assert.NotNull(mainCheck);
            Assert.NotNull(mainCombo);
            Assert.NotNull(mainColor);
            Assert.True(mainCheck!.IsVisible);
            Assert.True(mainCombo!.IsVisible);
            Assert.Equal(12, mainCombo.ItemCount);
            Assert.DoesNotContain(SeriesPrimitiveType.ColorStrip, vm.SelectableRenderPrimitives);

            var mainGrid = Assert.IsType<Grid>(mainCheck.Parent);
            var mainCheckPos = mainCheck.TranslatePoint(new global::Avalonia.Point(0, 0), mainGrid)!.Value;
            var mainComboPos = mainCombo.TranslatePoint(new global::Avalonia.Point(0, 0), mainGrid)!.Value;
            var mainColorPos = mainColor!.TranslatePoint(new global::Avalonia.Point(0, 0), mainGrid)!.Value;

            Assert.True(mainCheckPos.X < mainComboPos.X, $"CheckBox X ({mainCheckPos.X}) must be left of ComboBox X ({mainComboPos.X}).");
            Assert.True(mainComboPos.X < mainColorPos.X, $"ComboBox X ({mainComboPos.X}) must be left of ColorPicker X ({mainColorPos.X}).");

            // Null MainRenderPrimitive displays effective default Line without mutating underlying MainRenderPrimitive
            Assert.Equal(SeriesPrimitiveType.Line, vm.SelectedMainRenderPrimitive);
            Assert.Null(vm.SelectedIndicator!.MainRenderPrimitive);

            // 2. Multi-series (MACD)
            vm.SelectedIndicator = vm.Indicators[1];
            PumpLayout();

            var itemsControl = window.FindControl<ItemsControl>("SeriesColorSettingsItemsControl");
            Assert.NotNull(itemsControl);
            var panel = itemsControl!.GetVisualDescendants().OfType<ResponsiveFormPanel>().Single();
            var rowStacks = panel.Children.OfType<ContentPresenter>().ToList();
            Assert.Equal(3, rowStacks.Count);

            // Verify 2-column non-collapse at 1000 px width: item 0 and item 1 share row Y, item 1 is to the right of item 0
            Assert.True(Math.Abs(rowStacks[0].Bounds.Y - rowStacks[1].Bounds.Y) < 1.0,
                $"Expected 2-column layout at 1000 px, but item0 Y={rowStacks[0].Bounds.Y} and item1 Y={rowStacks[1].Bounds.Y}.");
            Assert.True(rowStacks[1].Bounds.X > rowStacks[0].Bounds.X,
                $"Expected item1 X ({rowStacks[1].Bounds.X}) > item0 X ({rowStacks[0].Bounds.X}).");

            // Verify [CheckBox < ComboBox < ColorPicker] inside multi-series rows
            foreach (var rowPresenter in rowStacks)
            {
                var rowGrid = rowPresenter.GetVisualDescendants().OfType<Grid>().First();
                var cb = rowGrid.Children.OfType<CheckBox>().Single();
                var combo = rowGrid.Children.OfType<ComboBox>().Single();
                var cp = rowGrid.Children.OfType<ColorPicker>().Single();

                Assert.True(cb.Bounds.X < combo.Bounds.X);
                Assert.True(combo.Bounds.X < cp.Bounds.X);
                Assert.Equal(12, combo.ItemCount);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CustomStructuralIndicators_HideSeriesStyleAndVisibilityControls()
    {
        var vp = new CoreIndicatorSettings
        {
            Id = "vp_ui",
            TypeEnum = IndicatorType.VolumeProfile,
            Category = CoreIndicatorCategory.Chart,
            IsEnabled = true,
            IsOverlay = true
        };
        var mss = new CoreIndicatorSettings
        {
            Id = "mss_ui",
            TypeEnum = IndicatorType.MarketStructureShift,
            Category = CoreIndicatorCategory.Chart,
            IsEnabled = true,
            IsOverlay = true
        };

        var vm = CreateViewModel();
        vm.Initialize(new[] { vp, mss });
        var window = MountWindow(vm);
        try
        {
            vm.SelectedIndicator = vm.Indicators[0];
            PumpLayout();
            Assert.False(vm.ShowSeriesStyleAndVisibilityControls);
            Assert.False(window.FindControl<CheckBox>("MainSeriesVisibleCheckBox")!.IsVisible);
            Assert.False(window.FindControl<ComboBox>("MainSeriesRenderStyleComboBox")!.IsVisible);
            Assert.True(window.FindControl<ColorPicker>("MainSeriesColorPicker")!.IsVisible);

            vm.SelectedIndicator = vm.Indicators[1];
            PumpLayout();
            Assert.False(vm.ShowSeriesStyleAndVisibilityControls);
            Assert.False(window.FindControl<CheckBox>("MainSeriesVisibleCheckBox")!.IsVisible);
            Assert.False(window.FindControl<ComboBox>("MainSeriesRenderStyleComboBox")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void EditingVisibilityAndStyle_AppliesWithoutMathematicalVersionIncrement_AndRollsBackOnCancel()
    {
        var sma = CreateSma();
        var macd = CreateMacd();
        long smaMath = sma.MathematicalVersion;
        long macdMath = macd.MathematicalVersion;

        IEnumerable<CoreIndicatorSettings>? applied = null;
        var vm = CreateViewModel();
        vm.OnApplyCallback = items => applied = items.ToList();
        vm.Initialize(new[] { sma, macd });

        // Edit SMA (single-series)
        vm.SelectedIndicator = vm.Indicators[0];
        vm.SelectedMainSeriesVisible = false;
        vm.SelectedMainRenderPrimitive = SeriesPrimitiveType.Lollipop;

        // Edit MACD (multi-series)
        vm.SelectedIndicator = vm.Indicators[1];
        Assert.Equal(3, vm.ActiveSeriesStyleRows.Count);
        Assert.Equal(SeriesPrimitiveType.Line, vm.ActiveSeriesStyleRows[0].EffectivePrimitive);
        Assert.Equal(SeriesPrimitiveType.Histogram, vm.ActiveSeriesStyleRows[2].EffectivePrimitive);

        vm.ActiveSeriesStyleRows[1].IsVisible = false;
        vm.ActiveSeriesStyleRows[2].EffectivePrimitive = SeriesPrimitiveType.DynamicStyling;

        // Apply keeps SelectedIndicator and ActiveSeriesStyleRows intact
        vm.ApplyCommand.Execute(null);
        Assert.NotNull(applied);
        var appliedList = applied!.ToList();
        Assert.False(appliedList[0].IsMainSeriesVisible);
        Assert.Equal(SeriesPrimitiveType.Lollipop, appliedList[0].MainRenderPrimitive);
        Assert.Equal(smaMath, appliedList[0].MathematicalVersion);
        Assert.False(appliedList[1].SeriesColors[1].IsVisible);
        Assert.Equal(SeriesPrimitiveType.DynamicStyling, appliedList[1].SeriesColors[2].RenderPrimitive);
        Assert.Equal(macdMath, appliedList[1].MathematicalVersion);
        Assert.Equal(vm.Indicators[1], vm.SelectedIndicator);
        Assert.Equal(3, vm.ActiveSeriesStyleRows.Count);

        // Cancel in a fresh session leaves originals untouched
        var freshSma = CreateSma("sma_cancel");
        var vmCancel = CreateViewModel();
        bool applyCalledOnCancel = false;
        vmCancel.OnApplyCallback = _ => applyCalledOnCancel = true;
        vmCancel.Initialize(new[] { freshSma });
        vmCancel.SelectedMainSeriesVisible = false;
        vmCancel.SelectedMainRenderPrimitive = SeriesPrimitiveType.Area;
        vmCancel.CancelCommand.Execute(null);

        Assert.False(applyCalledOnCancel);
        Assert.True(freshSma.IsMainSeriesVisible);
        Assert.Null(freshSma.MainRenderPrimitive);
    }

    [Fact]
    public void LocalizationKeys_All12SelectablePrimitivesAndTooltipsResolveWithoutMissingMarkers()
    {
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;
        try
        {
            LocalizationManager.Instance.Initialize("en");
            Assert.Equal("Dot (Points)", EnumToLocalizedDisplayNameConverter.ResolveDisplayText(SeriesPrimitiveType.Dot));
            Assert.False(string.IsNullOrWhiteSpace(LocalizationManager.Instance["Tooltip_SeriesVisible"]));
            Assert.False(string.IsNullOrWhiteSpace(LocalizationManager.Instance["Tooltip_SeriesRenderStyle"]));
            foreach (var primitive in SeriesRenderStyleCatalog.SelectablePrimitives)
            {
                var text = EnumToLocalizedDisplayNameConverter.ResolveDisplayText(primitive);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("Enum_SeriesPrimitiveType_", text, StringComparison.Ordinal);
            }

            LocalizationManager.Instance.Initialize("ja");
            Assert.Equal("ドット（点）", EnumToLocalizedDisplayNameConverter.ResolveDisplayText(SeriesPrimitiveType.Dot));
            Assert.False(string.IsNullOrWhiteSpace(LocalizationManager.Instance["Tooltip_SeriesVisible"]));
            Assert.False(string.IsNullOrWhiteSpace(LocalizationManager.Instance["Tooltip_SeriesRenderStyle"]));
            foreach (var primitive in SeriesRenderStyleCatalog.SelectablePrimitives)
            {
                var text = EnumToLocalizedDisplayNameConverter.ResolveDisplayText(primitive);
                Assert.False(string.IsNullOrWhiteSpace(text));
                Assert.DoesNotContain("Enum_SeriesPrimitiveType_", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            LocalizationManager.Instance.Initialize(string.IsNullOrEmpty(previousLanguage) ? "en" : previousLanguage);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ReSelectingDefaultPrimitive_NormalizesOverrideToNull_AndResetRestoresDefault()
    {
        var volume = new CoreIndicatorSettings
        {
            Id = "vol_rev",
            TypeEnum = IndicatorType.Volume,
            Category = CoreIndicatorCategory.Volume,
            IsEnabled = true,
            IsOverlay = false,
            Color = new IndicatorColor(255, 128, 128, 128)
        };
        var macd = CreateMacd("macd_rev");

        var vm = CreateViewModel();
        vm.Initialize(new[] { volume, macd });

        // Volume defaults to Histogram with MainRenderPrimitive == null
        vm.SelectedIndicator = vm.Indicators[0];
        Assert.Equal(SeriesPrimitiveType.Histogram, vm.SelectedMainRenderPrimitive);
        Assert.Null(vm.SelectedIndicator!.MainRenderPrimitive);

        vm.SelectedMainRenderPrimitive = SeriesPrimitiveType.Area;
        Assert.Equal(SeriesPrimitiveType.Area, vm.SelectedIndicator.MainRenderPrimitive);

        // Re-selecting Volume's default Histogram normalizes MainRenderPrimitive back to null
        vm.SelectedMainRenderPrimitive = SeriesPrimitiveType.Histogram;
        Assert.Null(vm.SelectedIndicator.MainRenderPrimitive);
        Assert.Equal(SeriesPrimitiveType.Histogram, vm.SelectedMainRenderPrimitive);

        // Multi-series Signal: Line -> Dot -> Line normalizes back to null
        vm.SelectedIndicator = vm.Indicators[1];
        vm.ActiveSeriesStyleRows[1].EffectivePrimitive = SeriesPrimitiveType.Dot;
        Assert.Equal(SeriesPrimitiveType.Dot, vm.SelectedIndicator.SeriesColors[1].RenderPrimitive);
        vm.ActiveSeriesStyleRows[1].EffectivePrimitive = SeriesPrimitiveType.Line;
        Assert.Null(vm.SelectedIndicator.SeriesColors[1].RenderPrimitive);

        // Hide and override MACD, then ResetCommand restores system defaults
        vm.ActiveSeriesStyleRows[0].IsVisible = false;
        vm.ActiveSeriesStyleRows[0].EffectivePrimitive = SeriesPrimitiveType.Lollipop;
        await vm.ResetCommand.ExecuteAsync(null);
        Assert.True(vm.SelectedIndicator!.SeriesColors[0].IsVisible);
        Assert.Null(vm.SelectedIndicator.SeriesColors[0].RenderPrimitive);
    }

    [AvaloniaFact]
    public void IndicatorPropertiesDialog_RendersVolumeHistogramAndMultiSeriesControlsLeftToRight()
    {
        var volume = new CoreIndicatorSettings
        {
            Id = "props_vol",
            TypeEnum = IndicatorType.Volume,
            Category = CoreIndicatorCategory.Volume,
            IsEnabled = true,
            IsOverlay = false,
            UseUpDownColors = false,
            Color = new IndicatorColor(255, 128, 128, 128)
        };

        CoreIndicatorSettings? appliedSingle = null;
        using var volVm = new IndicatorPropertiesViewModel(
            volume,
            new StrongReferenceMessenger(),
            new SynchronousDispatcherService());
        volVm.OnApplyCallback = s => appliedSingle = s;

        var volWindow = new IndicatorPropertiesDialog
        {
            DataContext = volVm,
            Width = 520,
            Height = 650
        };
        volWindow.Show();
        PumpLayout();
        try
        {
            var cb = volWindow.FindControl<CheckBox>("MainSeriesVisibleCheckBox");
            var combo = volWindow.FindControl<ComboBox>("MainSeriesRenderStyleComboBox");
            var cp = volWindow.FindControl<ColorPicker>("MainSeriesColorPicker");

            Assert.NotNull(cb);
            Assert.NotNull(combo);
            Assert.NotNull(cp);
            Assert.True(cb!.IsVisible);
            Assert.True(combo!.IsVisible);
            Assert.Equal(12, combo.ItemCount);
            Assert.DoesNotContain(SeriesPrimitiveType.ColorStrip, volVm.SelectableRenderPrimitives);
            Assert.Equal(SeriesPrimitiveType.Histogram, volVm.EditingMainRenderPrimitive);

            var grid = Assert.IsType<Grid>(cb.Parent);
            Assert.True(cb.TranslatePoint(default, grid)!.Value.X < combo.TranslatePoint(default, grid)!.Value.X);
            Assert.True(combo.TranslatePoint(default, grid)!.Value.X < cp!.TranslatePoint(default, grid)!.Value.X);

            volVm.EditingMainRenderPrimitive = SeriesPrimitiveType.Line;
            Assert.Equal(SeriesPrimitiveType.Line, volVm.EditingSettings.MainRenderPrimitive);
            volVm.EditingMainRenderPrimitive = SeriesPrimitiveType.Histogram;
            Assert.Null(volVm.EditingSettings.MainRenderPrimitive);

            volVm.EditingSettings.UseUpDownColors = true;
            volVm.ApplyCommand.Execute(null);
            Assert.NotNull(appliedSingle);
            Assert.True(appliedSingle!.UseUpDownColors);
        }
        finally
        {
            volWindow.Close();
        }

        // Multi-series (MACD) on IndicatorPropertiesDialog
        var macd = CreateMacd("props_macd");
        using var macdVm = new IndicatorPropertiesViewModel(
            macd,
            new StrongReferenceMessenger(),
            new SynchronousDispatcherService());

        var macdWindow = new IndicatorPropertiesDialog
        {
            DataContext = macdVm,
            Width = 520,
            Height = 650
        };
        macdWindow.Show();
        PumpLayout();
        try
        {
            var itemsControl = macdWindow.FindControl<ItemsControl>("SeriesColorSettingsItemsControl");
            Assert.NotNull(itemsControl);
            var rowGrids = itemsControl!.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "MultiSeriesRowGrid").ToList();
            Assert.Equal(3, rowGrids.Count);
            foreach (var rowGrid in rowGrids)
            {
                var cb = rowGrid.Children.OfType<CheckBox>().Single();
                var combo = rowGrid.Children.OfType<ComboBox>().Single();
                var cp = rowGrid.Children.OfType<ColorPicker>().Single();
                Assert.True(cb.Bounds.X < combo.Bounds.X);
                Assert.True(combo.Bounds.X < cp.Bounds.X);
                Assert.Equal(12, combo.ItemCount);
            }
        }
        finally
        {
            macdWindow.Close();
        }
    }

    [Fact]
    public void OwnerStyleChange_NotifiesMainAndPerSeriesPrimitiveProperties_WhenOverrideIsNull()
    {
        var sma = CreateSma("style_sync_sma");
        var macd = CreateMacd("style_sync_macd");

        var settingsVm = CreateViewModel();
        settingsVm.Initialize(new[] { sma, macd });

        settingsVm.SelectedIndicator = settingsVm.Indicators[0];
        var settingsNotifications = new List<string?>();
        settingsVm.PropertyChanged += (_, e) => settingsNotifications.Add(e.PropertyName);

        settingsVm.SelectedIndicator!.Style = CoreLineStyle.Step;
        Assert.Equal(SeriesPrimitiveType.StepLine, settingsVm.SelectedMainRenderPrimitive);
        Assert.Contains(nameof(IndicatorSettingsDialogViewModel.SelectedMainRenderPrimitive), settingsNotifications);

        settingsVm.SelectedIndicator = settingsVm.Indicators[1];
        var rowNotifications = new List<string?>();
        settingsVm.ActiveSeriesStyleRows[0].PropertyChanged += (_, e) => rowNotifications.Add(e.PropertyName);
        settingsVm.SelectedIndicator!.Style = CoreLineStyle.Dash;
        Assert.Equal(SeriesPrimitiveType.DashedLine, settingsVm.ActiveSeriesStyleRows[0].EffectivePrimitive);
        Assert.Contains(nameof(ActiveSeriesStyleRow.EffectivePrimitive), rowNotifications);

        using var propsVm = new IndicatorPropertiesViewModel(
            sma,
            new StrongReferenceMessenger(),
            new SynchronousDispatcherService());
        var propsNotifications = new List<string?>();
        propsVm.PropertyChanged += (_, e) => propsNotifications.Add(e.PropertyName);
        propsVm.EditingSettings.Style = CoreLineStyle.Dot;
        Assert.Equal(SeriesPrimitiveType.DashedLine, propsVm.EditingMainRenderPrimitive);
        Assert.Contains(nameof(IndicatorPropertiesViewModel.EditingMainRenderPrimitive), propsNotifications);
    }

    [Fact]
    public void SelectableRenderPrimitives_AreSortedAlphabeticallyByLocalizedDisplayName()
    {
        var previousLanguage = LocalizationManager.Instance.CurrentLanguage;
        try
        {
            LocalizationManager.Instance.Initialize("en");
            var enVm = CreateViewModel();
            var enLabels = enVm.SelectableRenderPrimitives
                .Select(p => EnumToLocalizedDisplayNameConverter.ResolveDisplayText(p))
                .ToList();
            Assert.Equal("Area", enLabels[0]);
            Assert.Equal(enLabels.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase), enLabels);

            LocalizationManager.Instance.Initialize("ja");
            var jaVm = CreateViewModel();
            var jaLabels = jaVm.SelectableRenderPrimitives
                .Select(p => EnumToLocalizedDisplayNameConverter.ResolveDisplayText(p))
                .ToList();
            Assert.Equal(jaLabels.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase), jaLabels);
        }
        finally
        {
            LocalizationManager.Instance.Initialize(string.IsNullOrEmpty(previousLanguage) ? "en" : previousLanguage);
        }
    }
}
