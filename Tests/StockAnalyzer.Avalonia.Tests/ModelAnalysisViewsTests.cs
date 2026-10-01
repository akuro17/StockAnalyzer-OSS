using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

[Collection("LocalizationSharedState")]
public sealed class ModelAnalysisViewsTests
{
    [AvaloniaFact]
    public async Task DetailsView_RealizedBarsShowBothSignsAndTypedUnits_AndThemeResourcesUseCoreTokens()
    {
        string previous = LocalizationManager.Instance.CurrentLanguage;
        LocalizationManager.Instance.Initialize("en");
        var service = new Mock<IModelAnalysisService>();
        service.Setup(s => s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ModelAnalysisPresentationTests.Fixture());
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(20);
        using var vm = new AIPredictionsSettingsViewModel(settings.Object, Mock.Of<IClipboardService>(),
            Mock.Of<IToastNotificationService>(), modelAnalysisService: service.Object,
            analysisDispatcher: new SynchronousDispatcherService());
        vm.SelectedGeneration = new ModelGenerationSummary("first", "analysis_fixture.onnx", "classification",
            "daily", 5, .5, new string('a', 64), false);
        await vm.AnalysisLoadTask;
        var view = new AIPredictionsSettingsView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 1000 };
        try
        {
            typeof(StockAnalyzer.Avalonia.App).GetMethod("SyncThemeResources", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Application.Current, new object[] { ThemeColors.Light });
            var brush = Assert.IsType<SolidColorBrush>(Application.Current!.Resources["Brush.TrainingPeriod.Train"]);
            Assert.Equal(Color.FromArgb(0x26, 0x69, 0xB5, 0x8C), brush.Color);
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var text = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            Assert.Contains(text, t => t is not null && t.Contains("Validation permutation importance"));
            Assert.Contains("-2.50 pp", text);
            var bars = view.GetVisualDescendants().OfType<ProgressBar>().ToArray();
            Assert.Contains(bars, b => b.Value == 2.5 && b.FlowDirection == FlowDirection.RightToLeft && b.Maximum == 100);
            Assert.Contains(bars, b => b.Value == 5 && b.FlowDirection == FlowDirection.LeftToRight && b.Maximum == 100);
        }
        finally
        {
            window.Close();
            LocalizationManager.Instance.Initialize(previous);
        }
    }
}
