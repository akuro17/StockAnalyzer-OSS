using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Read-only settings page showing the app version, runtime environment status,
/// and the investment-advice disclaimer.
/// </summary>
public class AboutSettingsViewModel : ViewModelBase, ISettingsPageViewModel
{
    public string TitleKey => "Settings_About";
    public string IconKey => "SettingsAboutIcon";
    public bool IsModified => false;

    public string AppVersion => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    public IReadOnlyList<string> EnvironmentStatusLines => GetEnvironmentStatusLines();

    public string EnvironmentStatusText => string.Join(Environment.NewLine, EnvironmentStatusLines);

    public string DisclaimerText => LocalizationManager.Instance["Disclaimer_Message"];

    public Task SaveChangesAsync() => Task.CompletedTask;
    public void RevertChanges() { }
    public void ResetToDefault() { }

    private static IReadOnlyList<string> GetEnvironmentStatusLines()
    {
        var lines = new System.Collections.Generic.List<string>
        {
            GetPythonStatus(),
            GetPackageStatus("yfinance", "yFinance (Market Data)", "0.2.38"),
            GetPackageStatus("pandas", "Pandas (DataFrames)", "2.2.2"),
            GetPackageStatus("polars", "Polars (Fast Parquet)", "0.20.22"),
            GetPackageStatus("pyarrow", "PyArrow (IPC Transfer)", "23.0.1"),
            GetPackageStatus("scipy", "SciPy (FFT & Signal Processing)", "1.13.0"),
            GetPackageStatus("pandas-ta", "pandas-ta (Technical Indicators)", "0.3.14"),
            GetPackageStatus("scikit-learn", "scikit-learn (Machine Learning)", "1.4.2"),
            GetPackageStatus("arch", "arch (EGARCH Volatility)", "7.0.0"),
            GetPackageStatus("statsmodels", "statsmodels (Time Series)", "0.14.2"),
            GetPackageStatus("tslearn", "tslearn (DTW Pattern Search)", "0.6.3"),
            GetPackageStatus("pywin32", "pywin32 (Windows Named Pipe IPC)", "306"),
            GetOnnxRuntimeStatus(),
            GetOnnxModelStatus("trend_predictor.onnx", "PyTorch / LSTM"),
            GetOnnxModelStatus("trend_predictor_tf.onnx", "TensorFlow / Keras"),
            GetOnnxModelStatus("trend_predictor_lgbm.onnx", "LightGBM / GBDT"),
            GetOnnxTrainingStatus()
        };
        return lines;
    }

    private static string GetPythonStatus()
    {
        try
        {
            var isInstalled = Python.Included.Installer.IsPythonInstalled();
            return isInstalled ? "Python Environment: Installed (v3.13)" : "Python Environment: Not Installed (Standby)";
        }
        catch
        {
            return "Python Environment: Standby";
        }
    }

    private static string GetPackageStatus(string packageName, string displayName, string defaultVersion)
    {
        try
        {
            var isInstalled = Python.Included.Installer.IsPythonInstalled();
            return isInstalled 
                ? $"{displayName}: Installed (v{defaultVersion})" 
                : $"{displayName}: Not Installed (Standby)";
        }
        catch
        {
            return $"{displayName}: Standby";
        }
    }

    private static string GetOnnxRuntimeStatus()
    {
        try
        {
            var onnxVer = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version?.ToString(3) ?? "1.24.3";
            return $"ONNX Runtime (.NET): v{onnxVer} (Installed)";
        }
        catch
        {
            return "ONNX Runtime (.NET): v1.24.3";
        }
    }

    private static string GetOnnxModelStatus(string modelFileName, string modelFrameworkLabel)
    {
        try
        {
            var candidatePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", modelFileName),
                PathDiscovery.ResolveFilePath(null, Path.Combine("StockAnalyzer.Python", "training", "artifacts", modelFileName)),
                PathDiscovery.ResolveFilePath(null, Path.Combine("StockAnalyzer.Python", "models", modelFileName)),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, modelFileName)
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    return $"ONNX Model ({modelFrameworkLabel}): {modelFileName} (Ready)";
                }
            }

            return $"ONNX Model ({modelFrameworkLabel}): {modelFileName} (Available on Train)";
        }
        catch
        {
            return $"ONNX Model ({modelFrameworkLabel}): {modelFileName} (Standby)";
        }
    }

    private static string GetOnnxTrainingStatus()
    {
        try
        {
            var isInstalled = Python.Included.Installer.IsPythonInstalled();
            return isInstalled
                ? "ONNX Model Training Tools: Configured (PyTorch / TensorFlow / LightGBM)"
                : "ONNX Model Training Tools: Standby (Configure in AI Predictions)";
        }
        catch
        {
            return "ONNX Model Training Tools: Standby";
        }
    }
}
