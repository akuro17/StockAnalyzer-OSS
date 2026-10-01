using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class AIPredictionsSettingsViewModelTests
{
    private static readonly TimeSpan BarrierTimeout = TimeSpan.FromSeconds(10);

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
        Assert.Equal(75, vm.SelectedWindowSize);
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

        Assert.Equal(75, vm.SelectedWindowSize);
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

        Assert.Equal(75, vm.SelectedWindowSize);
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
        public System.Collections.Generic.IReadOnlyList<ModelGenerationSummary> List() => _models;
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
