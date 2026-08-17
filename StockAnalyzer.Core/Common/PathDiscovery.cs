using System;
using System.IO;
using System.Linq;

namespace StockAnalyzer.Core.Common;

public static class PathDiscovery
{
    private const string DataFolderName = "Data";
    private const string SolutionFileName = "StockAnalyzer.sln";

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
        var dataDir = ResolveDataPath(null, "Data");
        var configDir = Path.Combine(dataDir, "Config");
        try
        {
            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
        }
        catch (Exception)
        {
            // Fallback strategy: System Temp directory
            try
            {
                configDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer", "Config");
                if (!Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }
            }
            catch
            {
                // Secondary fallback: App domain base directory subfolder
                configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                try
                {
                    if (!Directory.Exists(configDir))
                    {
                        Directory.CreateDirectory(configDir);
                    }
                }
                catch
                {
                    // Ultimate fallback: Just use the base directory directly
                    configDir = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
        }
        return Path.Combine(configDir, fileName);
    }

    public static string ResolvePortfolioPath(string fileName)
    {
        var dataDir = ResolveDataPath(null, "Data");
        var portfolioDir = Path.Combine(dataDir, "Portfolios");
        try
        {
            if (!Directory.Exists(portfolioDir))
            {
                Directory.CreateDirectory(portfolioDir);
            }
        }
        catch (Exception)
        {
            // Fallback strategy: System Temp directory
            try
            {
                portfolioDir = Path.Combine(Path.GetTempPath(), "StockAnalyzer", "Portfolios");
                if (!Directory.Exists(portfolioDir))
                {
                    Directory.CreateDirectory(portfolioDir);
                }
            }
            catch
            {
                // Secondary fallback: App domain base directory subfolder
                portfolioDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Portfolios");
                try
                {
                    if (!Directory.Exists(portfolioDir))
                    {
                        Directory.CreateDirectory(portfolioDir);
                    }
                }
                catch
                {
                    // Ultimate fallback: Just use the base directory directly
                    portfolioDir = AppDomain.CurrentDomain.BaseDirectory;
                }
            }
        }
        return Path.Combine(portfolioDir, fileName);
    }

    private static void Log(string msg) { /* No-op for production */ }
}
