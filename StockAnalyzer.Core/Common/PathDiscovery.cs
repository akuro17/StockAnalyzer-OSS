using StockAnalyzer.Core.Models.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StockAnalyzer.Core.Common;

public static class PathDiscovery
{
    private const string DataFolderName = "Data";

    /// <summary>Folder below the OS temp directory that holds every StockAnalyzer temporary or lease file (never <c>Data</c>).</summary>
    public const string TempFolderName = "StockAnalyzer";
    public const string UiTestEnabledEnvironmentVariable = "SA_UI_TESTS_ENABLED";
    public const string UiTestDataRootEnvironmentVariable = "SA_UI_TEST_DATA_ROOT";
    private const string SolutionFileName = "StockAnalyzer.sln";
    private const string ModelsFolderName = "Models";
    private const string ModelGenerationsFolderName = "Generations";
    private const string TrainingWizardArtifactsFolderName = "TW";
    // Fallback only: used when the configured path has no filename component.
    // PredictionSettings.Validate() rejects an empty ModelPath, so production never hits this.
    private const string DefaultPredictionModelFileName = "trend_predictor.onnx";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Defense-in-depth guard for the `fileName` argument of the Resolve*Path helpers below:
    /// strips path-traversal segments, invalid filename characters, and Windows-reserved
    /// device names so a caller passing untrusted input can never escape the resolved
    /// Config/Portfolio/Templates directory. All current call sites pass fixed constant or
    /// GUID-derived names, so this has no effect on existing behavior.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;

        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Where(c => !invalidChars.Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
        // '/' and '\' are already stripped above, so a bare ".." can no longer act as a
        // directory-traversal segment when this name is later passed through Path.Combine.
        // Kept as an extra defense-in-depth layer in case the separator-stripping logic
        // above is ever changed.
        cleaned = cleaned.Replace("..", string.Empty);

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedDeviceNames.Contains(nameWithoutExtension))
        {
            cleaned = "_" + cleaned;
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "_" : cleaned;
    }

    /// <summary>
    /// Creates <paramref name="preferredDir"/> when it does not exist and returns it. When it cannot be created the
    /// (not created) path is returned all the same, WITHOUT redirecting to the OS temp folder or the base directory
    /// (owner decision D-2', 2026-10-30): persistent data lives below <c>Data</c> only. The caller then fails at its own
    /// write (for example <see cref="UnauthorizedAccessException"/>) and reads see "no file", which the callers already
    /// handle. The read-only mode is announced separately by <c>DataStorageProbe</c> and the status banner.
    /// Under UI automation (<see cref="UiTestEnabledEnvironmentVariable"/> = 1) a creation failure is still thrown, because an
    /// isolated run must never continue on a path that is not its own.
    /// </summary>
    private static string EnsureDirectory(string preferredDir)
    {
        try
        {
            if (!Directory.Exists(preferredDir))
            {
                Directory.CreateDirectory(preferredDir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            if (Environment.GetEnvironmentVariable(UiTestEnabledEnvironmentVariable) == "1")
            {
                throw;
            }
        }

        return preferredDir;
    }

    public static string ResolveDataPath(string? configPath, string defaultFolderName = StockAnalyzer.Core.Models.MarketDataSettings.DefaultDailyDataPath, string? filePattern = null)
    {
        Log($"[ResolveDataPath] configPath: {configPath}, default: {defaultFolderName}, pattern: {filePattern}");
        if (Environment.GetEnvironmentVariable(UiTestEnabledEnvironmentVariable) == "1")
        {
            string isolatedRoot = Environment.GetEnvironmentVariable(UiTestDataRootEnvironmentVariable)
                ?? throw new InvalidOperationException("UI automation data root is required when UI tests are enabled.");
            string requestedPath = configPath ?? defaultFolderName;
            string isolatedPath = ResolveUiTestDataPath(isolatedRoot, requestedPath);
            Directory.CreateDirectory(isolatedPath);
            return isolatedPath;
        }

        // 1. If absolute, use it
        if (!string.IsNullOrEmpty(configPath) && Path.IsPathRooted(configPath))
        {
            Log($"[ABSOLUTE] {configPath}");
            return configPath;
        }

        var relativePath = configPath ?? defaultFolderName;

        // 2. Try relative to BaseDirectory
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        Log($"[BaseDir] {baseDir}");

        // 3. WebAI Fix: Prioritize 'Smart Discovery' (Step 3) for relative paths involving 'Data'.
        // We MUST skip Step 2 entirely for 'Data' paths to avoid falling into local bin/Debug folders.
        bool isDataPath = relativePath.Contains(DataFolderName, StringComparison.OrdinalIgnoreCase);

        // Sanitize relative path when resolving Data paths so it never escapes BaseDirectory via "../" in release mode
        var safeRelativePath = relativePath;
        if (isDataPath)
        {
            int dataIdx = relativePath.IndexOf(DataFolderName, StringComparison.OrdinalIgnoreCase);
            if (dataIdx >= 0)
            {
                safeRelativePath = relativePath.Substring(dataIdx);
            }
        }
        var candidate = Path.GetFullPath(Path.Combine(baseDir, safeRelativePath));

        if (!isDataPath)
        {
            if (Directory.Exists(candidate) && (filePattern == null || Directory.GetFiles(candidate, filePattern).Any()))
            {
                Log($"[FOUND RELATIVE] {candidate}");
                return candidate;
            }
        }

        string resolvedPath = "";
        string? fallbackDataDir = null;
        
        // 3. Fallback: Search upwards for project root (contains 'Data' folder or '.sln')
        var current = baseDir;
        while (!string.IsNullOrEmpty(current))
        {
            var dataDir = Path.Combine(current, DataFolderName);
            bool slnExists = File.Exists(Path.Combine(current, SolutionFileName));
            bool dataExists = Directory.Exists(dataDir);

            if (slnExists)
            {
                int dataIdx = relativePath.IndexOf(DataFolderName, StringComparison.OrdinalIgnoreCase);
                if (dataIdx >= 0)
                {
                    var subPath = relativePath.Substring(dataIdx + DataFolderName.Length).TrimStart('\\', '/');
                    resolvedPath = Path.GetFullPath(Path.Combine(current, DataFolderName, subPath));
                }
                else
                {
                    resolvedPath = Path.GetFullPath(Path.Combine(current, relativePath));
                }
                break;
            }
            else if (dataExists && fallbackDataDir == null)
            {
                int dataIdx = relativePath.IndexOf(DataFolderName, StringComparison.OrdinalIgnoreCase);
                if (dataIdx >= 0)
                {
                    var subPath = relativePath.Substring(dataIdx + DataFolderName.Length).TrimStart('\\', '/');
                    fallbackDataDir = Path.GetFullPath(Path.Combine(current, DataFolderName, subPath));
                }
                else
                {
                    fallbackDataDir = Path.GetFullPath(Path.Combine(current, relativePath));
                }
            }

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        if (string.IsNullOrEmpty(resolvedPath))
        {
            resolvedPath = fallbackDataDir ?? candidate;
        }

        // OSS requirement: Auto-create directory if it doesn't exist
        try
        {
            if (!string.IsNullOrEmpty(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                Directory.CreateDirectory(resolvedPath);
            }
        }
        catch
        {
            // Ignore failure to guarantee resilience (e.g. read-only media)
        }

        return resolvedPath;
    }

    /// <summary>
    /// The application data root (<c>Data</c>) as <see cref="ResolveDataPathWithoutCreating"/> returns it, without creating anything. The one place that
    /// names the root for the start-up write probe (<see cref="DataStorageProbe"/>), so no caller spells the folder name itself.
    /// </summary>
    public static string ResolveDataRootWithoutCreating(string? baseDirectory = null)
        => ResolveDataPathWithoutCreating(null, DataFolderName, baseDirectory: baseDirectory);

    /// <summary>
    /// Read-only counterpart of <see cref="ResolveDataPath"/>: returns the path that
    /// <see cref="ResolveDataPath"/> would return for the same arguments, but never creates a
    /// directory (the caller checks <see cref="Directory.Exists(string?)"/>). Used by hosts that
    /// must not modify the file system. <paramref name="baseDirectory"/> defaults to
    /// <see cref="AppDomain.BaseDirectory"/>; tests pass a temporary tree.
    /// </summary>
    public static string ResolveDataPathWithoutCreating(string? configPath, string defaultFolderName = StockAnalyzer.Core.Models.MarketDataSettings.DefaultDailyDataPath, string? filePattern = null, string? baseDirectory = null)
    {
        if (Environment.GetEnvironmentVariable(UiTestEnabledEnvironmentVariable) == "1")
        {
            string isolatedRoot = Environment.GetEnvironmentVariable(UiTestDataRootEnvironmentVariable)
                ?? throw new InvalidOperationException("UI automation data root is required when UI tests are enabled.");
            return ResolveUiTestDataPath(isolatedRoot, configPath ?? defaultFolderName);
        }

        if (!string.IsNullOrEmpty(configPath) && Path.IsPathRooted(configPath))
        {
            return configPath;
        }

        var relativePath = configPath ?? defaultFolderName;
        var baseDir = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
        int dataIdx = relativePath.IndexOf(DataFolderName, StringComparison.OrdinalIgnoreCase);
        bool isDataPath = dataIdx >= 0;
        var candidate = Path.GetFullPath(Path.Combine(baseDir, isDataPath ? relativePath.Substring(dataIdx) : relativePath));

        if (!isDataPath
            && Directory.Exists(candidate)
            && (filePattern == null || Directory.GetFiles(candidate, filePattern).Any()))
        {
            return candidate;
        }

        string? fallbackDataDir = null;
        var current = baseDir;
        while (!string.IsNullOrEmpty(current))
        {
            string resolved = isDataPath
                ? Path.GetFullPath(Path.Combine(current, DataFolderName, relativePath.Substring(dataIdx + DataFolderName.Length).TrimStart('\\', '/')))
                : Path.GetFullPath(Path.Combine(current, relativePath));

            if (File.Exists(Path.Combine(current, SolutionFileName)))
            {
                return resolved;
            }

            if (Directory.Exists(Path.Combine(current, DataFolderName)) && fallbackDataDir == null)
            {
                fallbackDataDir = resolved;
            }

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return fallbackDataDir ?? candidate;
    }

    /// <summary>Read-only counterpart of <see cref="ResolveTemplatesDirectory"/>: the same folder when it exists, never creates it (the caller checks <see cref="Directory.Exists(string?)"/>).</summary>
    public static string ResolveTemplatesDirectoryWithoutCreating(StockAnalyzer.Core.Models.Templates.TemplateType type, string? baseDirectory = null)
        => Path.Combine(ResolveDataPathWithoutCreating(null, "Data", baseDirectory: baseDirectory), "Templates", type.ToString());

    /// <summary>Read-only counterpart of <see cref="ResolveSourceIndicatorsPath"/>: same result when the folder exists, never creates it.</summary>
    public static string ResolveSourceIndicatorsPathWithoutCreating(string fileName)
        => Path.Combine(ResolveDataPathWithoutCreating(null, "Data"), "SourceIndicators", SanitizeFileName(fileName));

    /// <summary>Read-only counterpart of <see cref="ResolveDynamicPeriodDriversPath"/>: same result when the folder exists, never creates it.</summary>
    public static string ResolveDynamicPeriodDriversPathWithoutCreating(string fileName)
        => Path.Combine(ResolveDataPathWithoutCreating(null, "Data"), "DynamicPeriodDrivers", SanitizeFileName(fileName));

    /// <summary>Read-only counterpart of <see cref="ResolveConfigPath"/>: same result when the folder exists, never creates it.</summary>
    public static string ResolveConfigPathWithoutCreating(string fileName)
        => Path.Combine(ResolveDataPathWithoutCreating(null, "Data"), "Config", SanitizeFileName(fileName));

    internal static string ResolveUiTestDataPath(string isolatedRoot, string requestedPath)
    {
        if (!Path.IsPathFullyQualified(isolatedRoot))
            throw new ArgumentException("UI automation data root must be an absolute path.", nameof(isolatedRoot));

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(isolatedRoot));
        if (Path.IsPathRooted(requestedPath))
        {
            string absolute = Path.GetFullPath(requestedPath);
            if (absolute.Equals(root, PathComparison) || absolute.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                return absolute;
            throw new InvalidOperationException("UI automation data path escapes the isolated root.");
        }

        string[] segments = requestedPath.Replace('\\', '/').Split('/');
        if (segments.Length == 0 || !segments[0].Equals(DataFolderName, StringComparison.OrdinalIgnoreCase)
            || segments.Skip(1).Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".."))
            throw new InvalidOperationException("UI automation data path must remain below Data.");

        string candidate = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments.Skip(1)).ToArray()));
        if (!candidate.Equals(root, PathComparison) && !candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("UI automation data path escapes the isolated root.");
        return candidate;
    }

    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>
    /// Resolves a file path, searching upwards for the solution root if necessary.
    /// </summary>
    public static string ResolveFilePath(string? configPath, string defaultRelativePath)
    {
        var relativePath = configPath ?? defaultRelativePath;

        if (!string.IsNullOrEmpty(configPath) && Path.IsPathRooted(configPath))
        {
            return configPath;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var current = baseDir;
        string? fallbackFile = null;

        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.GetFullPath(Path.Combine(current, relativePath));
            bool slnExists = File.Exists(Path.Combine(current, SolutionFileName));
            bool fileExists = File.Exists(candidate);

            if (slnExists)
            {
                return candidate;
            }
            else if (fileExists && fallbackFile == null)
            {
                fallbackFile = candidate;
            }

            var parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        return fallbackFile ?? Path.GetFullPath(Path.Combine(baseDir, relativePath));
    }

    public static string ResolveConfigPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var configDir = EnsureDirectory(Path.Combine(dataDir, "Config"));
        return Path.Combine(configDir, fileName);
    }

    public static string ResolvePortfolioPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var portfolioDir = EnsureDirectory(Path.Combine(dataDir, "Portfolios"));
        return Path.Combine(portfolioDir, fileName);
    }

    public static string ResolveBacktestReportSettingsPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var backtestDir = EnsureDirectory(Path.Combine(dataDir, "Backtest"));
        return Path.Combine(backtestDir, fileName);
    }

    public static string ResolveBacktestDefaultSettingsPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var backtestDir = EnsureDirectory(Path.Combine(dataDir, "Backtest"));
        return Path.Combine(backtestDir, fileName);
    }

    public static string ResolveBacktestReportExportPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var reportsSubDir = Path.Combine("Backtest", "Reports");
        var reportsDir = EnsureDirectory(Path.Combine(dataDir, reportsSubDir));
        return Path.Combine(reportsDir, fileName);
    }

    public static string ResolveIndicatorDefaultsPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var defaultsDir = EnsureDirectory(Path.Combine(dataDir, "IndicatorDefaults"));
        return Path.Combine(defaultsDir, fileName);
    }

    public static string ResolveSourceIndicatorsPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var sourceDir = EnsureDirectory(Path.Combine(dataDir, "SourceIndicators"));
        return Path.Combine(sourceDir, fileName);
    }

    public static string ResolveDynamicPeriodDriversPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var driverDir = EnsureDirectory(Path.Combine(dataDir, "DynamicPeriodDrivers"));
        return Path.Combine(driverDir, fileName);
    }

    public static string ResolveTemplatesDirectory(StockAnalyzer.Core.Models.Templates.TemplateType? type = null)
    {
        var dataDir = ResolveDataPath(null, "Data");
        var subDir = type.HasValue ? Path.Combine("Templates", type.Value.ToString()) : "Templates";
        return EnsureDirectory(Path.Combine(dataDir, subDir));
    }

    public static string ResolveTemplatePath(StockAnalyzer.Core.Models.Templates.TemplateType type, string fileName)
    {
        var dir = ResolveTemplatesDirectory(type);
        return Path.Combine(dir, SanitizeFileName(fileName));
    }

    /// <summary>
    /// Resolves (and creates) the per-run experiment log directory
    /// <c>&lt;DataRoot&gt;/Experiments/&lt;runId&gt;/</c> used by <c>ExperimentLogService</c> to
    /// record a training run's <c>config.json</c> / <c>metrics.json</c>. <paramref name="runId"/>
    /// is sanitized like every other <c>Resolve*Path</c> helper's <c>fileName</c> argument, so it
    /// cannot escape the Experiments directory via path-traversal segments.
    /// </summary>
    public static string ResolveExperimentsDirectory(string runId)
    {
        var sanitizedRunId = SanitizeFileName(runId);
        var dataDir = ResolveDataPath(null, "Data");
        var subDir = Path.Combine("Experiments", sanitizedRunId);
        return EnsureDirectory(Path.Combine(dataDir, subDir));
    }

    /// <summary>
    /// Resolves the runtime location of the trend-predictor ONNX model. Priority:
    /// (1) an absolute <paramref name="configuredPath"/> is returned verbatim;
    /// (2) <c>&lt;DataRoot&gt;/Models/&lt;filename&gt;</c> when that file exists (the canonical store);
    /// (3) <c>&lt;BaseDirectory&gt;/&lt;configuredPath&gt;</c> when that file exists (back-compat with a
    /// model shipped next to the executable and with test fixtures under <c>Assets/</c>);
    /// (4) otherwise the canonical <c>&lt;DataRoot&gt;/Models/&lt;filename&gt;</c> path, with the Models
    /// directory created so the caller's not-found error points at the canonical location.
    /// Only the filename component of <paramref name="configuredPath"/> is used for the Models
    /// directory, run through <see cref="SanitizeFileName"/>, so a value containing "../" cannot
    /// escape it.
    /// </summary>
    public static string ResolvePredictionModelPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var fileName = SanitizeFileName(Path.GetFileName(configuredPath ?? string.Empty));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = DefaultPredictionModelFileName;
        }

        // Same behaviour as ResolveConfigPath / ResolvePortfolioPath: the folder is created when possible and never redirected.
        var modelsDir = EnsureDirectory(Path.Combine(ResolveDataPath(null, "Data"), ModelsFolderName));

        var dataCandidate = Path.Combine(modelsDir, fileName);
        if (File.Exists(dataCandidate))
        {
            return dataCandidate;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var binCandidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath));
            if (File.Exists(binCandidate))
            {
                return binCandidate;
            }
        }

        return dataCandidate;
    }

    /// <summary>
    /// <c>&lt;DataRoot&gt;/TrainingArtifacts/TW</c>: where the Training Wizard writes its models and sidecars (same location as <c>dataset.TRAINING_WIZARD_ARTIFACTS_DIR</c>
    /// in the trainer). The folder is not created here; it may not exist yet.
    /// </summary>
    public static string ResolveTrainingWizardArtifactsDirectory() =>
        Path.Combine(ResolveDataPath(null, DataFolderName), StockAnalyzer.Core.Models.ReinforcementLearning.RlFiles.TrainingFolder, TrainingWizardArtifactsFolderName);

    /// <summary>Canonical immutable model generation store under the prediction Models directory.</summary>
    public static string ResolveModelGenerationsDirectory()
    {
        var modelsDir = EnsureDirectory(Path.Combine(ResolveDataPath(null, DataFolderName), ModelsFolderName));
        return Path.Combine(modelsDir, ModelGenerationsFolderName);
    }

    /// <summary>Canonical immutable model generation store under the prediction Models directory without creating it.</summary>
    public static string ResolveModelGenerationsDirectoryWithoutCreating()
    {
        var dataRoot = ResolveDataPathWithoutCreating(null, DataFolderName);
        return Path.Combine(dataRoot, ModelsFolderName, ModelGenerationsFolderName);
    }

    private static void Log(string msg) { /* No-op for production */ }
}
