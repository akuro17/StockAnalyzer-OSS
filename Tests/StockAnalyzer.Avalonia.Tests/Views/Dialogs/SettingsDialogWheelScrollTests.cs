using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using StockAnalyzer.Avalonia.Behaviors;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Templates;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Locks the rollout of <see cref="WheelScrollRedirectBehavior"/> to the settings dialogs that share
/// DrawingSettingsDialog's "dense scrollable form" shape. These are structural checks on the XAML
/// wiring; the wheel-redirect mechanism itself is covered by
/// <see cref="StockAnalyzer.Avalonia.Tests.Behaviors.WheelScrollRedirectBehaviorTests"/> and
/// <c>DrawingSettingsDialogTests</c>.
/// </summary>
public class SettingsDialogWheelScrollTests
{
    [AvaloniaFact]
    public void IndicatorPropertiesDialog_RootScrollViewer_HasWheelScrollRedirectEnabled()
    {
        var indicator = DefaultCoreIndicatorSettings.GetDefault().First(s => s.TypeEnum == IndicatorType.SMA);
        indicator.IsEnabled = true;
        var vm = new IndicatorPropertiesViewModel(indicator, new StrongReferenceMessenger(), new SynchronousDispatcherService());

        var dialog = new IndicatorPropertiesDialog { DataContext = vm };
        dialog.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var scrollViewer = dialog.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(WheelScrollRedirectBehavior.GetIsEnabled(scrollViewer));
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void TrainingWizardWindow_BodyScrollViewer_HasWheelScrollRedirectEnabled()
    {
        var window = new TrainingWizardWindow { DataContext = new TrainingWizardViewModel() };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var expectedTheme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            var bodyScrollViewer = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => ReferenceEquals(sv.Theme, expectedTheme));

            Assert.True(WheelScrollRedirectBehavior.GetIsEnabled(bodyScrollViewer));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FeatureChannelPickerView_EverySidebarThemedScrollViewer_HasWheelScrollRedirectEnabled()
    {
        var vm = new FeatureChannelPickerViewModel(new IndicatorFactory());
        var view = new FeatureChannelPickerView { DataContext = vm };
        var window = new Window { Content = view, Width = 1140, Height = 640 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var expectedTheme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            var themedScrollViewers = view.GetVisualDescendants().OfType<ScrollViewer>()
                .Where(sv => ReferenceEquals(sv.Theme, expectedTheme))
                .ToList();

            Assert.Equal(3, themedScrollViewers.Count);
            Assert.All(themedScrollViewers, sv => Assert.True(WheelScrollRedirectBehavior.GetIsEnabled(sv)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void IndicatorSettingsWindow_EveryParameterFormScrollViewer_HasWheelScrollRedirectEnabled()
    {
        var mockDialogService = new Mock<IDialogService>();
        var mockToastService = new Mock<IToastNotificationService>();
        var mockTemplateService = new Mock<ITemplateService>();
        mockTemplateService
            .Setup(s => s.GetAllAsync<IndicatorTemplate>(TemplateType.Indicator))
            .ReturnsAsync(new List<IndicatorTemplate>());

        var vm = new IndicatorSettingsDialogViewModel(
            mockDialogService.Object,
            IndicatorFactory.Default,
            mockToastService.Object,
            mockTemplateService.Object,
            new Mock<IIndicatorUserDefaultService>().Object);

        var window = new IndicatorSettingsWindow { DataContext = vm };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // Active-mode right-panel parameter form + Library-mode detail column's two
            // IsVisible-switched mode ScrollViewers (SA_UI_INTERACTION.md Section 25 Adoption).
            var redirected = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Where(sv => WheelScrollRedirectBehavior.GetIsEnabled(sv))
                .ToList();

            Assert.Equal(3, redirected.Count);
        }
        finally
        {
            window.Close();
        }
    }
}
