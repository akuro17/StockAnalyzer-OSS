using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

/// <summary>Settings > AI Predictions after the model-execution separation: only the values that are saved and reverted (window size, prediction colour) and the way to the Prediction Models window.</summary>
public class AIPredictionsSettingsViewModelTests
{
    public AIPredictionsSettingsViewModelTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    /// <summary>An <see cref="IDialogService"/> that only records which method was called.</summary>
    internal class RecordingDialogProxy : DispatchProxy
    {
        public List<(string Method, object? First)> Calls { get; } = new();

        public static (IDialogService Service, RecordingDialogProxy Probe) Create()
        {
            var service = DispatchProxy.Create<IDialogService, RecordingDialogProxy>();
            return (service, (RecordingDialogProxy)(object)service);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add((targetMethod!.Name, args is { Length: > 0 } ? args[0] : null));
            return Task.CompletedTask;
        }
    }

    internal class FakeClipboardService : IClipboardService
    {
        public string? CopiedText { get; private set; }
        public Task SetTextAsync(string text)
        {
            CopiedText = text;
            return Task.CompletedTask;
        }
        public Task<string?> GetTextAsync() => Task.FromResult(CopiedText);
    }

    internal class FakeToastNotificationService : IToastNotificationService
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

    internal class FakePythonService : IPythonService
    {
        public bool IsInitializing => false;
        public bool IsTorchInstalled { get; set; }
        public bool LastForceUpgrade { get; private set; }
        public bool InstallPackagesCalled { get; private set; }
        public System.Collections.Generic.IReadOnlyList<string>? LastPackages { get; private set; }
        public System.Func<Task>? InstallBehavior { get; set; }

        public Task<bool> IsPackageInstalledAsync(string packageName, System.Threading.CancellationToken ct = default)
        {
            return Task.FromResult(packageName == "torch" && IsTorchInstalled);
        }

        public Task InstallPackagesAsync(System.Collections.Generic.IEnumerable<string> packageNames, bool forceUpgrade = false, System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default)
        {
            InstallPackagesCalled = true;
            LastForceUpgrade = forceUpgrade;
            LastPackages = packageNames.ToList();
            progress?.Report("Completed");
            return InstallBehavior?.Invoke() ?? Task.CompletedTask;
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
    public async Task AIPredictionsSettingsViewModel_ManualInstall_CopiesPipCommandToClipboard()
    {
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var vm = new AIPredictionsSettingsViewModel(fakeManager, clipboardService: fakeClipboard, toastNotificationService: fakeToast);

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
        var packages = StockAnalyzer.Core.Services.PythonPackageSets.OnnxTraining;

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
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = false };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, clipboardService: fakeClipboard, toastNotificationService: fakeToast, pythonService: fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.ManualInstallOnnxAsync();

        Assert.NotNull(fakeClipboard.CopiedText);
        Assert.Equal(AIPredictionsSettingsViewModel.OnnxPipManualInstallCommand, fakeClipboard.CopiedText);
        // Only the pip security step (pip>=26.2.2) upgrades; the fresh install does not.
        Assert.Equal(1, fakeClipboard.CopiedText.Split("--upgrade").Length - 1);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_ManualInstall_Installed_CopiesUpgradeCommand()
    {
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = true };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, clipboardService: fakeClipboard, toastNotificationService: fakeToast, pythonService: fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.ManualInstallOnnxAsync();

        Assert.NotNull(fakeClipboard.CopiedText);
        Assert.Equal(AIPredictionsSettingsViewModel.OnnxPipManualUpgradeCommand, fakeClipboard.CopiedText);
        Assert.Contains("--upgrade", fakeClipboard.CopiedText);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_AutoInstall_Uninstalled_CallsInstallWithoutForceUpgrade()
    {
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = false };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, clipboardService: fakeClipboard, toastNotificationService: fakeToast, pythonService: fakePython);

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
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var fakeClipboard = new FakeClipboardService();
        var fakeToast = new FakeToastNotificationService();
        var fakePython = new FakePythonService { IsTorchInstalled = true };
        var vm = new AIPredictionsSettingsViewModel(fakeManager, clipboardService: fakeClipboard, toastNotificationService: fakeToast, pythonService: fakePython);

        await vm.CheckOnnxInstalledAsync();
        await vm.AutoInstallOnnxAsync();

        Assert.True(fakePython.InstallPackagesCalled);
        Assert.True(fakePython.LastForceUpgrade);
        Assert.True(vm.IsOnnxInstalled);
        Assert.NotNull(fakeToast.NotificationMessage);
    }

    [Fact]
    public async Task OpenPredictionModels_AsksTheDialogServiceForTheModelsWindowOnce()
    {
        var (dialogs, probe) = RecordingDialogProxy.Create();
        using var vm = new AIPredictionsSettingsViewModel(new PredictionModelsViewModelTests.FakePredictionSettingsManager(), dialogService: dialogs);

        await vm.OpenPredictionModelsCommand.ExecuteAsync(null);

        Assert.Equal(new[] { nameof(IDialogService.ShowPredictionModelsWindowAsync) }, probe.Calls.Select(c => c.Method));
        Assert.False(vm.IsModified);
    }

    [Fact]
    public async Task OpenPredictionModels_WithoutADialogServiceDoesNothing()
    {
        using var vm = new AIPredictionsSettingsViewModel(new PredictionModelsViewModelTests.FakePredictionSettingsManager());
        await vm.OpenPredictionModelsCommand.ExecuteAsync(null);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void SettingsPage_HoldsOnlyTheSavedValues_NoModelOperation()
    {
        var names = typeof(AIPredictionsSettingsViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();
        foreach (var moved in new[] { "Generations", "EnsembleMembers", "SelectedGeneration", "MonitoringStatus", "ScheduleStatus", "Explainability" })
            Assert.DoesNotContain(moved, names);
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
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var vm = new AIPredictionsSettingsViewModel(fakeManager);

        vm.SelectedWindowSize = 100;

        Assert.True(vm.IsModified);
        Assert.Equal(100, vm.SelectedWindowSize);
    }

    [Fact]
    public async Task AIPredictionsSettingsViewModel_SaveChangesAsync_ClearsIsModified()
    {
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var vm = new AIPredictionsSettingsViewModel(fakeManager);

        vm.SelectedWindowSize = 120;
        Assert.True(vm.IsModified);

        await vm.SaveChangesAsync();

        Assert.False(vm.IsModified);
        Assert.Equal(120, fakeManager.WindowSize);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_RevertChanges_RestoresSnapshot()
    {
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var vm = new AIPredictionsSettingsViewModel(fakeManager);

        vm.SelectedWindowSize = 50;
        Assert.True(vm.IsModified);

        vm.RevertChanges();

        Assert.Equal(60, vm.SelectedWindowSize);
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void AIPredictionsSettingsViewModel_ResetToDefault_SetsTo75()
    {
        var fakeManager = new PredictionModelsViewModelTests.FakePredictionSettingsManager();
        var vm = new AIPredictionsSettingsViewModel(fakeManager);

        vm.SelectedWindowSize = 200;
        vm.ResetToDefault();

        Assert.Equal(60, vm.SelectedWindowSize);
    }

    [Fact]
    public async Task SavingAChangedWindowSize_TellsTheCharts_AndSavingNothingNewDoesNot()
    {
        var (messenger, count) = PredictionModelsViewModelTests.CountingMessenger();
        using var vm = new AIPredictionsSettingsViewModel(new PredictionModelsViewModelTests.FakePredictionSettingsManager(), messenger);

        await vm.SaveChangesAsync();
        Assert.Equal(0, count());

        vm.SelectedWindowSize += 5;
        await vm.SaveChangesAsync();
        Assert.Equal(1, count());

        await vm.SaveChangesAsync();
        Assert.Equal(1, count());
    }

    private static readonly IndicatorColor TestHeat = new(200, 10, 20, 30);

    private static (AIPredictionsSettingsViewModel Vm, ThemeManager Theme, string ThemeFile, string Root) CreateColorRig()
    {
        var root = Path.Combine(Path.GetTempPath(), "sa_vm_heat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var themeFile = Path.Combine(root, "user_theme.json");
        var theme = new ThemeManager(null, themeFile);
        var vm = new AIPredictionsSettingsViewModel(new PredictionModelsViewModelTests.FakePredictionSettingsManager(), themeManager: theme);
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
        using var vm = new AIPredictionsSettingsViewModel(new PredictionModelsViewModelTests.FakePredictionSettingsManager());
        Assert.False(vm.HasAttentionHeatColor);
        Assert.False(vm.IsModified);
    }
}
