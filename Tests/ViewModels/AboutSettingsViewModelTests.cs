using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
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
}
