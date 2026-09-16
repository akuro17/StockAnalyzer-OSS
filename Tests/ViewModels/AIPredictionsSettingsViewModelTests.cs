using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class AIPredictionsSettingsViewModelTests
{
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
        public Task<string> CalculateEgarchAsync(int p = 1, int q = 1) => Task.FromResult("ok");
        public Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m) => Task.FromResult("ok");
        public Task<string> CalculateFftCycleAsync(int windowSize = 50) => Task.FromResult("ok");
        public Task<string> CalculateFourierTransformAsync(int targetPeriod = 50) => Task.FromResult("ok");
        public Task<string> CalculateFftTrendFilterAsync(int windowSize = 50, int numHarmonics = 3) => Task.FromResult("ok");
        public Task<string> CalculateBacktestStatsAsync(System.Collections.Generic.IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => Task.FromResult("ok");
        public Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = 5, double shortSpanPenaltyAlpha = 0.05) => Task.FromResult("ok");
        public Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = 5) => Task.FromResult("ok");
        public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = 5) => Task.FromResult("ok");
        public Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = 5) => Task.FromResult("ok");
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
}
