using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;
using Point = global::Avalonia.Point;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>T24b: the attention overlay's view model, renderer, theme token, localization and its wiring into the chart.</summary>
[Collection("LocalizationSharedState")]
public sealed class AttentionHeatmapPresentationTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly string[] Keys =
    {
        "Attention_Legend", "Attention_Tooltip", "Attention_Loading", "Attention_Failed", "Attention_Unsupported_NoSidecar",
        "Attention_Unsupported_Timeframe", "Attention_Unsupported_Ensemble", "Attention_Unsupported_ChartType",
        "Attention_Legend_Ensemble", "Attention_Tooltip_Ensemble", "Attention_Unsupported_NoAttentionMember", "Attention_Tooltip_WindowsDiffer",
    };

    private readonly string _originalLanguage = LocalizationManager.Instance.CurrentLanguage;

    public AttentionHeatmapPresentationTests() => LocalizationManager.Instance.Initialize("en");

    public void Dispose() => LocalizationManager.Instance.Initialize(_originalLanguage);

    private static AttentionRequestKey Key(string model = "first", string symbol = "X", string timeframe = "Daily", int day = 40) =>
        new(model, symbol, timeframe, new DateTime(2020, 1, 1).AddDays(day), 1);

    private static AttentionSnapshot Snapshot(AttentionRequestKey key, params float[] relative)
    {
        var times = relative.Select((_, i) => new DateTime(2020, 1, 1).AddDays(10 + (i * 20))).ToImmutableArray();
        return new AttentionSnapshot(key, 2, times, relative.Select(v => v * 0.2f).ToImmutableArray(), relative.ToImmutableArray());
    }

    // --- View model ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ViewModel_ShowsLoading_ThenReady_ForTheMatchingKeyOnly()
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        Assert.Equal(AttentionViewState.None, vm.State);
        Assert.False(vm.HasLegend);
        Assert.False(vm.HasStatus);

        var key = Key();
        vm.Begin(key);
        Assert.Equal(AttentionViewState.Loading, vm.State);
        Assert.Equal("Attention: loading…", vm.StatusText);
        Assert.Null(vm.Snapshot);

        vm.Publish(key, AttentionResult.Ready(Snapshot(key, 1f, .5f, .25f)));
        Assert.Equal(AttentionViewState.Ready, vm.State);
        Assert.True(vm.HasLegend);
        Assert.False(vm.HasStatus);
        Assert.Contains("1.0", vm.LegendText);
        Assert.Contains("2", vm.Tooltip);
        Assert.Contains("first", vm.Tooltip);
        Assert.Equal(3, vm.Snapshot!.Relative.Length);
    }

    [Fact]
    public void ViewModel_DiscardsAResultOfAnOlderRequest_AndAClearedOne()
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var older = Key(day: 39);
        var newer = Key(day: 40);
        vm.Begin(older);
        vm.Begin(newer);
        vm.Publish(older, AttentionResult.Ready(Snapshot(older, 1f, .5f)));
        Assert.Equal(AttentionViewState.Loading, vm.State);
        Assert.Null(vm.Snapshot);

        vm.Publish(newer, AttentionResult.Ready(Snapshot(older, 1f, .5f)));   // a snapshot that is not for the awaited key is refused as an error
        Assert.Equal(AttentionViewState.Failed, vm.State);

        vm.Begin(newer);
        vm.Clear();
        vm.Publish(newer, AttentionResult.Ready(Snapshot(newer, 1f, .5f)));
        Assert.Equal(AttentionViewState.None, vm.State);
        Assert.Null(vm.Snapshot);
    }

    [Fact]
    public void ViewModel_DoesNotPublishAfterDispose_AndDefersToTheDispatcher()
    {
        var queued = new List<Action>();
        var dispatcher = new Mock<IDispatcherService>();
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(queued.Add);
        var vm = new AttentionHeatmapViewModel(dispatcher.Object);
        var key = Key();
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Ready(Snapshot(key, 1f, .5f)));
        Assert.Equal(AttentionViewState.None, vm.State);            // nothing changes until the UI thread runs the posted work
        vm.Dispose();
        foreach (var action in queued) action();
        Assert.Equal(AttentionViewState.None, vm.State);
        Assert.Null(vm.Snapshot);
    }

    [Theory]
    [InlineData(AttentionContract.UnsupportedReason.NoSidecar, "Attention: this model has no attention data.")]
    [InlineData(AttentionContract.UnsupportedReason.Timeframe, "Attention: the model was trained for another timeframe.")]
    [InlineData(AttentionContract.UnsupportedReason.NoModel, "Attention: not available for an unregistered model.")]
    [InlineData(AttentionContract.UnsupportedReason.NoAttentionMember, "Attention: no member of this ensemble has attention data.")]
    public void ViewModel_UnsupportedStates_ShowTheirLocalizedReason(string reason, string expected)
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var key = Key();
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Unsupported(reason));
        Assert.Equal(AttentionViewState.Unsupported, vm.State);
        Assert.Equal(expected, vm.StatusText);
        Assert.False(vm.HasLegend);
        Assert.Null(vm.Snapshot);

        vm.MarkUnsupported(AttentionContract.UnsupportedReason.NoModel);
        Assert.Equal("Attention: not available for an unregistered model.", vm.StatusText);
        Assert.Null(vm.Pending);
    }

    [Fact]
    public void ViewModel_AFailedComputation_IsShownAsFailed_NotAsAnException()
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var key = Key();
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Failed("System.InvalidOperationException: raw detail"));
        Assert.Equal(AttentionViewState.Failed, vm.State);
        Assert.Equal("Attention: could not be computed.", vm.StatusText);
        Assert.DoesNotContain("InvalidOperation", vm.StatusText);
    }

    [Fact]
    public void ViewModel_AChartTypeWithoutATimeAxis_HidesTheLegendAndSaysSo()
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var key = Key();
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Ready(Snapshot(key, 1f, .5f)));
        Assert.True(vm.HasLegend);
        vm.SetChartType(ChartType.Renko);
        Assert.False(vm.HasLegend);
        Assert.Equal("Attention: not available for this chart type.", vm.StatusText);
        vm.SetChartType(ChartType.Candlestick);
        Assert.True(vm.HasLegend);
        Assert.False(vm.HasStatus);
    }

    [Fact]
    public void Localization_EveryAttentionKey_ResolvesInEveryLocale()
    {
        foreach (var language in new[] { "en", "ja" })
        {
            LocalizationManager.Instance.Initialize(language);
            foreach (var key in Keys)
            {
                var text = LocalizationManager.Instance.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(text) || text == "[" + key + "]", $"{key} is missing from {language}.json");
            }
        }

        Assert.Equal(Keys.Distinct().Count(), Keys.Length);
    }

    // --- Theme token ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThemeToken_IsDeclaredOnceAndUpdatableGranularly()
    {
        var theme = new ThemeManager();
        Assert.Equal(IndicatorColor.FromUInt(0x73B455D6), theme.CurrentTheme.AttentionHeat);
        Assert.NotEqual(theme.CurrentTheme.AttentionHeat.R, theme.CurrentTheme.TrainingPeriodTrain.R);   // a hue of its own
        var color = IndicatorColor.FromUInt(0xFFFF0000);
        theme.UpdateSingleColor(ThemeColorKey.AttentionHeat, color);
        Assert.Equal(color, theme.CurrentTheme.AttentionHeat);
        Assert.Equal(color, theme.GetCurrentColors()[ThemeColorKey.AttentionHeat]);
    }

    // --- Renderer ---------------------------------------------------------------------------------------------------------------------------

    private static ChartDataSnapshot ChartSnapshot(AttentionSnapshot? attention, ChartType chartType = ChartType.Candlestick, string symbol = "X", string timeframe = "Daily")
    {
        var candles = new[]
        {
            new CoreCandleData(new DateTime(2020, 1, 1), 100m, 100m, 100m, 100m, 100),
            new CoreCandleData(new DateTime(2020, 4, 10), 100m, 100m, 100m, 100m, 100),
        };
        return new ChartDataSnapshot(candles, symbol, timeframe, chartType: chartType, attention: attention);
    }

    [Fact]
    public void Renderer_DrawsFullHeightBands_ScaledByRelativeAttention_UsingXOnly()
    {
        var key = Key();
        var attention = Snapshot(key, 1f, 0.5f, 0.25f, 0f);   // bars at x = 10, 30, 50, 70
        var snapshot = ChartSnapshot(attention);
        var transform = new XOnlyTransform();
        using var bitmap = new SKBitmap(120, 60);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new AttentionHeatmapRenderer();
        var area = new Rect(10, 5, 100, 40);
        var theme = ThemeColors.Light with { AttentionHeat = IndicatorColor.FromUInt(0xFFFF0000) };
        canvas.Clear(SKColors.White);
        renderer.Render(canvas, area, snapshot, transform, theme);

        var top = bitmap.GetPixel(20, 6);          // bar 0: x = 20 on screen
        var bottom = bitmap.GetPixel(20, 44);
        Assert.Equal(new SKColor(255, 0, 0), top);                       // the most attended bar has the full base opacity (alpha 255 here)
        Assert.Equal(top, bottom);                                       // full height of the chart area
        Assert.Equal(SKColors.White, bitmap.GetPixel(20, 4));            // nothing above the area
        Assert.Equal(SKColors.White, bitmap.GetPixel(20, 46));           // nothing below it: no price transform is involved
        var half = bitmap.GetPixel(40, 20);                              // bar 1: relative 0.5
        var quarter = bitmap.GetPixel(60, 20);                           // bar 2: relative 0.25
        Assert.InRange(half.Green, 120, 135);
        Assert.InRange(quarter.Green, 185, 200);
        Assert.True(quarter.Green > half.Green);                         // weaker attention = lighter
        Assert.Equal(SKColors.White, bitmap.GetPixel(80, 20));           // relative 0 draws nothing
        Assert.Equal(SKColors.White, bitmap.GetPixel(5, 20));            // outside the area on the left
    }

    [Fact]
    public void Renderer_FollowsPanAndTheme_AndRendersNothingForAMismatch()
    {
        var key = Key();
        var attention = Snapshot(key, 1f, 1f);
        var transform = new XOnlyTransform();
        using var bitmap = new SKBitmap(120, 60);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new AttentionHeatmapRenderer();
        var area = new Rect(10, 5, 100, 40);
        var theme = ThemeColors.Light with { AttentionHeat = IndicatorColor.FromUInt(0xFFFF0000) };

        canvas.Clear(SKColors.White);
        renderer.Render(canvas, area, ChartSnapshot(attention), transform, theme);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(25, 20));
        transform.Offset = 30;                                           // the viewport moved: the bands follow the bars
        canvas.Clear(SKColors.White);
        renderer.Render(canvas, area, ChartSnapshot(attention), transform, theme);
        Assert.Equal(SKColors.White, bitmap.GetPixel(25, 20));
        Assert.Equal(SKColors.Red, bitmap.GetPixel(55, 20));
        transform.Offset = 0;
        var green = ThemeColors.Light with { AttentionHeat = IndicatorColor.FromUInt(0xFF00FF00) };
        canvas.Clear(SKColors.White);
        renderer.Render(canvas, area, ChartSnapshot(attention), transform, green);   // the theme colour is read at draw time
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(25, 20));

        foreach (var other in new[]
        {
            ChartSnapshot(attention, ChartType.Renko),                   // no time axis
            ChartSnapshot(attention, symbol: "OTHER"),
            ChartSnapshot(attention, timeframe: "Weekly"),
            ChartSnapshot(null),
        })
        {
            canvas.Clear(SKColors.White);
            renderer.Render(canvas, area, other, transform, theme);
            Assert.Equal(SKColors.White, bitmap.GetPixel(25, 20));
        }
    }

    [Fact]
    public void Renderer_HotPath_AllocatesNothing_AndUsesNoPriceTransform()
    {
        var key = Key();
        var snapshot = ChartSnapshot(Snapshot(key, 1f, .5f, .25f));
        var transform = new XOnlyTransform();     // GetYFromPrice / ChartToScreen throw: the renderer must never ask
        using var bitmap = new SKBitmap(120, 60);
        using var canvas = new SKCanvas(bitmap);
        using var renderer = new AttentionHeatmapRenderer();
        var area = new Rect(10, 5, 100, 40);
        var theme = ThemeColors.Light;
        for (int i = 0; i < 20; i++) renderer.Render(canvas, area, snapshot, transform, theme);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) renderer.Render(canvas, area, snapshot, transform, theme);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // --- Chart wiring ----------------------------------------------------------------------------------------------------------------------

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

    private static PredictionResult Prediction(string? modelId, bool fallback = false) => new(
        "Up", .5f, new[] { new ClassScore("Up", .5f), new ClassScore("Down", .3f), new ClassScore("Neutral", .2f) }, IsFallback: fallback)
    {
        ModelId = modelId,
        OutputContract = new PredictionOutputContract(PredictionOutputSemantic.ClassProbabilities, PredictionOutputUnit.Probability, 5, TimeframeType.Daily,
            ConfidenceType.ClassProbability, PredictionTargetFormula.ThresholdedSimpleReturn),
    };

    /// <summary>A result of the ensemble path: no single generation id, and the ensemble that produced it recorded on the result.</summary>
    private static PredictionResult EnsemblePrediction(EnsembleSpec produced) => Prediction(null) with { Ensemble = produced };

    private static ChartViewModel Chart(Mock<IPredictionService> prediction, IAttentionService? attention,
        IEnsembleAttentionService? ensembleAttention = null, IEnsembleSettingsManager? ensembleSettings = null) => new(new DailyData(), new DialogService(), null!, new XSettings(),
        new TimeFrameManager(new DailyData()), null!, new ThemeManager(), new MockChartSettingsManager(), new SynchronousDispatcherService(), prediction.Object, null!,
        messenger: new StrongReferenceMessenger(), attentionService: attention, ensembleAttentionService: ensembleAttention, ensembleSettings: ensembleSettings);

    private static readonly EnsembleSpec SavedEnsemble = new(Array.AsReadOnly(new[] { new EnsembleMember("first", 0.6), new EnsembleMember("second", 0.4) }));

    private static Mock<IEnsembleSettingsManager> SavedEnsembleSettings(Func<EnsembleSpec?> current)
    {
        var settings = new Mock<IEnsembleSettingsManager>();
        settings.SetupGet(s => s.Current).Returns(current);
        return settings;
    }

    private static AttentionSnapshot EnsembleSnapshot(AttentionRequestKey key, int capable, int members, params float[] relative) =>
        Snapshot(key, relative) with { CapableMembers = capable, MemberCount = members };

    [Fact]
    public async Task Chart_AnEnsemblePrediction_RequestsTheEnsembleAttentionUnderTheFingerprint_AndShowsTheMemberCount()
    {
        AttentionRequestKey? seenKey = null;
        EnsembleSpec? seenSpec = null;
        var ensembleAttention = new Mock<IEnsembleAttentionService>();
        ensembleAttention.Setup(a => a.ComputeAsync(It.IsAny<EnsembleSpec>(), It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .Returns<EnsembleSpec, AttentionRequestKey, IReadOnlyList<CandleData>, CancellationToken>((spec, key, _, _) =>
            {
                seenSpec = spec;
                seenKey = key;
                return Task.FromResult(AttentionResult.Ready(EnsembleSnapshot(key, 1, 2, 1f, .5f)));
            });
        var single = new Mock<IAttentionService>(MockBehavior.Strict);   // the single-model service must not be asked for an ensemble
        using var chart = Chart(PredictionFor(EnsemblePrediction(SavedEnsemble), () => null), single.Object, ensembleAttention.Object, SavedEnsembleSettings(() => SavedEnsemble).Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);

        Assert.Equal(AttentionViewState.Ready, chart.AttentionHeatmap.State);
        Assert.Same(SavedEnsemble, seenSpec);
        Assert.Equal(SavedEnsemble.Fingerprint(), seenKey!.ModelId);
        Assert.Equal(new DateTime(2020, 5, 1).AddDays(39), seenKey.Anchor);
        Assert.Equal(seenKey, chart.AttentionSnapshot!.Key);
        Assert.True(chart.AttentionSnapshot.IsEnsemble);
        Assert.Contains("1 of 2", chart.AttentionHeatmap.LegendText);
        Assert.Contains("1 of 2", chart.AttentionHeatmap.Tooltip);
        single.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Chart_AnEnsembleWithoutAnyAttentionMember_IsShownAsUnsupported()
    {
        var ensembleAttention = new Mock<IEnsembleAttentionService>();
        ensembleAttention.Setup(a => a.ComputeAsync(It.IsAny<EnsembleSpec>(), It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoAttentionMember));
        using var chart = Chart(PredictionFor(EnsemblePrediction(SavedEnsemble), () => null), new Mock<IAttentionService>().Object, ensembleAttention.Object,
            SavedEnsembleSettings(() => SavedEnsemble).Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);

        Assert.Equal(AttentionViewState.Unsupported, chart.AttentionHeatmap.State);
        Assert.Equal("Attention: no member of this ensemble has attention data.", chart.AttentionHeatmap.StatusText);
        Assert.Null(chart.AttentionSnapshot);
    }

    [Fact]
    public async Task Chart_DoesNotShowEnsembleAttentionOfAnEnsembleThatIsNoLongerSaved()
    {
        EnsembleSpec? saved = SavedEnsemble;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ensembleAttention = new Mock<IEnsembleAttentionService>();
        ensembleAttention.Setup(a => a.ComputeAsync(It.IsAny<EnsembleSpec>(), It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .Returns<EnsembleSpec, AttentionRequestKey, IReadOnlyList<CandleData>, CancellationToken>(async (_, key, _, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return AttentionResult.Ready(EnsembleSnapshot(key, 1, 2, 1f, .5f));
            });
        using var chart = Chart(PredictionFor(EnsemblePrediction(SavedEnsemble), () => null), new Mock<IAttentionService>().Object, ensembleAttention.Object,
            SavedEnsembleSettings(() => saved).Object);
        await chart.LoadDataAsync();
        await started.Task.WaitAsync(Deadline);
        saved = new EnsembleSpec(Array.AsReadOnly(new[] { new EnsembleMember("first", 0.5), new EnsembleMember("second", 0.5) }));   // re-weighted meanwhile
        release.SetResult(true);
        await chart.PredictionUpdateTask.WaitAsync(Deadline);

        Assert.Null(chart.AttentionSnapshot);
        Assert.NotEqual(AttentionViewState.Ready, chart.AttentionHeatmap.State);
    }

    [Theory]
    [InlineData(false)]   // another ensemble B is saved by the time the attention is requested: A's prediction must not get B's attention
    [InlineData(true)]    // the ensemble that produced the prediction is still the saved one
    public async Task Chart_AnEnsemblePrediction_RequestsTheEnsembleThatProducedIt_NotTheOneSavedAtRequestTime(bool stillSaved)
    {
        var producedByA = SavedEnsemble;
        var savedB = new EnsembleSpec(Array.AsReadOnly(new[] { new EnsembleMember("first", 0.5), new EnsembleMember("second", 0.5) }));
        EnsembleSpec? seenSpec = null;
        AttentionRequestKey? seenKey = null;
        var ensembleAttention = new Mock<IEnsembleAttentionService>();
        ensembleAttention.Setup(a => a.ComputeAsync(It.IsAny<EnsembleSpec>(), It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .Returns<EnsembleSpec, AttentionRequestKey, IReadOnlyList<CandleData>, CancellationToken>((spec, key, _, _) =>
            {
                seenSpec = spec;
                seenKey = key;
                return Task.FromResult(AttentionResult.Ready(EnsembleSnapshot(key, 1, 2, 1f, .5f)));
            });
        using var chart = Chart(PredictionFor(EnsemblePrediction(producedByA), () => null), new Mock<IAttentionService>().Object, ensembleAttention.Object,
            SavedEnsembleSettings(() => stillSaved ? producedByA : savedB).Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);

        Assert.Same(producedByA, seenSpec);                                // requested for A even though B is saved
        Assert.Equal(producedByA.Fingerprint(), seenKey!.ModelId);
        if (stillSaved)
        {
            Assert.Equal(AttentionViewState.Ready, chart.AttentionHeatmap.State);
            Assert.NotNull(chart.AttentionSnapshot);
        }
        else
        {
            Assert.Null(chart.AttentionSnapshot);                          // never B's (or A's stale) attention under A's prediction
            Assert.NotEqual(AttentionViewState.Ready, chart.AttentionHeatmap.State);
        }
    }

    [Fact]
    public async Task Chart_AResultWithoutAnEnsembleAndWithoutAModelId_IsUnsupportedEvenWhenAnEnsembleIsSaved()
    {
        var ensembleAttention = new Mock<IEnsembleAttentionService>(MockBehavior.Strict);
        using var chart = Chart(PredictionFor(Prediction(null), () => null), new Mock<IAttentionService>().Object, ensembleAttention.Object,
            SavedEnsembleSettings(() => SavedEnsemble).Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);
        Assert.Equal(AttentionViewState.Unsupported, chart.AttentionHeatmap.State);
        Assert.Equal("Attention: not available for an unregistered model.", chart.AttentionHeatmap.StatusText);   // reason no_model
        ensembleAttention.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Chart_AnEnsembleResultWithoutASavedEnsemble_StaysUnsupported()
    {
        var ensembleAttention = new Mock<IEnsembleAttentionService>(MockBehavior.Strict);
        using var chart = Chart(PredictionFor(Prediction(null), () => null), new Mock<IAttentionService>().Object, ensembleAttention.Object,
            SavedEnsembleSettings(() => null).Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);
        Assert.Equal(AttentionViewState.Unsupported, chart.AttentionHeatmap.State);
        ensembleAttention.VerifyNoOtherCalls();
    }

    [Fact]
    public void ViewModel_AnEnsembleSnapshot_ShowsTheMemberCountInLegendAndTooltip()
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var key = Key(SavedEnsemble.Fingerprint());
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Ready(EnsembleSnapshot(key, 2, 3, 1f, .5f, .25f) with { CoverageWeight = 0.7, WindowsDiffer = true }));
        Assert.Equal(AttentionViewState.Ready, vm.State);
        Assert.Equal("Ensemble attention (a weighted view, not the reason for the prediction): 2 of 3 members, weight coverage 70%", vm.LegendText);
        Assert.Contains("2 of 3", vm.Tooltip);
        Assert.Contains("weight coverage 70%", vm.Tooltip);
        Assert.DoesNotContain(SavedEnsemble.Fingerprint(), vm.Tooltip);   // the raw fingerprint is not shown on screen
    }

    private const string ReasonDisclaimer = "Attention is the model's internal reference weight; it does not prove which bars drove the prediction.";
    private const string WindowsDifferLine = "Members have different window lengths, so older bars are covered by fewer members.";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ViewModel_EnsembleTooltip_AlwaysHasTheDisclaimer_AndTheWindowsLineOnlyWhenTheWindowsDiffer(bool windowsDiffer)
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var key = Key(SavedEnsemble.Fingerprint());
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Ready(EnsembleSnapshot(key, 2, 3, 1f, .5f) with { CoverageWeight = 0.7, WindowsDiffer = windowsDiffer }));

        Assert.Contains(ReasonDisclaimer, vm.Tooltip);
        Assert.Equal(windowsDiffer, vm.Tooltip!.Contains(WindowsDifferLine));
    }

    [Fact]
    public void ViewModel_SingleModelTooltip_HasTheDisclaimer_AndNeverTheWindowsLine()
    {
        using var vm = new AttentionHeatmapViewModel(new SynchronousDispatcherService());
        var key = Key();
        vm.Begin(key);
        vm.Publish(key, AttentionResult.Ready(Snapshot(key, 1f, .5f)));

        Assert.Contains(ReasonDisclaimer, vm.Tooltip);
        Assert.DoesNotContain(WindowsDifferLine, vm.Tooltip);
    }

    private static Mock<IPredictionService> PredictionFor(PredictionResult result, Func<string?> active)
    {
        var prediction = new Mock<IPredictionService>();
        prediction.SetupGet(s => s.ActiveModelId).Returns(active);
        prediction.Setup(s => s.PredictAsync(It.IsAny<IEnumerable<CandleData>>())).ReturnsAsync(result);
        return prediction;
    }

    [Fact]
    public async Task Chart_PublishesTheAttentionOfTheDisplayedPrediction_AndClearsItOnATimeframeChange()
    {
        IReadOnlyList<CandleData>? seen = null;
        var attention = new Mock<IAttentionService>();
        attention.Setup(a => a.ComputeAsync(It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .Returns<AttentionRequestKey, IReadOnlyList<CandleData>, CancellationToken>((key, history, _) =>
            {
                seen = history;
                return Task.FromResult(AttentionResult.Ready(Snapshot(key, 1f, .5f)));
            });
        using var chart = Chart(PredictionFor(Prediction("first"), () => "first"), attention.Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);

        Assert.Equal(AttentionViewState.Ready, chart.AttentionHeatmap.State);
        var snapshot = chart.AttentionSnapshot!;
        Assert.Equal("first", snapshot.Key.ModelId);
        Assert.Equal("X", snapshot.Key.Symbol);
        Assert.Equal("Daily", snapshot.Key.Timeframe);
        Assert.Equal(new DateTime(2020, 5, 1).AddDays(39), snapshot.Key.Anchor);   // the anchor is the newest bar
        Assert.Equal(40, seen!.Count);
        chart.SelectedTimeFrame = TimeframeType.Weekly;
        Assert.Null(chart.AttentionSnapshot);
        Assert.Equal(AttentionViewState.None, chart.AttentionHeatmap.State);
    }

    [Fact]
    public async Task Chart_DoesNotShowAttentionOfAGenerationThatIsNoLongerActive()
    {
        string active = "first";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AttentionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attention = new Mock<IAttentionService>();
        attention.Setup(a => a.ComputeAsync(It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .Returns<AttentionRequestKey, IReadOnlyList<CandleData>, CancellationToken>((key, _, _) =>
            {
                started.TrySetResult();
                return release.Task.ContinueWith(t => AttentionResult.Ready(Snapshot(key, 1f, .5f)));
            });
        using var chart = Chart(PredictionFor(Prediction("first"), () => active), attention.Object);
        await chart.LoadDataAsync();
        await started.Task.WaitAsync(Deadline);
        active = "second";
        release.SetResult(AttentionResult.Failed("unused"));
        await chart.PredictionUpdateTask.WaitAsync(Deadline);
        Assert.Null(chart.AttentionSnapshot);
        Assert.NotEqual(AttentionViewState.Ready, chart.AttentionHeatmap.State);
    }

    [Theory]
    [InlineData(true, AttentionViewState.None)]      // a fallback shows nothing
    [InlineData(false, AttentionViewState.Unsupported)]   // a result without a registered generation (an ensemble) is unsupported
    public async Task Chart_FallbackAndEnsemblePredictions_NeverComputeAttention(bool fallback, AttentionViewState expected)
    {
        var attention = new Mock<IAttentionService>(MockBehavior.Strict);
        using var chart = Chart(PredictionFor(Prediction(null, fallback), () => null), attention.Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);
        Assert.Equal(expected, chart.AttentionHeatmap.State);
        Assert.Null(chart.AttentionSnapshot);
        attention.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Chart_AnAttentionFailure_DoesNotBreakThePredictionDisplay()
    {
        var attention = new Mock<IAttentionService>();
        attention.Setup(a => a.ComputeAsync(It.IsAny<AttentionRequestKey>(), It.IsAny<IReadOnlyList<CandleData>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        using var chart = Chart(PredictionFor(Prediction("first"), () => "first"), attention.Object);
        await chart.LoadDataAsync();
        await chart.PredictionUpdateTask.WaitAsync(Deadline);
        Assert.NotNull(chart.PredictionDisplay);
        Assert.Equal(AttentionViewState.Failed, chart.AttentionHeatmap.State);
        Assert.DoesNotContain("boom", chart.AttentionHeatmap.StatusText);
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
