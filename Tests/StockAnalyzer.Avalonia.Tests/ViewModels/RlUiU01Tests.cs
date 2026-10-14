using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;
using StockAnalyzer.Avalonia.Tests.TestHelpers;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

public sealed partial class RlUiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task U01_UninitializedAndFailedSnapshotsAreNeverEmptySuccess(bool uninitialized)
    {
        using var chart = Chart(new ImmediateData());
        var registry = new EmptyRegistry { DefaultSnapshot = uninitialized, SnapshotError = uninitialized ? null : new IOException("private path") };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        await vm.InitializeAsync();
        Assert.Equal(RlDialogLoadState.Failed, vm.ModelsState); Assert.False(vm.HasModels);
        Assert.False(vm.CanActivate); Assert.DoesNotContain("private path", vm.Message);
        Assert.True(vm.HasTechnicalDetails); Assert.Contains(vm.VerificationFacts, f => f.Name == "Rl.Ui.Fact.F01.Label");
        Assert.Contains(vm.VerificationFacts, f => f.Name == "Rl.Ui.Fact.F02.Label");
        Assert.Contains(vm.VerificationFacts, f => f.Name == "Rl.Ui.Fact.F03.Label");
    }
    [Fact]
    public async Task U01_IdleInvalidCommandsCannotMutateStateOrSelectSingleRowsAutomatically()
    {
        using var chart = Chart(new ImmediateData());
        var generation = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var training = new DelayedTraining(); var registry = new EmptyRegistry { Generations = new[] { generation } };
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        await vm.InitializeAsync();
        Assert.Single(vm.ModelOptions); Assert.Null(vm.SelectedGeneration);
        vm.SelectedModelOption = vm.ModelOptions.Single(); Assert.Equal(generation, vm.SelectedGeneration);
        vm.SelectedModelOption = null; Assert.Null(vm.SelectedGeneration);
        vm.CancelCommand.Execute(null); await vm.ActivateCommand.ExecuteAsync(null);
        await vm.RecoverPointerCommand.ExecuteAsync(null); await vm.CleanupArtifactsCommand.ExecuteAsync(null);
        await vm.ResumeCheckpointCommand.ExecuteAsync(null); await vm.AbandonCheckpointCommand.ExecuteAsync(null);
        Assert.Equal("Ready", vm.Status); Assert.Equal(RlDialogOperation.Idle, vm.Operation);
        Assert.Null(training.Job); Assert.Null(registry.LastRecovered); Assert.Null(registry.LastCleanup); Assert.Null(registry.LastAbandoned);
        vm.Symbol = "SPY"; vm.StartDate = "2020-01-01"; vm.EndDateExclusive = "2020-01-01";
        Assert.False(vm.CanTrain); vm.EndDateExclusive = "2026-01-01"; Assert.True(vm.CanTrain);
        vm.SetEditorValidity(nameof(vm.InitialCapital), false); Assert.False(vm.CanTrain);
        vm.SetEditorValidity(nameof(vm.InitialCapital), true); Assert.True(vm.CanTrain);
        vm.CommissionFlat = -1m; Assert.False(vm.CanTrain); vm.CommissionFlat = 0m;
        vm.SlippageRatio = 1m; Assert.False(vm.CanTrain); vm.SlippageRatio = 0m;
        vm.UseContinuousPolicy = true;
        vm.SelectedDirectionGeneration = generation with { ModelId = new string('b', 64) }; Assert.False(vm.CanTrain);
        vm.SelectedDirectionGeneration = generation; Assert.True(vm.CanTrain);
    }
    [Fact]
    public async Task U01_DisposedPendingSnapshotCannotPublishAndUnsupportedInspectionRemainsUnsupported()
    {
        using var chart = Chart(new ImmediateData());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new EmptyRegistry { SnapshotStarted = started, SnapshotRelease = release };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        var initialize = vm.InitializeAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RlDialogLoadState.Loading, vm.ModelsState);
        Assert.False(vm.CanInspectArtifacts); Assert.False(vm.CanInspectCheckpoints); Assert.False(vm.CanInspectRecovery);
        vm.Dispose(); release.TrySetResult(); await initialize;
        Assert.Equal(RlDialogOperation.Disposed, vm.Operation); Assert.Empty(vm.Generations); Assert.False(vm.CanTrain);
        Assert.Equal(RlDialogLoadState.Unsupported, vm.ArtifactsState);
    }
    [Theory]
    [InlineData("en", "Daily")] [InlineData("ja", "\u65e5\u8db3")]
    public void FixedTimeframeRowShowsTheLocalizedDailyNameWhileTheContractKeepsItsIdentifier(string language, string expected)
    {
        string previous = LocalizationManager.Instance.CurrentLanguage;
        LocalizationManager.Instance.Initialize(language);
        try
        {
            using var chart = Chart(new ImmediateData());
            using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
            var row = Assert.Single(vm.FixedConditions, c => c.Name == LocalizationManager.Instance.Get("Rl.Ui.Field.FixedTimeframe"));
            Assert.Equal(expected, row.Value);
        }
        finally { LocalizationManager.Instance.Initialize(previous); }
        Assert.Equal(nameof(StockAnalyzer.Core.Models.TimeFrame.D1), StockAnalyzer.Core.Models.ReinforcementLearning.RlSourceContract.Acquisition["timeframe"]);
    }
    [Fact]
    public async Task InsufficientHistoryIsShownAsTheInsufficientDataStateWithGuidanceNotAsAFailure()
    {
        using var chart = Chart(new ImmediateData());
        var training = new DelayedTraining();
        using var vm = new RlTrainingDialogViewModel(training, new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.Symbol = "SPY"; vm.StartDate = "2026-01-01"; vm.EndDateExclusive = "2026-10-04";
        var run = vm.TrainCommand.ExecuteAsync(null); await training.Started.Task;
        training.Result.SetException(new InvalidDataException(StockAnalyzer.Core.Models.ReinforcementLearning.RlSourceContract.InsufficientData("complete independent RL episode support is unavailable.")));
        await run;
        Assert.Equal("InsufficientData", vm.Status); Assert.Equal("Rl.Ui.Status.InsufficientData", vm.Message);
        Assert.True(vm.HasTechnicalDetails);
    }
    [Fact]
    public async Task U01_SupersededSnapshotCannotOverwriteNewerPublishedGenerations()
    {
        using var chart = Chart(new ImmediateData());
        var stale = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var current = stale with { ModelId = new string('b', 64) };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new EmptyRegistry
        {
            SnapshotPlan = call => call == 1
                ? (ImmutableArray.Create(stale), Task.Run(() => { firstStarted.TrySetResult(); release.Task.Wait(); }))
                : (ImmutableArray.Create(current), null),
        };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        var first = vm.InitializeAsync(); await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.InitializeAsync();
        Assert.Equal(current.ModelId, Assert.Single(vm.Generations).ModelId);
        release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(current.ModelId, Assert.Single(vm.Generations).ModelId);
        Assert.Equal(RlDialogLoadState.Loaded, vm.ModelsState);
    }
    [Fact]
    public async Task U01_RevisionOverflowAndDisposedPropertyChangesCannotClearPublishedReplay()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        await vm.InitializeAsync(); await chart.ApplyRlReplayAsync(Snapshot(0, 1, -1), NullLocalizationService.Instance);
        typeof(RlTrainingDialogViewModel).GetField("_blocksRevision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, long.MaxValue);
        vm.SelectedGeneration = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        Assert.Equal(RlDialogOperation.Disposed, vm.Operation); Assert.NotNull(chart.RlPresentation);
        vm.UseContinuousPolicy = true; Assert.NotNull(chart.RlPresentation); Assert.False(vm.CanTrain);
    }
    [Fact]
    public async Task U01_NullInspectionIsFailureAndProtectedRowsSurviveEmptyCandidates()
    {
        using var chart = Chart(new ImmediateData());
        var registry = new EmptyRegistry { Capabilities = new(true, true, true), RlArtifactPolicy = new(NonactiveRetentionDays: 0, NonactiveKeepCount: 1) };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        await vm.InitializeAsync(); Assert.Equal(RlDialogLoadState.Failed, vm.RecoveryState);
        await vm.InspectArtifactsCommand.ExecuteAsync(null); Assert.Equal(RlDialogLoadState.Failed, vm.ArtifactsState);
        Assert.Equal(RlDialogOperation.Idle, vm.Operation);
        registry.ArtifactPreview = new(registry.RlArtifactPolicy!, "review", 10, 100,
            ImmutableArray.Create(new RlArtifactEntry("active", "Generation", "private path", 10, DateTimeOffset.UtcNow, "identity", ImmutableArray.Create("Active"), false)));
        await vm.InspectArtifactsCommand.ExecuteAsync(null);
        Assert.Equal(RlDialogLoadState.Loaded, vm.ArtifactsState); Assert.True(vm.HasArtifacts); Assert.Single(vm.ArtifactRows);
        Assert.False(vm.HasCleanupCandidates); vm.CleanupConfirmed = true; Assert.False(vm.CanCleanupArtifacts);
        Assert.DoesNotContain("private path", vm.ArtifactRows[0].Display);
        await vm.InspectCheckpointsCommand.ExecuteAsync(null); Assert.Equal(RlDialogLoadState.Loaded, vm.CheckpointsState);
        Assert.False(vm.HasCheckpoints); Assert.Equal(RlDialogOperation.Idle, vm.Operation);
    }
    [AvaloniaFact]
    public async Task U01_ActualNumericEditorsRejectBlankFractionalAndOverflowInsteadOfRetainedValues()
    {
        using var chart = Chart(new ImmediateData());
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), new EmptyRegistry(), new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, NullLocalizationService.Instance);
        await vm.InitializeAsync(); vm.Symbol = "SPY"; vm.StartDate = "2000-01-01"; vm.EndDateExclusive = "2026-01-01";
        var number = new NumericUpDown { Value = vm.InitialCapital, DataContext = vm };
        RlInputValidation.SetField(number, nameof(vm.InitialCapital));
        var window = new Window { Content = number }; window.Show();
        try
        {
            number.Text = vm.InitialCapital.ToString(System.Globalization.CultureInfo.CurrentCulture); Assert.True(vm.CanTrain);
            number.Text = ""; Assert.False(vm.CanTrain);
            number.Text = "999999999999999999999999999999999999"; Assert.False(vm.CanTrain);
            number.Text = "invalid"; Assert.False(vm.CanTrain);
            number.Text = vm.InitialCapital.ToString(System.Globalization.CultureInfo.CurrentCulture); Assert.True(vm.CanTrain);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData("en", false, false)] [InlineData("ja", false, false)]
    [InlineData("en", true, true)] [InlineData("ja", true, true)]
    public async Task U01_ActualWindowGroupsEmptyVisibilityFontsAndLocaleHaveValidBindings(string language, bool dark, bool largeFont)
    {
        LocalizationManager.Instance.Initialize(language);
        var fonts = new FontSettingsManager(); double size = largeFont ? 24d : FontDefaults.Base;
        fonts.SetBaseFontSize(size); fonts.SetDetailFontSize(size); fonts.SetHelperFontSize(size); fonts.SetTooltipFontSize(size);
        using var chart = Chart(new ImmediateData());
        var registry = new EmptyRegistry { Capabilities = new(true, true, true), RlArtifactPolicy = new() };
        using var vm = new RlTrainingDialogViewModel(new DelayedTraining(), registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
        await vm.InitializeAsync();
        var window = new RlTrainingWindow { DataContext = vm, Width = RlUiLayout.MinimumWidth, Height = RlUiLayout.MinimumHeight,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        window.Show();
        try
        {
            var checkpointExpander = window.GetLogicalDescendants().OfType<Expander>().First(e => !e.Classes.Contains("rl-storage") && !e.Classes.Contains("rl-maintenance") && e.GetLogicalDescendants().OfType<Button>().Any(b => b.Command == vm.InspectCheckpointsCommand));
            Assert.True(checkpointExpander.IsExpanded);
            Assert.All(window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.GetType().Name != "AccessText"), t => Assert.True(t.Classes.Contains("heading") || t.Classes.Contains("groupHeader") || t.Classes.Contains("label") || t.Classes.Contains("value") || t.Classes.Contains("diagnostic"), $"Unclassed TextBlock: '{t.Text}'"));
            foreach (var expander in window.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
            window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var headings = window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("heading")).Select(t => t.Text).ToArray();
            // S3a: Basic (G1, G2) | Advanced (Run conditions, G3) | Results and management (G4, G7, G5, G6)
            Assert.Equal(new[] { "G1", "G2", "RunConditions", "G3", "G4", "G7", "G5", "G6" }.Select(k => LocalizationManager.Instance.Get("Rl.Ui.Section." + k)), headings);
            var groupHeaders = window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("groupHeader")).ToArray();
            Assert.Equal(new[] { "TrainingWizard_Header_BasicSettings", "TrainingWizard_Header_AdvancedSettings", "Rl.Ui.Group.Results" }.Select(k => LocalizationManager.Instance.Get(k)), groupHeaders.Select(t => t.Text));
            Assert.Equal(new[] { false, true, true }, groupHeaders.Select(t => t.Classes.Contains("advancedGroupHeader")));
            foreach (var combo in window.GetLogicalDescendants().OfType<ComboBox>()) Assert.False(combo.IsVisible);
            Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Command == vm.TrainCommand).IsEnabled);
            Assert.True(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Command == vm.InspectArtifactsCommand).IsVisible);
            Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Command == vm.CancelCommand).IsVisible);
            Assert.False(window.GetLogicalDescendants().OfType<Button>().Single(b => b.Command == vm.PauseCommand).IsVisible);
            Assert.Equal(size, window.GetLogicalDescendants().OfType<NumericUpDown>().First().FontSize);
            Assert.All(window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Text is not null), t => Assert.DoesNotContain("[Rl.", t.Text!));
            Assert.Equal(0, window.GetLogicalDescendants().OfType<ScrollViewer>().First().ScrollBarMaximum.X);
            var firstFact = vm.VerificationFacts[0].Name;
            LocalizationManager.Instance.Initialize(language == "en" ? "ja" : "en"); Assert.NotEqual(firstFact, vm.VerificationFacts[0].Name);
        }
        finally
        {
            window.Close(); fonts.SetBaseFontSize(FontDefaults.Base); fonts.SetDetailFontSize(FontDefaults.Detail);
            fonts.SetHelperFontSize(FontDefaults.Helper); fonts.SetTooltipFontSize(FontDefaults.Tooltip);
        }
    }
    [AvaloniaFact]
    public async Task U01_PausePendingTransitionsStatusDisplayAndHidesPauseWhileCancelRemainsAvailable()
    {
        using var chart = Chart(new ImmediateData());
        var training = new DelayedTraining();
        var directionGen = new RlGeneration(new string('a', 64), "SPY", "source", RlContract.ContractHash, "evaluation", 0m, 0m, new(), DateTimeOffset.UtcNow);
        var registry = new EmptyRegistry { Capabilities = new(false, false, true), Generations = new[] { directionGen } };
        using var vm = new RlTrainingDialogViewModel(training, registry, new UnavailableQ(), new FakeBacktestDefaultSettingsManager(), chart, new LocalizationService());
        await vm.InitializeAsync();
        vm.Symbol = "SPY"; vm.StartDate = "2020-01-01"; vm.EndDateExclusive = "2026-01-01";
        vm.UseContinuousPolicy = true; vm.SelectedDirectionOption = vm.DirectionOptions.First();
        Assert.True(vm.CanTrain);
        var trainTask = vm.TrainCommand.ExecuteAsync(null);
        await training.Started.Task;
        Assert.True(vm.ShowPause);
        Assert.True(vm.CanPause);
        Assert.True(vm.ShowCancel);
        Assert.True(vm.CanCancel);
        vm.PauseCommand.Execute(null);
        Assert.False(vm.ShowPause);
        Assert.False(vm.CanPause);
        Assert.True(vm.ShowCancel);
        Assert.True(vm.CanCancel);
        Assert.Equal(LocalizationManager.Instance.Get("Rl.Checkpoint.PausePending"), vm.StatusDisplay);
        vm.CancelCommand.Execute(null);
        training.Result.SetCanceled();
        await trainTask;
        Assert.Equal(LocalizationManager.Instance.Get("Rl.State.Cancelled"), vm.StatusDisplay);
    }
}
