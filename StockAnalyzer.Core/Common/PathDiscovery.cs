using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StockAnalyzer.Core.Common;

public static class PathDiscovery
{
    private const string DataFolderName = "Data";
    private const string SolutionFileName = "StockAnalyzer.sln";
    private const string ModelsFolderName = "Models";
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
    /// Ensures a writable directory exists, degrading through the same three tiers used by every
    /// <c>Resolve*Path</c> helper: (1) <paramref name="preferredDir"/>; (2)
    /// <c>%TEMP%/StockAnalyzer/<paramref name="subDirRelativePath"/></c>; (3)
    /// <c>&lt;BaseDirectory&gt;/<paramref name="subDirRelativePath"/></c>; (4) the bare
    /// <see cref="AppDomain.BaseDirectory"/>. Returns the first directory that could be created or
    /// already exists. Consolidates what were four byte-identical fallback blocks.
    /// </summary>
    private static string EnsureDirectoryWithFallback(string preferredDir, string subDirRelativePath)
    {
        try
        {
            if (!Directory.Exists(preferredDir))
            {
                Directory.CreateDirectory(preferredDir);
            }
            return preferredDir;
        }
        catch (Exception)
        {
            // Fallback strategy: System Temp directory
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer", subDirRelativePath);
                if (!Directory.Exists(tempDir))
                {
                    Directory.CreateDirectory(tempDir);
                }
                return tempDir;
            }
            catch
            {
                // Secondary fallback: App domain base directory subfolder
                var baseSubDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, subDirRelativePath);
                try
                {
                    if (!Directory.Exists(baseSubDir))
                    {
                        Directory.CreateDirectory(baseSubDir);
                    }
                    return baseSubDir;
                }
                catch
                {
                    // Ultimate fallback: Just use the base directory directly
                    return AppDomain.CurrentDomain.BaseDirectory;
                }
            }
        }
    }

    public static string ResolveDataPath(string? configPath, string defaultFolderName = "Data/Daily", string? filePattern = null)
    {
        Log($"[ResolveDataPath] configPath: {configPath}, default: {defaultFolderName}, pattern: {filePattern}");
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
        var configDir = EnsureDirectoryWithFallback(Path.Combine(dataDir, "Config"), "Config");
        return Path.Combine(configDir, fileName);
    }

    public static string ResolvePortfolioPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var portfolioDir = EnsureDirectoryWithFallback(Path.Combine(dataDir, "Portfolios"), "Portfolios");
        return Path.Combine(portfolioDir, fileName);
    }

    public static string ResolveIndicatorDefaultsPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var defaultsDir = EnsureDirectoryWithFallback(Path.Combine(dataDir, "IndicatorDefaults"), "IndicatorDefaults");
        return Path.Combine(defaultsDir, fileName);
    }

    public static string ResolveSourceIndicatorsPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var sourceDir = EnsureDirectoryWithFallback(Path.Combine(dataDir, "SourceIndicators"), "SourceIndicators");
        return Path.Combine(sourceDir, fileName);
    }

    public static string ResolveDynamicPeriodDriversPath(string fileName)
    {
        fileName = SanitizeFileName(fileName);
        var dataDir = ResolveDataPath(null, "Data");
        var driverDir = EnsureDirectoryWithFallback(Path.Combine(dataDir, "DynamicPeriodDrivers"), "DynamicPeriodDrivers");
        return Path.Combine(driverDir, fileName);
    }

    public static string ResolveTemplatesDirectory(StockAnalyzer.Core.Models.Templates.TemplateType? type = null)
    {
        var dataDir = ResolveDataPath(null, "Data");
        var subDir = type.HasValue ? Path.Combine("Templates", type.Value.ToString()) : "Templates";
        return EnsureDirectoryWithFallback(Path.Combine(dataDir, subDir), subDir);
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
        return EnsureDirectoryWithFallback(Path.Combine(dataDir, subDir), subDir);
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

        // Same 3-tier degradation as ResolveConfigPath / ResolvePortfolioPath.
        var modelsDir = EnsureDirectoryWithFallback(
            Path.Combine(ResolveDataPath(null, "Data"), ModelsFolderName), ModelsFolderName);

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

    private static void Log(string msg) { /* No-op for production */ }
}
