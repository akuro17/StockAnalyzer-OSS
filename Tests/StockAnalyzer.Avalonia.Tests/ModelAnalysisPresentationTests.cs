using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Messaging;
using Moq;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests;

[Collection("LocalizationSharedState")]
public sealed class ModelAnalysisPresentationTests : IDisposable
{
    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;
    public ModelAnalysisPresentationTests() => LocalizationManager.Instance.Initialize("en");
    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static ModelAnalysisSnapshot Fixture(string id = "first")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "StockAnalyzer.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var json = File.ReadAllText(Path.Combine(root.FullName, "Tests", "StockAnalyzer.Core.Tests", "Assets",
            "analysis_fixture.onnx.analysis.json"));
        var value = JsonSerializer.Deserialize<ModelAnalysis>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        return new ModelAnalysisSnapshot(id, value!);
    }

    [Fact]
    public void Bands_UseXOnlyClipAndInvalidateOnPanThemeAndModel_WithNoHotPathAllocation()
    {
        var analysis = Fixture();
        var candles = new[]
        {
            new CoreCandleData(new DateTime(2020, 1, 1), 100m, 100m, 100m, 100m, 100),
            new CoreCandleData(new DateTime(2020, 4, 10), 100m, 100m, 100m, 100m, 100),
        };
        var snapshot = new ChartDataSnapshot(candles, "X", "Daily", modelAnalysis: analysis);
        var transform = new XOnlyTransform();
        using var bitmap = new SKBitmap(120, 60);
        using var canvas = new SKCanvas(bitmap);
        using var bands = new TrainingPeriodRenderer();
        var area = new Rect(10, 5, 100, 40);
        canvas.Clear(SKColors.White);
        bands.Render(canvas, area, snapshot, transform, ThemeColors.Light);
        Assert.NotEqual(SKColors.White, bitmap.GetPixel(20, 10));
        Assert.NotEqual(SKColors.White, bitmap.GetPixel(85, 10));
        Assert.NotEqual(SKColors.White, bitmap.GetPixel(105, 10));
        Assert.Equal(SKColors.White, bitmap.GetPixel(75, 10));
        Assert.Equal(SKColors.White, bitmap.GetPixel(5, 10));
        Assert.Equal(SKColors.White, bitmap.GetPixel(20, 50));
        transform.Offset = 10;
        canvas.Clear(SKColors.White);
        bands.Render(canvas, area, snapshot, transform, ThemeColors.Light);
        Assert.Equal(SKColors.White, bitmap.GetPixel(19, 10));
        Assert.NotEqual(SKColors.White, bitmap.GetPixel(21, 10));
        var custom = ThemeColors.Light with { TrainingPeriodTrain = IndicatorColor.FromUInt(0xFFFF0000) };
        bands.Render(canvas, area, snapshot, transform, custom);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(21, 10));
        for (int i = 0; i < 20; i++) bands.Render(canvas, area, snapshot, transform, custom);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) bands.Render(canvas, area, snapshot, transform, custom);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        canvas.Clear(SKColors.White);
        var mismatch = new ChartDataSnapshot(candles, "X", "Weekly", modelAnalysis: analysis);
        bands.Render(canvas, area, mismatch, transform, custom);
        Assert.Equal(SKColors.White, bitmap.GetPixel(21, 10));
        var replacement = new ChartDataSnapshot(candles, "missing", "Daily", modelAnalysis: Fixture("second"));
        bands.Render(canvas, area, replacement, transform, custom);
        Assert.Equal(SKColors.White, bitmap.GetPixel(21, 10));
    }

    [Fact]
    public void ThemeTokens_ApprovedArgbAndGranularUpdates()
    {
        var theme = new ThemeManager();
        Assert.Equal(IndicatorColor.FromUInt(0x2669B58C), theme.CurrentTheme.TrainingPeriodTrain);
        Assert.Equal(IndicatorColor.FromUInt(0x26E3AF50), theme.CurrentTheme.TrainingPeriodValidation);
        Assert.Equal(IndicatorColor.FromUInt(0x267E8CE0), theme.CurrentTheme.TrainingPeriodOos);
        var color = IndicatorColor.FromUInt(0xFFFF0000);
        theme.UpdateSingleColor(ThemeColorKey.TrainingPeriodTrain, color);
        Assert.Equal(color, theme.CurrentTheme.TrainingPeriodTrain);
        Assert.Equal(color, theme.GetCurrentColors()[ThemeColorKey.TrainingPeriodTrain]);
    }

    private static AIPredictionsSettingsViewModel Settings(IModelAnalysisService service)
    {
        var settings = new Mock<IPredictionSettingsManager>();
        settings.SetupGet(s => s.WindowSize).Returns(20);
        return new AIPredictionsSettingsViewModel(settings.Object, Mock.Of<IClipboardService>(),
            Mock.Of<IToastNotificationService>(), modelAnalysisService: service,
            analysisDispatcher: new SynchronousDispatcherService());
    }
    private static ModelGenerationSummary Generation(string id) => new(id, "model.onnx", "classification",
        "daily", 1, .5, new string('a', 64), false);

    [Fact]
    public async Task Details_IgnoreOlderSelectionCompletion_AndRetainNegativePercentagePoints()
    {
        var gate = new TaskCompletionSource<ModelAnalysisSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IModelAnalysisService>();
        service.Setup(s => s.LoadAsync("first", It.IsAny<CancellationToken>())).Returns(gate.Task);
        service.Setup(s => s.LoadAsync("second", It.IsAny<CancellationToken>())).ReturnsAsync(Fixture("second"));
        using var vm = Settings(service.Object);
        vm.SelectedGeneration = Generation("first");
        var older = vm.AnalysisLoadTask;
        vm.SelectedGeneration = Generation("second");
        await vm.AnalysisLoadTask.WaitAsync(Deadline);
        Assert.False(vm.IsAnalysisLoading);
        Assert.Equal(5, vm.ImportanceRows.Count);
        Assert.Equal(2.5, vm.ImportanceRows[0].NegativeMagnitude);
        Assert.Equal(0, vm.ImportanceRows[0].PositiveMagnitude);
        Assert.Contains("-2", vm.ImportanceRows[0].Display);
        Assert.Contains("second", vm.ImportanceProvenance);
        gate.SetResult(Fixture("first"));
        await older.WaitAsync(Deadline);
        Assert.Contains("second", vm.ImportanceProvenance);
        Assert.DoesNotContain("first", vm.ImportanceProvenance);
    }

    [Fact]
    public async Task Details_DisposeDoesNotPublishLateCompletion_AndLegacyHasNoFabricatedValues()
    {
        var gate = new TaskCompletionSource<ModelAnalysisSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IModelAnalysisService>();
        service.Setup(s => s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(gate.Task);
        var vm = Settings(service.Object);
        vm.SelectedGeneration = Generation("first");
        var loading = vm.AnalysisLoadTask;
        vm.Dispose();
        gate.SetResult(Fixture());
        await loading.WaitAsync(Deadline);
        Assert.Empty(vm.ImportanceRows);
        using var legacy = Settings(Mock.Of<IModelAnalysisService>(s =>
            s.LoadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult<ModelAnalysisSnapshot?>(null)));
        legacy.SelectedGeneration = Generation("legacy");
        await legacy.AnalysisLoadTask.WaitAsync(Deadline);
        Assert.Empty(legacy.ImportanceRows);
        Assert.NotNull(legacy.ImportanceStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chart_PublishesMatchingAnalysis_AndRejectsResultAfterActiveGenerationChanges(bool stale)
    {
        var data = new DailyData();
        string active = "first";
        var prediction = new Mock<IPredictionService>();
        prediction.SetupGet(s => s.ActiveModelId).Returns(() => active);
        prediction.Setup(s => s.PredictAsync(It.IsAny<IEnumerable<CandleData>>())).ReturnsAsync(new PredictionResult(
            "Up", .5f, new[] { new ClassScore("Up", .5f), new ClassScore("Down", .3f), new ClassScore("Neutral", .2f) })
        {
            ModelId = "first",
            OutputContract = new PredictionOutputContract(PredictionOutputSemantic.ClassProbabilities,
                PredictionOutputUnit.Probability, 5, TimeframeType.Daily, ConfidenceType.ClassProbability,
                PredictionTargetFormula.ThresholdedSimpleReturn),
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ModelAnalysisSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<IModelAnalysisService>();
        service.Setup(s => s.LoadAsync("first", It.IsAny<CancellationToken>())).Returns(() =>
        { started.SetResult(); return release.Task; });
        service.Setup(s => s.Assign(It.IsAny<ModelAnalysisSnapshot>(), It.IsAny<IReadOnlyList<CandleData>>(), TimeframeType.Daily))
            .Returns(new RegimeAssignment(1, new DateTime(2020, 5, 21)));
        using var chart = new ChartViewModel(data, new DialogService(), null!, new XSettings(),
            new TimeFrameManager(data), null!, new ThemeManager(), new MockChartSettingsManager(),
            new SynchronousDispatcherService(), prediction.Object, null!, messenger: new StrongReferenceMessenger(),
            modelAnalysisService: service.Object);
        await chart.LoadDataAsync();
        await started.Task.WaitAsync(Deadline);
        if (stale) active = "second";
        release.SetResult(Fixture());
        await chart.PredictionUpdateTask.WaitAsync(Deadline);
        if (stale)
        {
            Assert.Null(chart.ModelAnalysis); Assert.Null(chart.RegimeDisplay); Assert.False(chart.HasPeriodDisplay);
        }
        else
        {
            Assert.Equal("first", chart.ModelAnalysis!.ModelId);
            Assert.Contains("1", chart.RegimeDisplay);
            Assert.True(chart.HasPeriodDisplay);
            Assert.Contains("2020-01-01", chart.TrainPeriodTooltip);
            Assert.Contains(new string('a', 64), chart.TrainPeriodTooltip);
            chart.SelectedTimeFrame = TimeframeType.Weekly;
            Assert.Null(chart.ModelAnalysis);
        }
    }

    private sealed class DailyData : IDataService
    {
        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame frame, int count = 100)
            => Task.FromResult<IReadOnlyList<CandleData>>(Enumerable.Range(0, 40).Select(i => new CandleData(
                new DateTime(2020, 5, 1).AddDays(i), 100m, 100m, 100m, 100m, 100)).ToArray());
    }

    private sealed class XSettings : MockStockAnalyzerSettings, IStockAnalyzerSettings
    {
        string IStockAnalyzerSettings.DefaultSymbol => "X";
    }

    private sealed class XOnlyTransform : ICoordinateTransform
    {
        public double Offset { get; set; }
        public double GetXFromTime(DateTime time) => (time - new DateTime(2020, 1, 1)).TotalDays + Offset;
        public Point ChartToScreen(ChartPoint point) => throw new InvalidOperationException("Price Y conversion is forbidden.");
        public ChartPoint ScreenToChart(Point point) => throw new NotSupportedException();
        public Point NumericToScreen(double x, double y) => throw new NotSupportedException();
        public (double x, double y) ScreenToNumeric(Point point) => throw new NotSupportedException();
        public void UpdateRange(DateTime minTime, DateTime maxTime, decimal minPrice, decimal maxPrice,
            double? newCanvasWidth = null, double? newCanvasHeight = null) { }
        public void SetTimeMap(IReadOnlyList<DateTime> times) { }
        public IReadOnlyList<DateTime>? TimeMap => null;
        public double CanvasWidth => 120;
        public double CanvasHeight => 60;
        public Rect ScreenRect => new(0, 0, 120, 60);
        public double ViewportX => Offset;
        public double ViewportWidth => 100;
        public double ScaleX => 1;
        public double GetXFromIndex(double index) => index + Offset;
        public double GetYFromPrice(decimal price) => throw new InvalidOperationException("Price Y conversion is forbidden.");
        public PriceScaleType PriceScale => PriceScaleType.Linear;
        public TransformMetadata Metadata => default;
    }
}
