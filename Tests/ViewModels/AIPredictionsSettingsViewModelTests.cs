using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class AIPredictionsSettingsViewModelTests
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

    public AIPredictionsSettingsViewModelTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    private sealed class BlockingGenerationRegistry : IModelGenerationRegistry, IDisposable
    {
        private readonly IModelGenerationRegistry _inner;
        private readonly ManualResetEventSlim _release = new(true);
        private TaskCompletionSource<bool> _started = NewSignal();
        private int _armed;

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BlockingGenerationRegistry(IModelGenerationRegistry inner) => _inner = inner;
        public Task Started => _started.Task;
        public void Arm()
        {
            _release.Reset();
            _started = NewSignal();
            Volatile.Write(ref _armed, 1);
        }
        public void Release() => _release.Set();
        public string? ActiveId => _inner.ActiveId;
        public string? PreviousId => _inner.PreviousId;
        public IReadOnlyList<ModelGenerationSummary> List() => _inner.List();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            CancellationToken ct = default) => _inner.RegisterAsync(onnxPath, metricsPath, ct);
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            CancellationToken ct = default) => _inner.ActivateAsync(modelId, manual, confirmLowerScore, ct);
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            CancellationToken ct = default) => _inner.ConfirmActivationAsync(modelId, expectedActiveId, ct);
        public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default) =>
            _inner.RollbackAsync(confirmLowerScore, ct);
        public ModelGenerationLease? AcquireActive() => _inner.AcquireActive();
        public ModelGenerationLease? Acquire(string id) => _inner.Acquire(id);
        public ModelGenerationManifest? GetManifest(string id)
        {
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _started.TrySetResult(true);
                if (!_release.Wait(BarrierTimeout)) throw new TimeoutException("Preset barrier was not released.");
            }
            return _inner.GetManifest(id);
        }
        public void Dispose() => _release.Dispose();
    }

    private sealed class ControlledEnsembleSettingsManager : IEnsembleSettingsManager
    {
        private TaskCompletionSource<bool>? _loadRelease;
        private TaskCompletionSource<bool>? _saveRelease;
        public EnsembleSpec? Current { get; private set; }
        public TaskCompletionSource<bool> LoadStarted { get; private set; } = NewSignal();
        public TaskCompletionSource<bool> SaveStarted { get; private set; } = NewSignal();
        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> BlockLoad() => _loadRelease = NewSignal();
        public TaskCompletionSource<bool> BlockNextSave()
        {
            SaveStarted = NewSignal();
            return _saveRelease = NewSignal();
        }
        public async Task LoadAsync()
        {
            LoadStarted.TrySetResult(true);
            if (_loadRelease is { } pending) await pending.Task;
        }
        public async Task SaveAsync(EnsembleSpec? spec)
        {
            var pending = _saveRelease;
            _saveRelease = null;
            SaveStarted.TrySetResult(true);
            if (pending is not null) await pending.Task;
            Current = spec;
        }
    }
    private class FakePredictionSettingsManager : IPredictionSettingsManager
    {
        public int WindowSize { get; private set; } = PredictionSettingsManager.DefaultWindowSize;
        public void SetWindowSize(int value) => WindowSize = value;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private class FakeClipboardService : IClipboardService
    {
        public string? CopiedText { get; private set; }
        public Task SetTextAsync(string text)
        {
            CopiedText = text;
            return Task.CompletedTask;
        }
        public Task<string?> GetTextAsync() => Task.FromResult(CopiedText);
    }

    private class FakeToastNotificationService : IToastNotificationService
    {
        public string? NotificationMessage { get; private set; }
        public bool IsNotificationVisible { get; private set; }

        public void ShowNotification(string message)
        {
            NotificationMessage = message;
            IsNotificationVisible = true;
        }

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private class FakePythonService : IPythonService
    {
        public bool IsInitializing => false;
        public bool IsTorchInstalled { get; set; }
        public bool LastForceUpgrade { get; private set; }
        public bool InstallPackagesCalled { get; private set; }

        public Task<bool> IsPackageInstalledAsync(string packageName, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(packageName == "torch" && IsTorchInstalled);
        }

        public Task InstallPackagesAsync(System.Collections.Generic.IEnumerable<string> packageNames, bool forceUpgrade = false, System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default)
        {
            InstallPackagesCalled = true;
            LastForceUpgrade = forceUpgrade;
            progress?.Report("Completed");
            return Task.CompletedTask;
        }

        public Task InitializeAsync(System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task InitializeExternalProcessAsync() => Task.CompletedTask;
        public Task<string> PingExternalProcessAsync() => Task.FromResult("pong");
        public Task<string> SendCandlesAsync(System.Collections.Generic.List<StockAnalyzer.Core.Models.CandleData> candles) => Task.FromResult("ok");
        public Task<string> CalculateFftTrendFilterAsync(int windowSize = 50, int numHarmonics = 3) => Task.FromResult("ok");
        public Task<string> CalculateBacktestStatsAsync(System.Collections.Generic.IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => Task.FromResult("ok");
        public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = 5, System.Collections.Generic.IReadOnlyList<double>? volatility = null) => Task.FromResult("ok");
        public Task RunUpdatePipelineAsync(string? symbol = null, System.IProgress<int>? progress = null, bool forceMetadata = false, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task RunPipCommandAsync(string arguments, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task<T> RunAsync<T>(System.Func<Python.Runtime.PyModule, T> func, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(default(T)!);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_DefaultsTo75Bars()
    {
        var vm = new AIPredictionsSettingsViewModel();

        Assert.Equal("Settings_AIPredictions", vm.TitleKey);
        Assert.Equal("SettingsAdvIcon", vm.IconKey);
        Assert.Equal(60, vm.SelectedWindowSize);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_ChangingWindowSize_SetsIsModified()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast);

        vm.SelectedWindowSize = 100;

        Assert.True(vm.IsModified);
        Assert.Equal(100, vm.SelectedWindowSize);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_SaveChangesAsync_ClearsIsModified()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast);

        vm.SelectedWindowSize = 120;
        Assert.True(vm.IsModified);

        await vm.SaveChangesAsync();

        Assert.False(vm.IsModified);
        Assert.Equal(120, fakeManager.WindowSize);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_RevertChanges_RestoresSnapshot()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast);

        vm.SelectedWindowSize = 50;
        Assert.True(vm.IsModified);

        vm.RevertChanges();

        Assert.Equal(60, vm.SelectedWindowSize);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_ResetToDefault_SetsTo75()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast);

        vm.SelectedWindowSize = 200;
        vm.ResetToDefault();

        Assert.Equal(60, vm.SelectedWindowSize);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_ManualInstall_CopiesPipCommandToClipboard()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast);

        await vm.ManualInstallOnnxAsync();

        Assert.NotNull(fakeClipboard.CopiedText);
        Assert.Contains("pip install", fakeClipboard.CopiedText);
        Assert.Contains("torch", fakeClipboard.CopiedText);
        Assert.Contains("tensorflow", fakeClipboard.CopiedText);
        Assert.Contains("lightgbm", fakeClipboard.CopiedText);
        Assert.Contains("numpy", fakeClipboard.CopiedText);
        Assert.Contains("onnx", fakeClipboard.CopiedText);
        Assert.NotNull(fakeToast.NotificationMessage);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_OnnxTrainingPackages_CoversAllThreeModels()
    {
        var packages = AIPredictionsSettingsViewModel.OnnxTrainingPackages;

        Assert.Contains("numpy", packages);
        Assert.Contains("torch", packages);
        Assert.Contains("onnx", packages);
        Assert.Contains("onnxruntime", packages);
        Assert.Contains("onnxscript", packages);
        Assert.Contains("tensorflow", packages);
        Assert.Contains("tf2onnx", packages);
        Assert.Contains("lightgbm", packages);
        Assert.Contains("scikit-learn", packages);
        Assert.Contains("skl2onnx", packages);
        Assert.Contains("onnxmltools", packages);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_ManualInstall_Uninstalled_CopiesFreshInstallCommand()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = false };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast, fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.ManualInstallOnnxAsync();

        Assert.NotNull(fakeClipboard.CopiedText);
        Assert.Equal(AIPredictionsSettingsViewModel.OnnxPipManualInstallCommand, fakeClipboard.CopiedText);
        Assert.DoesNotContain("--upgrade", fakeClipboard.CopiedText);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_ManualInstall_Installed_CopiesUpgradeCommand()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = true };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast, fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.ManualInstallOnnxAsync();

        Assert.NotNull(fakeClipboard.CopiedText);
        Assert.Equal(AIPredictionsSettingsViewModel.OnnxPipManualUpgradeCommand, fakeClipboard.CopiedText);
        Assert.Contains("--upgrade", fakeClipboard.CopiedText);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_AutoInstall_Uninstalled_CallsInstallWithoutForceUpgrade()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = false };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast, fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.AutoInstallOnnxAsync();

        Assert.True(fakePython.InstallPackagesCalled);
        Assert.False(fakePython.LastForceUpgrade);
        Assert.True(vm.IsOnnxInstalled);
        Assert.NotNull(fakeToast.NotificationMessage);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_AutoInstall_Installed_CallsInstallWithForceUpgrade()
    {
        var fakeManager = new FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = true };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, fakeClipboard, fakeToast, fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.AutoInstallOnnxAsync();

        Assert.True(fakePython.InstallPackagesCalled);
        Assert.True(fakePython.LastForceUpgrade);
        Assert.True(vm.IsOnnxInstalled);
        Assert.NotNull(fakeToast.NotificationMessage);
    }

    [Fact]
    public async Task ModelGenerationActivation_RequiresSecondExplicitActionForLowerScore()
    {
        var registry = new FakeGenerationRegistry();
        var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal("current", registry.ActiveId);
        Assert.False(string.IsNullOrEmpty(vm.ActivationWarning));

        vm.SelectedGeneration = registry.List()[2];
        Assert.Null(vm.ActivationWarning);
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal("current", registry.ActiveId);
        vm.SelectedGeneration = registry.List()[1];
        await vm.ActivateGenerationCommand.ExecuteAsync(null);

        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal("older", registry.ActiveId);
        Assert.Null(vm.ActivationWarning);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task ActivationButton_BecomesDeactivate_ForTheActiveGeneration_AndClearsTheActiveGeneration()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        var activate = LocalizationManager.Instance["Settings_ModelGenerations_Activate"];
        var deactivate = LocalizationManager.Instance["Settings_ModelGenerations_Deactivate"];
        Assert.NotEqual(activate, deactivate);
        Assert.Equal(activate, vm.GenerationActivationText); // nothing selected

        vm.SelectedGeneration = registry.List()[1];
        Assert.False(vm.IsSelectedGenerationActive);
        Assert.Equal(activate, vm.GenerationActivationText);

        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedGeneration = registry.List()[0]; // the active generation (marked)
        Assert.True(vm.IsSelectedGenerationActive);
        Assert.Equal(deactivate, vm.GenerationActivationText);
        Assert.Contains(nameof(AIPredictionsSettingsViewModel.GenerationActivationText), raised);

        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null);

        Assert.Null(registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Deactivated"], vm.GenerationStatusMessage);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_LegacyUnverified"], vm.LegacyModelStatus);
        Assert.All(vm.Generations, item => Assert.False(item.IsActive));
        Assert.Equal(activate, vm.GenerationActivationText); // the list was refreshed: nothing selected, nothing active
        Assert.Null(vm.ActivationWarning);
    }

    [Fact]
    public async Task RowCheckBox_IsTheActivationButtonForThatRow_AndTheRowsShowTheRegistryStateAfterwards()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);

        // An inactive row of a lower score: the box asks for the confirmation exactly like Activate; it is not ticked until the registry says so.
        var lower = vm.GenerationRows.Single(r => r.Summary == registry.List()[1]);
        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(lower);
        Assert.Equal(registry.List()[1], vm.SelectedGeneration);
        Assert.False(string.IsNullOrEmpty(vm.ActivationWarning));
        Assert.Equal("current", registry.ActiveId);
        Assert.NotSame(lower, vm.GenerationRows.Single(r => r.Summary == registry.List()[1]));   // rebuilt: no stale local tick
        Assert.False(vm.GenerationRows.Single(r => r.Summary == registry.List()[1]).IsActive);

        // The ticked row deactivates.
        var active = vm.GenerationRows.Single(r => r.IsActive);
        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(active);
        Assert.Null(registry.ActiveId);
        Assert.DoesNotContain(vm.GenerationRows, r => r.IsActive);
        await vm.ToggleGenerationRowActivationCommand.ExecuteAsync(null);   // a header or nothing does nothing
    }

    [Fact]
    public void RowTip_ListsEveryFactOnItsOwnLine()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        var summary = registry.List()[0];
        var tip = vm.GenerationRows.Single(r => r.Summary == summary).Tooltip!;
        var lines = tip.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Contains(summary.ModelFile, lines[0]);
        Assert.Contains(summary.Timeframe, lines[2]);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Tip_Active"], lines[5]);
    }

    [Fact]
    public async Task ActivationButton_OnAnInactiveGeneration_StillActivates_AndDeactivatingTwiceReportsNothingActive()
    {
        var registry = new FakeGenerationRegistry { ActiveId = null };
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // the Activate behaviour is unchanged (a lower score asks for confirmation)
        Assert.False(string.IsNullOrEmpty(vm.ActivationWarning));

        await vm.DeactivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_AlreadyInactive"], vm.GenerationStatusMessage);
        Assert.Null(registry.ActiveId);
    }

    private static (CommunityToolkit.Mvvm.Messaging.IMessenger Messenger, Func<int> Count) CountingMessenger()
    {
        var messenger = new CommunityToolkit.Mvvm.Messaging.StrongReferenceMessenger();
        var count = 0;
        var recipient = new object();
        CommunityToolkit.Mvvm.Messaging.IMessengerExtensions.Register<StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage>(
            messenger, recipient, (_, _) => count++);
        GC.KeepAlive(recipient);
        return (messenger, () => count);
    }

    [Fact]
    public async Task ModelChangesThatSucceed_TellTheChartsOnce_AndRefusedOrUnchangedOnesNeverDo()
    {
        var (messenger, count) = CountingMessenger();
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry, messenger: messenger);

        vm.SelectedGeneration = registry.List()[0];
        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // deactivate
        Assert.Equal(1, count());

        await vm.DeactivateGenerationCommand.ExecuteAsync(null); // nothing active: no change
        Assert.Equal(1, count());

        vm.SelectedGeneration = registry.List()[1];
        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // lower score: asks for confirmation, nothing changed yet
        Assert.Equal(1, count());
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal(2, count());

        registry.ActivationFailure = new PredictionWindowMismatchException(25, 50);
        vm.SelectedGeneration = registry.List()[2];
        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null); // refused
        Assert.Equal(2, count());

        registry.ActivationFailure = null;
        await vm.RollbackGenerationCommand.ExecuteAsync(null); // restore previous: confirmation first, then applied
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal(3, count());
    }

    [Fact]
    public async Task SavingAChangedWindowSize_TellsTheCharts_AndSavingNothingNewDoesNot()
    {
        var (messenger, count) = CountingMessenger();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), messenger: messenger);

        await vm.SaveChangesAsync();
        Assert.Equal(0, count());

        vm.SelectedWindowSize += 5;
        await vm.SaveChangesAsync();
        Assert.Equal(1, count());

        await vm.SaveChangesAsync();
        Assert.Equal(1, count());
    }

    [Fact]
    public async Task ActivationRefusedForAWindowMismatch_NamesBothWindows()
    {
        var registry = new FakeGenerationRegistry { ActivationFailure = new PredictionWindowMismatchException(25, 50) };
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];

        await vm.ToggleGenerationActivationCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance["Settings_ModelGenerations_Error_WindowMismatch"], 25, 50), vm.GenerationStatusMessage);
        Assert.Contains("25", vm.GenerationStatusMessage);
        Assert.Contains("50", vm.GenerationStatusMessage);
    }

    [Fact]
    public void WindowMismatchAndDeactivationTexts_ExistInBothLanguages()
    {
        foreach (var language in new[] { "en", "ja" })
        {
            LocalizationManager.Instance.Initialize(language);
            foreach (var key in new[] { "Settings_ModelGenerations_Deactivate", "Settings_ModelGenerations_Deactivated",
                         "Settings_ModelGenerations_AlreadyInactive", "Settings_ModelGenerations_Error_WindowMismatch", "Prediction_Notice_WindowMismatch" })
                Assert.False(string.IsNullOrWhiteSpace(LocalizationManager.Instance[key]) || LocalizationManager.Instance[key] == $"[{key}]", $"{language}:{key}");
        }
        LocalizationManager.Instance.Initialize("en");
    }

    [Fact]
    public async Task ActivationWarning_IsDiscardedWhenSelectionChangesOrListRefreshesDuringAwait()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);

        vm.SelectedGeneration = registry.List()[1];
        var releaseSelection = registry.BlockNextActivation();
        var activation = vm.ActivateGenerationCommand.ExecuteAsync(null);
        await registry.ActivationStarted.WaitAsync(BarrierTimeout);
        vm.SelectedGeneration = registry.List()[2];
        releaseSelection.TrySetResult(true);
        await activation.WaitAsync(BarrierTimeout);
        Assert.Null(vm.ActivationWarning);
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal("current", registry.ActiveId);

        vm.SelectedGeneration = registry.List()[1];
        var releaseRefresh = registry.BlockNextActivation();
        activation = vm.ActivateGenerationCommand.ExecuteAsync(null);
        await registry.ActivationStarted.WaitAsync(BarrierTimeout);
        vm.RefreshGenerationsCommand.Execute(null);
        releaseRefresh.TrySetResult(true);
        await activation.WaitAsync(BarrierTimeout);
        Assert.Null(vm.ActivationWarning);
        Assert.Null(vm.SelectedGeneration);
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal("current", registry.ActiveId);
    }

    [Fact]
    public async Task RollbackWarning_KeepsOriginalCandidateWhenHistoryChangesDuringAwait()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        var release = registry.BlockNextActivation();
        var rollback = vm.RollbackGenerationCommand.ExecuteAsync(null);
        await registry.ActivationStarted.WaitAsync(BarrierTimeout);
        registry.PreviousId = "other";
        release.TrySetResult(true);
        await rollback.WaitAsync(BarrierTimeout);
        Assert.NotNull(vm.ActivationWarning);
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal("older", registry.ActiveId);
    }

    [Fact]
    public async Task Confirmation_RejectsChangedBaselineAndClearsOldWarning()
    {
        var registry = new FakeGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];
        await vm.ActivateGenerationCommand.ExecuteAsync(null);
        registry.ActiveId = "other";
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal("other", registry.ActiveId);
        Assert.Null(vm.ActivationWarning);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_ContextChanged"], vm.GenerationStatusMessage);
    }

    [Fact]
    public async Task EnsemblePercentages_AreConvertedAtSaveBoundary()
    {
        var manager = new FakeEnsembleSettingsManager();
        var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), ensembleSettings: manager);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("a", 25m));
        vm.EnsembleMembers.Add(new EnsembleWeightRow("b", 75m));
        await vm.SaveEnsembleCommand.ExecuteAsync(null);
        Assert.Equal(.25, manager.Current!.Members[0].Weight);
        Assert.Equal(.75, manager.Current.Members[1].Weight);
        await vm.ClearEnsembleCommand.ExecuteAsync(null);
        Assert.Null(manager.Current);
    }

    [Fact]
    public async Task ManagementFailures_ShowLocalizedReasonsInsteadOfExceptionText()
    {
        const string internalDetail = "sensitive internal path";
        var settings = new FakeEnsembleSettingsManager
        {
            SaveFailure = new IOException(internalDetail),
        };
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), ensembleSettings: settings);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("a", 100m));

        await vm.SaveEnsembleCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_Ensemble_Error_Storage"], vm.EnsembleStatus);
        Assert.DoesNotContain(internalDetail, vm.EnsembleStatus);

        settings.SaveFailure = new KeyNotFoundException(internalDetail);
        await vm.SaveEnsembleCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_Ensemble_Error_MissingGeneration"], vm.EnsembleStatus);
        Assert.DoesNotContain(internalDetail, vm.EnsembleStatus);

        var registry = new FakeGenerationRegistry { ActivationFailure = new KeyNotFoundException(internalDetail) };
        using var generationVm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        generationVm.SelectedGeneration = registry.List()[1];
        await generationVm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_Missing"],
            generationVm.GenerationStatusMessage);
        Assert.DoesNotContain(internalDetail, generationVm.GenerationStatusMessage);

        registry.ActivationFailure = new InvalidOperationException(internalDetail);
        await generationVm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_Incompatible"],
            generationVm.GenerationStatusMessage);

        registry.ActivationFailure = new IOException(internalDetail);
        await generationVm.ActivateGenerationCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_Storage"],
            generationVm.GenerationStatusMessage);
    }

    [Fact]
    public async Task AccuracyPreset_DiscardsResultsAfterMemberWeightClearOrSaveEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_vm_ensemble_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var registry = new ModelGenerationRegistry(Path.Combine(root, "generations"));
            var a = await RegisterEnsembleFixtureAsync(registry, root, "a", .4);
            var b = await RegisterEnsembleFixtureAsync(registry, root, "b", .6);
            var c = await RegisterEnsembleFixtureAsync(registry, root, "c", .5);
            using var blocked = new BlockingGenerationRegistry(registry);
            var settings = new FakeEnsembleSettingsManager();
            using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
                new FakeClipboardService(), new FakeToastNotificationService(),
                modelRegistry: blocked, ensembleSettings: settings);

            async Task CheckStale(Func<Task> edit)
            {
                vm.EnsembleMembers.Clear();
                vm.EnsembleMembers.Add(new EnsembleWeightRow(a.ModelId, 25m));
                vm.EnsembleMembers.Add(new EnsembleWeightRow(b.ModelId, 75m));
                blocked.Arm();
                var preset = vm.AccuracyEnsembleWeightsCommand.ExecuteAsync(null);
                await blocked.Started.WaitAsync(BarrierTimeout);
                try
                {
                    await edit();
                    var expected = vm.EnsembleMembers.Select(row => (row.ModelId, row.Percent)).ToArray();
                    var status = vm.EnsembleStatus;
                    blocked.Release();
                    await preset.WaitAsync(BarrierTimeout);
                    Assert.Equal(expected, vm.EnsembleMembers.Select(row => (row.ModelId, row.Percent)));
                    Assert.Equal(status, vm.EnsembleStatus);
                }
                finally { blocked.Release(); }
            }

            await CheckStale(() =>
            {
                vm.SelectedEnsembleMember = vm.EnsembleMembers[0];
                vm.RemoveEnsembleMemberCommand.Execute(null);
                vm.SelectedGeneration = blocked.List().Single(item => item.ModelId == c.ModelId);
                vm.AddEnsembleMemberCommand.Execute(null);
                return Task.CompletedTask;
            });
            await CheckStale(() =>
            {
                vm.EnsembleMembers.Move(0, 1);
                return Task.CompletedTask;
            });
            await CheckStale(() =>
            {
                vm.EnsembleMembers[0].Percent = 40m;
                return Task.CompletedTask;
            });
            await CheckStale(() => vm.ClearEnsembleCommand.ExecuteAsync(null));
            await CheckStale(() => vm.SaveEnsembleCommand.ExecuteAsync(null));
            Assert.Equal(.25, settings.Current!.Members[0].Weight);
            Assert.Equal(.75, settings.Current.Members[1].Weight);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PendingLoadSaveAndClear_DoNotOverwriteNewerRows()
    {
        var manager = new ControlledEnsembleSettingsManager();
        var loadRelease = manager.BlockLoad();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), ensembleSettings: manager);
        await manager.LoadStarted.Task.WaitAsync(BarrierTimeout);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("a", 25m));
        vm.EnsembleMembers.Add(new EnsembleWeightRow("b", 75m));
        loadRelease.TrySetResult(true);
        await Task.Yield();
        Assert.Equal(new[] { "a", "b" }, vm.EnsembleMembers.Select(row => row.ModelId));

        var saveRelease = manager.BlockNextSave();
        var saving = vm.SaveEnsembleCommand.ExecuteAsync(null);
        await manager.SaveStarted.Task.WaitAsync(BarrierTimeout);
        vm.EnsembleMembers[0].Percent = 40m;
        saveRelease.TrySetResult(true);
        await saving.WaitAsync(BarrierTimeout);
        Assert.Equal(.25, manager.Current!.Members[0].Weight);
        Assert.Equal(40m, vm.EnsembleMembers[0].Percent);
        Assert.Null(vm.EnsembleStatus);

        var clearRelease = manager.BlockNextSave();
        var clearing = vm.ClearEnsembleCommand.ExecuteAsync(null);
        await manager.SaveStarted.Task.WaitAsync(BarrierTimeout);
        vm.EnsembleMembers.Add(new EnsembleWeightRow("c", 0m));
        clearRelease.TrySetResult(true);
        await clearing.WaitAsync(BarrierTimeout);
        Assert.Equal(new[] { "a", "b", "c" }, vm.EnsembleMembers.Select(row => row.ModelId));
        Assert.Null(vm.EnsembleStatus);
    }

    private static readonly IndicatorColor TestHeat = new(200, 10, 20, 30);

    private static (AIPredictionsSettingsViewModel Vm, ThemeManager Theme, string ThemeFile, string Root) CreateColorRig()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_vm_heat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var themeFile = Path.Combine(root, "user_theme.json");
        var theme = new ThemeManager(null, themeFile);
        var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(), new FakeClipboardService(),
            new FakeToastNotificationService(), themeManager: theme);
        return (vm, theme, themeFile, root);
    }

    [Fact]
    public void PredictionColor_PreviewsOnTheChartsAtOnce_AndRevertPutsTheSavedColorBack()
    {
        var (vm, theme, _, root) = CreateColorRig();
        try
        {
            using (vm)
            {
                var saved = theme.CurrentTheme.AttentionHeat;
                Assert.True(vm.HasAttentionHeatColor);
                Assert.Equal(saved, vm.AttentionHeatColor);
                Assert.False(vm.IsModified);

                vm.AttentionHeatColor = TestHeat;

                Assert.Equal(TestHeat, theme.CurrentTheme.AttentionHeat);
                Assert.Equal(TestHeat, theme.GetCurrentColors()[ThemeColorKey.AttentionHeat]);
                Assert.True(vm.IsModified);

                vm.RevertChanges();

                Assert.Equal(saved, theme.CurrentTheme.AttentionHeat);
                Assert.Equal(saved, vm.AttentionHeatColor);
                Assert.False(vm.IsModified);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PredictionColor_IsWrittenToTheThemeFileOnlyWhenItChanged()
    {
        var (vm, theme, themeFile, root) = CreateColorRig();
        try
        {
            using (vm)
            {
                await vm.SaveChangesAsync();
                Assert.False(File.Exists(themeFile), "nothing changed: the owner's theme file is not rewritten");

                vm.AttentionHeatColor = TestHeat;
                await vm.SaveChangesAsync();

                Assert.True(File.Exists(themeFile));
                Assert.False(vm.IsModified);
                var reloaded = new ThemeManager(null, themeFile);
                await reloaded.LoadAsync();
                Assert.Equal(TestHeat, reloaded.CurrentTheme.AttentionHeat);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PredictionColor_FollowsAThemeChangeMadeElsewhere_AndResetUsesThePresetColor()
    {
        var (vm, theme, _, root) = CreateColorRig();
        try
        {
            using (vm)
            {
                theme.UpdateSingleColor(ThemeColorKey.AttentionHeat, TestHeat); // e.g. the Theme page
                Assert.Equal(TestHeat, vm.AttentionHeatColor);
                Assert.True(vm.IsModified);

                vm.ResetToDefault();

                var preset = (theme.CurrentTheme.IsDark ? ThemeColors.Dark : ThemeColors.Light).AttentionHeat;
                Assert.Equal(preset, vm.AttentionHeatColor);
                Assert.Equal(preset, theme.CurrentTheme.AttentionHeat);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PredictionColor_IsHiddenAndInertWithoutAThemeManager()
    {
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(), new FakeClipboardService(), new FakeToastNotificationService());
        Assert.False(vm.HasAttentionHeatColor);
        Assert.False(vm.IsModified);
    }

    /// <summary>An <see cref="IDialogService"/> that only answers the open-file dialog (a real fake of the whole interface is not needed here).</summary>
    public class OpenFileDialogProxy : DispatchProxy
    {
        public string? PickedPath { get; set; }
        public string[]? LastFilters { get; private set; }
        public int Calls { get; private set; }
        public bool ConfirmAnswer { get; set; } = true;
        public int ConfirmCalls { get; private set; }

        public static (IDialogService Service, OpenFileDialogProxy Probe) Create(string? pickedPath)
        {
            var service = DispatchProxy.Create<IDialogService, OpenFileDialogProxy>();
            var probe = (OpenFileDialogProxy)(object)service;
            probe.PickedPath = pickedPath;
            return (service, probe);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowConfirmationAsync))
            {
                ConfirmCalls++;
                return Task.FromResult(ConfirmAnswer);
            }

            if (targetMethod?.Name != nameof(IDialogService.ShowOpenFileDialogAsync)) throw new NotSupportedException(targetMethod?.Name);
            Calls++;
            LastFilters = (string[]?)args![1];
            return Task.FromResult(PickedPath);
        }
    }

    /// <summary>A registry-ready copy of ensemble fixture <paramref name="index"/> (a model with its independent-evaluation file beside it).</summary>
    private static string CopyRegistrableFixture(string root, int index, bool withMetrics = true, string? evaluationRevision = null)
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets"));
        var folder = Path.Combine(root, "picked" + index + (withMetrics ? "" : "_nometrics"));
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, $"ensemble_fixture_m0{index}.onnx");
        File.Copy(Path.Combine(assets, $"ensemble_fixture_m0{index}.onnx"), model);
        if (withMetrics) File.Copy(Path.Combine(assets, $"ensemble_fixture_m0{index}.onnx.metrics.json"), model + ".metrics.json");
        if (withMetrics && evaluationRevision is not null)
        {
            // a model of another training run: the same metrics with another evaluation revision (the fixtures share one revision)
            var fixtureRevision = new string('d', 64);
            File.WriteAllText(model + ".metrics.json", File.ReadAllText(model + ".metrics.json").Replace(fixtureRevision, evaluationRevision));
        }
        return model;
    }

    private static async Task WithAddFromFileRig(Func<string, ModelGenerationRegistry, Func<string?, (AIPredictionsSettingsViewModel Vm, OpenFileDialogProxy Dialog)>, Func<int>, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_vm_add_model_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var (messenger, count) = CountingMessenger();
        using var registry = new ModelGenerationRegistry(Path.Combine(root, "generations"));
        try
        {
            (AIPredictionsSettingsViewModel, OpenFileDialogProxy) Create(string? picked)
            {
                var (dialogs, probe) = OpenFileDialogProxy.Create(picked);
                return (new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(), new FakeClipboardService(),
                    new FakeToastNotificationService(), modelRegistry: registry, messenger: messenger, dialogService: dialogs), probe);
            }
            await body(root, registry, Create, count);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { /* temp folder, best effort */ } }
    }

    [Fact]
    public Task AddModelFromFile_RegistersActivatesAndAnnouncesThePickedOnnx() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Generations);
        Assert.True(row.IsActive);
        Assert.Null(vm.SelectedGeneration); // the list is reloaded after an activation, exactly as after the Activate button
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
        Assert.Equal(row.ModelId, registry.ActiveId);
        Assert.Equal(1, count());
        Assert.Equal(1, dialog.Calls);
        Assert.Equal(new[] { ".onnx" }, dialog.LastFilters);
    });

    [Fact]
    public Task AddModelFromFile_WithoutTheEvaluationFile_ChangesNothingAndSaysWhatIsMissing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, _) = create(CopyRegistrableFixture(root, 1, withMetrics: false));
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Empty(vm.Generations);
        Assert.Null(registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_FileMissing"], vm.GenerationStatusMessage);
        Assert.Contains(".metrics.json", vm.GenerationStatusMessage);
        Assert.Equal(0, count());
        Assert.False(vm.IsBusy);
    });

    [Fact]
    public Task AddModelFromFile_CancellingTheDialogDoesNothing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(null);
        using var _ = vm;

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        Assert.Empty(vm.Generations);
        Assert.Null(vm.GenerationStatusMessage);
        Assert.Equal(0, count());
    });

    [Fact]
    public Task AddModelFromFile_AModelThatDoesNotImproveTheScoreIsSelectedAndNeedsTheUsualConfirmation() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var first = registry.ActiveId;
        dialog.PickedPath = CopyRegistrableFixture(root, 2);

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // the same score: a manual activation asks for confirmation first

        Assert.Equal(2, vm.Generations.Count);
        Assert.Equal(first, registry.ActiveId);
        Assert.NotNull(vm.SelectedGeneration);
        Assert.NotEqual(first, vm.SelectedGeneration!.ModelId);
        Assert.False(string.IsNullOrEmpty(vm.ActivationWarning));
        Assert.Equal(1, count());

        await vm.ConfirmActivationCommand.ExecuteAsync(null);

        Assert.NotEqual(first, registry.ActiveId);
        Assert.Equal(registry.ActiveId, vm.Generations.Single(g => g.IsActive).ModelId);
        Assert.Equal(2, count());
    });

    private static readonly string OtherEvaluationRevision = new('e', 64);

    /// <summary>The attention fixture (horizon 3, while the ensemble fixtures have horizon 5) with the evidence the registry demands: another ensemble group.</summary>
    private static string CopyOtherGroupFixture(string root)
    {
        var assets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets"));
        var folder = Path.Combine(root, "picked_other_group");
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, "other_group.onnx");
        File.Copy(Path.Combine(assets, "attention_fixture.onnx"), model);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var revision = new string('d', 64);
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = revision, scored_model_sha256 = hash, target_type = "classification",
            timeframe = "daily", horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey], fold_macro_f1 = 0.4, fold_is_holdout = 1.0, fold = 2.0, fold_n = 20.0,
            n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash, fold_macro_f1 = 0.4 },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return model;
    }

    [Fact]
    public Task ModelList_GroupsTheModelsThatCanFormOneEnsemble_UnderHeaders_AndOnlyAddsWithinAGroup() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = CopyRegistrableFixture(root, 2);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // same group, same score: waits for the lower-score confirmation
        dialog.PickedPath = CopyOtherGroupFixture(root);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // another horizon: a different group (and a replacement confirmation)
        await vm.GenerationGroupingTask;

        var headers = vm.GenerationRows.Where(r => r.IsHeader).ToArray();
        Assert.Equal(2, headers.Length);
        Assert.All(headers, h => Assert.False(h.IsSelectable));
        // groups are ordered by time frame, then horizon: the 3-bar group first
        Assert.Contains("3", headers[0].HeaderText);
        Assert.EndsWith("(1)", headers[0].HeaderText);
        Assert.Contains("5", headers[1].HeaderText);
        Assert.EndsWith("(2)", headers[1].HeaderText);
        Assert.Equal(new[] { true, false, true, false, false }, vm.GenerationRows.Select(r => r.IsHeader).ToArray());
        var fiveBarRows = vm.GenerationRows.SkipWhile(r => !ReferenceEquals(r, headers[1])).Skip(1).ToArray();
        Assert.Equal(2, fiveBarRows.Length);
        Assert.Single(fiveBarRows.Select(r => r.GroupKey).Distinct());

        // ensemble: after one 5-bar model, the other 5-bar model may be added, the 3-bar one may not
        var first = fiveBarRows[0];
        var second = fiveBarRows[1];
        var otherGroup = vm.GenerationRows.First(r => r.IsItem);
        vm.SelectedGenerationRow = first;
        Assert.True(vm.AddEnsembleMemberCommand.CanExecute(null));
        vm.AddEnsembleMemberCommand.Execute(null);
        Assert.Single(vm.EnsembleMembers);

        vm.SelectedGenerationRow = otherGroup;
        Assert.False(vm.AddEnsembleMemberCommand.CanExecute(null));
        Assert.Equal(LocalizationManager.Instance["Settings_Ensemble_Hint_OtherGroup"], vm.AddEnsembleMemberHint);

        vm.SelectedGenerationRow = second;
        Assert.True(vm.AddEnsembleMemberCommand.CanExecute(null));
        Assert.Null(vm.AddEnsembleMemberHint);
        vm.AddEnsembleMemberCommand.Execute(null);
        Assert.Equal(2, vm.EnsembleMembers.Count);
        Assert.False(vm.AddEnsembleMemberCommand.CanExecute(null), "already a member");
    });

    [Fact]
    public Task ModelList_SelectionFollowsTheRowsAndSurvivesTheGroupingRefresh() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, _) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        await vm.GenerationGroupingTask;
        var item = vm.GenerationRows.Single(r => r.IsItem);

        vm.SelectedGenerationRow = item;
        Assert.Equal(item.Summary, vm.SelectedGeneration);

        vm.SelectedGeneration = null;
        Assert.Null(vm.SelectedGenerationRow);

        vm.SelectedGeneration = item.Summary;
        Assert.Same(item, vm.SelectedGenerationRow);
    });

    [Fact]
    public Task RemoveFromList_DeletesTheRegisteredCopy_KeepsTheSourceFile_AndTheFileCanBeAddedAgain() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var firstFile = CopyRegistrableFixture(root, 1);
        var (vm, dialog) = create(firstFile);
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var active = registry.ActiveId;
        var secondFile = CopyRegistrableFixture(root, 2, evaluationRevision: OtherEvaluationRevision);
        dialog.PickedPath = secondFile;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // registered, not active (needs the replace confirmation)
        var second = vm.SelectedGeneration!.ModelId;
        Assert.Equal(2, vm.Generations.Count);

        vm.SelectedGeneration = vm.Generations.Single(g => g.ModelId == second);
        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCalls);
        Assert.Equal(active, Assert.Single(vm.Generations).ModelId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Removed"], vm.GenerationStatusMessage);
        Assert.Null(vm.ActivationWarning);
        Assert.True(File.Exists(secondFile), "the file it was added from is never touched");
        Assert.False(Directory.Exists(Path.Combine(root, "generations", second)));

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // the same file again
        Assert.Equal(2, vm.Generations.Count);
    });

    [Fact]
    public Task RemoveFromList_RefusesTheActiveModel_AndAModelOfTheSavedEnsemble_WithoutAsking() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var active = vm.Generations.Single();
        vm.SelectedGeneration = active;

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveActive"], vm.GenerationStatusMessage);
        Assert.Equal(0, dialog.ConfirmCalls);
        Assert.Single(vm.Generations);

        // an ensemble member (not the active model) cannot be removed either
        dialog.PickedPath = CopyRegistrableFixture(root, 2);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        await vm.ConfirmActivationCommand.ExecuteAsync(null);
        var member = vm.Generations.Single(g => !g.IsActive);
        var ensemble = new FakeEnsembleSettingsManager();
        await ensemble.SaveAsync(EnsembleSpec.Equal(new[] { member.ModelId }));
        var (withEnsemble, _) = (new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(), new FakeClipboardService(),
            new FakeToastNotificationService(), modelRegistry: registry, ensembleSettings: ensemble,
            dialogService: DispatchProxy.Create<IDialogService, OpenFileDialogProxy>()), 0);
        using var __ = withEnsemble;
        withEnsemble.SelectedGeneration = withEnsemble.Generations.Single(g => g.ModelId == member.ModelId);

        await withEnsemble.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveEnsemble"], withEnsemble.GenerationStatusMessage);
        Assert.Equal(2, withEnsemble.Generations.Count);
    });

    [Fact]
    public Task RemoveFromList_ADeclinedConfirmationRemovesNothing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.PickedPath = CopyRegistrableFixture(root, 2);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        dialog.ConfirmAnswer = false;
        vm.SelectedGeneration = vm.Generations.Single(g => !g.IsActive);

        await vm.RemoveGenerationCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCalls);
        Assert.Equal(2, vm.Generations.Count);
    });

    [Fact]
    public Task AddModelFromFile_ARunEvaluatedDifferently_AsksToReplaceTheActiveModel_ThenSwitchesAndCanBeRestored() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var first = registry.ActiveId;
        dialog.PickedPath = CopyRegistrableFixture(root, 2, evaluationRevision: OtherEvaluationRevision);

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null); // another evaluation revision: cannot be switched to directly

        Assert.Equal(2, vm.Generations.Count);
        Assert.Equal(first, registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_ReplaceWarning"], vm.ActivationWarning);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_AddedNotActivated"], vm.GenerationStatusMessage);
        Assert.Equal(1, count());
        var second = vm.SelectedGeneration!.ModelId;

        await vm.ConfirmActivationCommand.ExecuteAsync(null);

        Assert.Equal(second, registry.ActiveId);
        Assert.Equal(first, registry.PreviousId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
        Assert.Null(vm.ActivationWarning);
        Assert.Equal(2, count());

        await vm.RollbackGenerationCommand.ExecuteAsync(null); // the old model is incompatible with the new one: the same confirmation
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_ReplaceWarning"], vm.ActivationWarning);
        Assert.Equal(second, registry.ActiveId);
        await vm.ConfirmActivationCommand.ExecuteAsync(null);

        Assert.Equal(first, registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Restored"], vm.GenerationStatusMessage);
        Assert.Equal(3, count());
    });

    [Fact]
    public Task Replacement_ThatChangedUnderneath_IsRefusedAndChangesNothing() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, dialog) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);
        var first = registry.ActiveId;
        dialog.PickedPath = CopyRegistrableFixture(root, 2, evaluationRevision: OtherEvaluationRevision);
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        await registry.DeactivateAsync(); // someone else changed the active model while the warning was shown
        await vm.ConfirmActivationCommand.ExecuteAsync(null);

        Assert.Null(registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Error_ContextChanged"], vm.GenerationStatusMessage);
        Assert.Equal(1, count());
        Assert.NotNull(first);
    });

    [Fact]
    public Task AddModelFromFile_APickedModelThatIsAlreadyActiveIsNotRegisteredTwice() => WithAddFromFileRig(async (root, registry, create, count) =>
    {
        var (vm, _) = create(CopyRegistrableFixture(root, 1));
        using var _ = vm;
        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        await vm.AddGenerationFromFileCommand.ExecuteAsync(null);

        Assert.Single(vm.Generations);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_AlreadyActive"], vm.GenerationStatusMessage);
        Assert.Equal(1, count());
    });

    private static async Task<ModelGenerationManifest> RegisterEnsembleFixtureAsync(
        ModelGenerationRegistry registry, string root, string name, double score)
    {
        var source = Path.Combine(root, name);
        Directory.CreateDirectory(source);
        var model = Path.Combine(source, "member.onnx");
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "StockAnalyzer.Core.Tests", "Assets", "trend_predictor_goodmeta.onnx"));
        File.Copy(fixture, model);
        using var session = new InferenceSession(model);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))).ToLowerInvariant();
        var revision = new string('a', 64);
        var report = new
        {
            evaluation_status = "independent_outer", evaluation_revision = revision,
            scored_model_sha256 = hash, target_type = "classification", timeframe = "daily",
            horizon = int.Parse(metadata[PredictionModelMetadata.HorizonKey]),
            class_order = metadata[PredictionModelMetadata.ClassOrderKey],
            fold_macro_f1 = score, fold_accuracy = score, fold_is_holdout = 1.0,
            fold = 2.0, fold_n = 20.0, n_splits = 3.0,
            outer_folds = new[]
            {
                new { fold = 0.0, evaluation_revision = revision, scored_model_sha256 = hash,
                    fold_macro_f1 = .2, fold_accuracy = .2 },
                new { fold = 1.0, evaluation_revision = revision, scored_model_sha256 = hash,
                    fold_macro_f1 = .3, fold_accuracy = .3 },
                new { fold = 2.0, evaluation_revision = revision, scored_model_sha256 = hash,
                    fold_macro_f1 = score, fold_accuracy = score },
            },
        };
        File.WriteAllText(model + ".metrics.json", JsonSerializer.Serialize(report));
        return await registry.RegisterAsync(model);
    }

    private sealed class FakeEnsembleSettingsManager : IEnsembleSettingsManager
    {
        public EnsembleSpec? Current { get; private set; }
        public Exception? LoadFailure { get; set; }
        public Exception? SaveFailure { get; set; }
        public Task LoadAsync() => LoadFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        public Task SaveAsync(EnsembleSpec? spec)
        {
            if (SaveFailure is { } failure) return Task.FromException(failure);
            Current = spec;
            return Task.CompletedTask;
        }
    }

    /// <summary>A registry that cannot compare the candidate with the active generation and replaces it in one call, like <see cref="ModelGenerationRegistry"/>.</summary>
    private sealed class ReplacingGenerationRegistry : IModelGenerationRegistry
    {
        private readonly ModelGenerationSummary[] _models =
        {
            new("current", "current.onnx", "classification", "daily", 5, 0.8, "revision", true),
            new("other", "other.onnx", "classification", "daily", 5, 0.6, "other revision", false),
        };
        public string? ActiveId { get; private set; } = "current";
        public string? PreviousId { get; private set; }
        public Exception? ReplaceFailure { get; set; }
        public int ActivateCalls { get; private set; }
        public int DeactivateCalls { get; private set; }
        public System.Collections.Generic.List<(string ModelId, string? ExpectedActiveId)> ReplaceCalls { get; } = new();
        public System.Collections.Generic.IReadOnlyList<ModelGenerationSummary> List() =>
            _models.Select(model => model with { IsActive = model.ModelId == ActiveId }).ToArray();
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            System.Threading.CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default)
        {
            ActivateCalls++;
            throw new ModelActivationIncompatibleException();
        }
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            System.Threading.CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<bool> RollbackAsync(bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeactivateAsync(System.Threading.CancellationToken ct = default)
        {
            DeactivateCalls++;
            return Task.FromResult(false);
        }
        public Task<bool> ReplaceActiveAsync(string modelId, string? expectedActiveId, System.Threading.CancellationToken ct = default)
        {
            ReplaceCalls.Add((modelId, expectedActiveId));
            if (ReplaceFailure is { } failure) return Task.FromException<bool>(failure);
            PreviousId = ActiveId;
            ActiveId = modelId;
            return Task.FromResult(true);
        }
        public ModelGenerationLease? AcquireActive() => null;
    }

    [Fact]
    public async Task ConfirmedReplacement_IsOneReplaceActiveCall_NotADeactivateAndAnActivate()
    {
        var registry = new ReplacingGenerationRegistry();
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];
        await vm.ActivateGenerationCommand.ExecuteAsync(null);   // incomparable: the replacement warning is shown
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_ReplaceWarning"], vm.ActivationWarning);

        await vm.ConfirmActivationCommand.ExecuteAsync(null);

        Assert.Equal(("other", (string?)"current"), Assert.Single(registry.ReplaceCalls));
        Assert.Equal(1, registry.ActivateCalls);    // only the first, refused attempt
        Assert.Equal(0, registry.DeactivateCalls);
        Assert.Equal("other", registry.ActiveId);
        Assert.Equal(LocalizationManager.Instance["Settings_ModelGenerations_Activated"], vm.GenerationStatusMessage);
    }

    [Fact]
    public async Task ConfirmedReplacement_RefusedByAWindowMismatch_ShowsBothWindows_AndChangesNothing()
    {
        var registry = new ReplacingGenerationRegistry { ReplaceFailure = new PredictionWindowMismatchException(25, 50) };
        using var vm = new AIPredictionsSettingsViewModel(new FakePredictionSettingsManager(),
            new FakeClipboardService(), new FakeToastNotificationService(), modelRegistry: registry);
        vm.SelectedGeneration = registry.List()[1];
        await vm.ActivateGenerationCommand.ExecuteAsync(null);

        await vm.ConfirmActivationCommand.ExecuteAsync(null);

        Assert.Single(registry.ReplaceCalls);
        Assert.Equal("current", registry.ActiveId);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance["Settings_ModelGenerations_Error_WindowMismatch"], 25, 50), vm.GenerationStatusMessage);
    }

    private sealed class FakeGenerationRegistry : IModelGenerationRegistry
    {
        private readonly ModelGenerationSummary[] _models =
        {
            new("current", "current.onnx", "classification", "daily", 5, 0.8, "revision", true),
            new("older", "older.onnx", "classification", "daily", 5, 0.4, "revision", false),
            new("other", "other.onnx", "classification", "daily", 5, 0.6, "revision", false),
        };
        private TaskCompletionSource<bool>? _activationRelease;
        public Task ActivationStarted { get; private set; } = Task.CompletedTask;
        public TaskCompletionSource<bool> BlockNextActivation()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ActivationStarted = started.Task;
            _activationStarted = started;
            return _activationRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private TaskCompletionSource<bool>? _activationStarted;
        public string? ActiveId { get; set; } = "current";
        public string? PreviousId { get; set; } = "older";
        public Exception? ActivationFailure { get; set; }
        public System.Collections.Generic.IReadOnlyList<ModelGenerationSummary> List() =>
            _models.Select(model => model with { IsActive = model.ModelId == ActiveId }).ToArray();
        public Task<bool> DeactivateAsync(System.Threading.CancellationToken ct = default)
        {
            if (ActiveId is null) return Task.FromResult(false);
            PreviousId = ActiveId;
            ActiveId = null;
            return Task.FromResult(true);
        }
        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null,
            System.Threading.CancellationToken ct = default) => throw new System.NotSupportedException();
        public async Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default)
        {
            if (ActivationFailure is { } failure) throw failure;
            if (_activationRelease is { } release)
            {
                _activationRelease = null;
                _activationStarted?.TrySetResult(true);
                await release.Task.WaitAsync(ct);
            }
            if (!confirmLowerScore) throw new ModelActivationConfirmationRequiredException(modelId, ActiveId);
            ActiveId = modelId;
            return true;
        }
        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId,
            System.Threading.CancellationToken ct = default)
        {
            if (ActiveId != expectedActiveId) throw new ModelActivationContextChangedException();
            return ActivateAsync(modelId, manual: true, confirmLowerScore: true, ct);
        }
        public Task<bool> RollbackAsync(bool confirmLowerScore = false,
            System.Threading.CancellationToken ct = default) => PreviousId is { } previous
                ? ActivateAsync(previous, true, confirmLowerScore, ct) : Task.FromResult(false);
        public ModelGenerationLease? AcquireActive() => null;
    }
}
