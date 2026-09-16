using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Read-only settings page showing the app version, the <b>actual</b> local runtime environment
/// status (embedded Python interpreter and the installed version of each Python distribution it
/// depends on), and the investment-advice disclaimer.
/// <para>
/// The environment lines are shown immediately as "Checking…" placeholders and then replaced with
/// live values resolved by <see cref="IPythonEnvironmentInspector"/>. Opening this page never
/// triggers a Python setup or <c>pip install</c>.
/// </para>
/// </summary>
public class AboutSettingsViewModel : ViewModelBase, ISettingsPageViewModel
{
    /// <summary>
    /// Display manifest for this page: the distribution name (as understood by
    /// <c>importlib.metadata.version</c>) paired with its human-readable label, in display order.
    /// This is the ordering/labelling source of truth for the About page only; it is a curated
    /// subset (installer bootstrap packages such as <c>setuptools</c>/<c>wheel</c> are omitted) and
    /// therefore intentionally not derived from <c>IStockAnalyzerSettings.PythonEssentialPackages</c>.
    /// </summary>
    private static readonly IReadOnlyList<(string Distribution, string Label)> PackageManifest = new[]
    {
        ("yfinance", "yFinance (Market Data)"),
        ("pandas", "Pandas (DataFrames)"),
        ("polars", "Polars (Fast Parquet)"),
        ("pyarrow", "PyArrow (IPC Transfer)"),
        ("scipy", "SciPy (FFT & Signal Processing)"),
        ("pandas-ta", "pandas-ta (Technical Indicators)"),
        ("scikit-learn", "scikit-learn (Machine Learning)"),
        ("arch", "arch (EGARCH Volatility)"),
        ("statsmodels", "statsmodels (Time Series)"),
        ("tslearn", "tslearn (DTW Pattern Search)"),
        ("pywin32", "pywin32 (Windows Named Pipe IPC)"),
    };

    /// <summary>Training frameworks probed for the "ONNX Model Training Tools" line.</summary>
    private static readonly IReadOnlyList<(string Distribution, string Label)> TrainingFrameworkManifest = new[]
    {
        ("torch", "PyTorch"),
        ("tensorflow", "TensorFlow"),
        ("lightgbm", "LightGBM"),
    };

    /// <summary>Deployed ONNX model files probed on the local filesystem.</summary>
    private static readonly IReadOnlyList<(string FileName, string FrameworkLabel)> OnnxModelManifest = new[]
    {
        ("trend_predictor.onnx", "PyTorch / LSTM"),
        ("trend_predictor_tf.onnx", "TensorFlow / Keras"),
        ("trend_predictor_lgbm.onnx", "LightGBM / GBDT"),
    };

    private const string PlaceholderSuffix = "Checking…";

    private readonly IPythonEnvironmentInspector _inspector;

    public string TitleKey => "Settings_About";
    public string IconKey => "SettingsAboutIcon";
    public bool IsModified => false;

    public string AppVersion => $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";

    /// <summary>Live-updating environment status lines bound by the view.</summary>
    public ObservableCollection<string> EnvironmentStatusLines { get; } = new();

    public string EnvironmentStatusText => string.Join(Environment.NewLine, EnvironmentStatusLines);

    public string DisclaimerText => LocalizationManager.Instance["Disclaimer_Message"];

    /// <summary>
    /// The background inspection kicked off by the constructor. Exposed for tests to await; not
    /// intended for view consumption.
    /// </summary>
    internal Task EnvironmentStatusLoadTask { get; }

    public Task SaveChangesAsync() => Task.CompletedTask;
    public void RevertChanges() { }
    public void ResetToDefault() { }

    public AboutSettingsViewModel(IPythonEnvironmentInspector inspector)
    {
        _inspector = inspector ?? new NullPythonEnvironmentInspector();

        // Populate the skeleton synchronously so the view (and unit tests) always see a complete,
        // stably-ordered list; the Python-dependent entries start as "Checking…" placeholders.
        SetLines(ComposeLines(snapshot: null));

        EnvironmentStatusLoadTask = LoadEnvironmentStatusAsync();
    }

    /// <summary>Designer / test fallback: no interpreter probe is performed.</summary>
    public AboutSettingsViewModel()
        : this(new NullPythonEnvironmentInspector())
    {
    }

    private async Task LoadEnvironmentStatusAsync()
    {
        PythonEnvironmentSnapshot snapshot;
        try
        {
            var distributions = PackageManifest.Select(p => p.Distribution)
                .Concat(TrainingFrameworkManifest.Select(f => f.Distribution))
                .ToArray();
            snapshot = await _inspector.InspectAsync(distributions, CancellationToken.None);
        }
        catch
        {
            snapshot = PythonEnvironmentSnapshot.NotInstalled;
        }

        // No ConfigureAwait(false) above: in the running app this continuation resumes on the UI
        // thread, matching the fire-and-forget pattern used by AIPredictionsSettingsViewModel.
        SetLines(ComposeLines(snapshot));
    }

    /// <summary>
    /// Builds the full ordered line list. When <paramref name="snapshot"/> is <c>null</c> the
    /// Python-dependent lines are rendered as "Checking…" placeholders; the ONNX Runtime and ONNX
    /// model-file lines are always resolved live (they need no interpreter).
    /// </summary>
    private static List<string> ComposeLines(PythonEnvironmentSnapshot? snapshot)
    {
        var lines = new List<string>(PackageManifest.Count + OnnxModelManifest.Count + 3)
        {
            ComposePythonLine(snapshot)
        };

        foreach (var (distribution, label) in PackageManifest)
        {
            lines.Add(ComposePackageLine(label, distribution, snapshot));
        }

        lines.Add(GetOnnxRuntimeStatus());

        foreach (var (fileName, frameworkLabel) in OnnxModelManifest)
        {
            lines.Add(GetOnnxModelStatus(fileName, frameworkLabel));
        }

        lines.Add(ComposeTrainingToolsLine(snapshot));

        return lines;
    }

    private static string ComposePythonLine(PythonEnvironmentSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return $"Python Environment: {PlaceholderSuffix}";
        }

        return snapshot.InterpreterInstalled
            ? $"Python Environment: Installed (v{snapshot.PythonVersion})"
            : "Python Environment: Not Installed (Standby)";
    }

    private static string ComposePackageLine(string label, string distribution, PythonEnvironmentSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return $"{label}: {PlaceholderSuffix}";
        }

        if (!snapshot.InterpreterInstalled)
        {
            return $"{label}: Standby";
        }

        return snapshot.PackageVersions.TryGetValue(distribution, out var version) && !string.IsNullOrWhiteSpace(version)
            ? $"{label}: Installed (v{version})"
            : $"{label}: Not Installed";
    }

    private static string ComposeTrainingToolsLine(PythonEnvironmentSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return $"ONNX Model Training Tools: {PlaceholderSuffix}";
        }

        if (!snapshot.InterpreterInstalled)
        {
            return "ONNX Model Training Tools: Standby (Configure in AI Predictions)";
        }

        var installed = TrainingFrameworkManifest
            .Select(f => snapshot.PackageVersions.TryGetValue(f.Distribution, out var v) && !string.IsNullOrWhiteSpace(v)
                ? $"{f.Label} v{v}"
                : null)
            .Where(entry => entry is not null)
            .ToArray();

        return installed.Length == 0
            ? "ONNX Model Training Tools: Standby (Configure in AI Predictions)"
            : $"ONNX Model Training Tools: Configured ({string.Join(", ", installed)})";
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

    /// <summary>
    /// Replaces the contents of <see cref="EnvironmentStatusLines"/> in place (per-index) so bound
    /// views update incrementally, then notifies <see cref="EnvironmentStatusText"/>.
    /// </summary>
    private void SetLines(IReadOnlyList<string> newLines)
    {
        if (EnvironmentStatusLines.Count == newLines.Count)
        {
            for (var i = 0; i < newLines.Count; i++)
            {
                if (!string.Equals(EnvironmentStatusLines[i], newLines[i], StringComparison.Ordinal))
                {
                    EnvironmentStatusLines[i] = newLines[i];
                }
            }
        }
        else
        {
            EnvironmentStatusLines.Clear();
            foreach (var line in newLines)
            {
                EnvironmentStatusLines.Add(line);
            }
        }

        OnPropertyChanged(nameof(EnvironmentStatusText));
    }

    /// <summary>Inert inspector used by the designer/test constructor.</summary>
    private sealed class NullPythonEnvironmentInspector : IPythonEnvironmentInspector
    {
        public Task<PythonEnvironmentSnapshot> InspectAsync(
            IReadOnlyCollection<string> distributionNames,
            CancellationToken ct = default)
            => Task.FromResult(PythonEnvironmentSnapshot.NotInstalled);
    }
}
