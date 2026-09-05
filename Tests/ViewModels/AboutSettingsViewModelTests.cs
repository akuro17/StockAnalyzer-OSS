using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Tests.ViewModels;

public class AboutSettingsViewModelTests
{
    [Fact]
    public void AboutSettingsViewModel_IsReadOnlyPage()
    {
        var vm = new AboutSettingsViewModel();

        Assert.Equal("Settings_About", vm.TitleKey);
        Assert.Equal("SettingsAboutIcon", vm.IconKey);
        Assert.False(vm.IsModified);

        // No-op mutators must not throw and must never flip IsModified.
        vm.ResetToDefault();
        vm.RevertChanges();
        Assert.False(vm.IsModified);
    }

    [Fact]
    public void AboutSettingsViewModel_ExposesVersionAndDisclaimerText()
    {
        var vm = new AboutSettingsViewModel();

        Assert.StartsWith("v", vm.AppVersion);
        Assert.DoesNotContain("-pro", vm.AppVersion);
        Assert.False(string.IsNullOrWhiteSpace(vm.EnvironmentStatusText));
        Assert.Contains("Python", vm.EnvironmentStatusText);
        Assert.Contains("ONNX", vm.EnvironmentStatusText);

        Assert.NotNull(vm.EnvironmentStatusLines);
        Assert.True(vm.EnvironmentStatusLines.Count >= 17);
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("Python Environment"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("yFinance"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("Pandas"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("Polars"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("PyArrow"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("SciPy"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("pandas-ta"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("scikit-learn"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("arch"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("statsmodels"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("tslearn"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("pywin32"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("ONNX Runtime"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("trend_predictor.onnx") && line.Contains("PyTorch"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("trend_predictor_tf.onnx") && line.Contains("TensorFlow"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("trend_predictor_lgbm.onnx") && line.Contains("LightGBM"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("ONNX Model Training Tools"));

        LocalizationManager.Instance.Initialize("en");
        Assert.Equal(LocalizationManager.Instance["Disclaimer_Message"], vm.DisclaimerText);
        Assert.NotEqual("[Disclaimer_Message]", vm.DisclaimerText);
    }

    [Fact]
    public void EnvironmentStatusLines_BeforeInspectionCompletes_ShowCheckingPlaceholders()
    {
        var gate = new TaskCompletionSource<PythonEnvironmentSnapshot>();
        var vm = new AboutSettingsViewModel(new FakeInspector(_ => gate.Task));

        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("Python Environment") && line.Contains("Checking"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("yFinance") && line.Contains("Checking"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("ONNX Model Training Tools") && line.Contains("Checking"));

        // ONNX Runtime / model-file lines need no interpreter and are resolved immediately.
        Assert.Contains(vm.EnvironmentStatusLines, line => line.Contains("ONNX Runtime") && !line.Contains("Checking"));

        gate.SetResult(PythonEnvironmentSnapshot.NotInstalled);
    }

    [Fact]
    public async Task EnvironmentStatusLines_AfterInspection_ReflectRealVersions()
    {
        var snapshot = new PythonEnvironmentSnapshot(
            InterpreterInstalled: true,
            PythonVersion: "3.13.7",
            PackageVersions: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["yfinance"] = "0.2.99",
                ["pandas"] = "2.9.9",
                ["arch"] = null,          // queried but not installed
                ["torch"] = "2.5.1",
                ["lightgbm"] = "4.5.0",
                // tensorflow intentionally absent
            });

        var vm = new AboutSettingsViewModel(new FakeInspector(_ => Task.FromResult(snapshot)));
        await vm.EnvironmentStatusLoadTask;

        Assert.DoesNotContain(vm.EnvironmentStatusLines, line => line.Contains("Checking"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "Python Environment: Installed (v3.13.7)");
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "yFinance (Market Data): Installed (v0.2.99)");
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "Pandas (DataFrames): Installed (v2.9.9)");
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "arch (EGARCH Volatility): Not Installed");

        var training = Assert.Single(vm.EnvironmentStatusLines.Where(l => l.StartsWith("ONNX Model Training Tools")));
        Assert.Contains("PyTorch v2.5.1", training);
        Assert.Contains("LightGBM v4.5.0", training);
        Assert.DoesNotContain("TensorFlow", training);
    }

    [Fact]
    public async Task EnvironmentStatusLines_WhenInterpreterMissing_ShowStandby()
    {
        var vm = new AboutSettingsViewModel(new FakeInspector(_ => Task.FromResult(PythonEnvironmentSnapshot.NotInstalled)));
        await vm.EnvironmentStatusLoadTask;

        Assert.DoesNotContain(vm.EnvironmentStatusLines, line => line.Contains("Checking"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "Python Environment: Not Installed (Standby)");
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "yFinance (Market Data): Standby");
        Assert.Contains(vm.EnvironmentStatusLines, line => line.StartsWith("ONNX Model Training Tools") && line.Contains("Standby"));
    }

    [Fact]
    public async Task Constructor_WhenInspectorThrows_FallsBackWithoutFaulting()
    {
        var vm = new AboutSettingsViewModel(new FakeInspector(_ => throw new InvalidOperationException("boom")));

        await vm.EnvironmentStatusLoadTask; // must not throw

        Assert.DoesNotContain(vm.EnvironmentStatusLines, line => line.Contains("Checking"));
        Assert.Contains(vm.EnvironmentStatusLines, line => line == "Python Environment: Not Installed (Standby)");
    }

    private sealed class FakeInspector : IPythonEnvironmentInspector
    {
        private readonly Func<IReadOnlyCollection<string>, Task<PythonEnvironmentSnapshot>> _handler;

        public FakeInspector(Func<IReadOnlyCollection<string>, Task<PythonEnvironmentSnapshot>> handler)
            => _handler = handler;

        public Task<PythonEnvironmentSnapshot> InspectAsync(
            IReadOnlyCollection<string> distributionNames,
            CancellationToken ct = default)
            => _handler(distributionNames);
    }
}
