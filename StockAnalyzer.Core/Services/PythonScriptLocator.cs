using System;
using System.IO;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Resolves the on-disk location of a script under <c>StockAnalyzer.Python/</c>, tolerating the
/// different working directories of a dev run (repo checkout), a published build (Python sources
/// copied beside the executable) and the test host.
/// </summary>
/// <remarks>
/// Extracted from two byte-identical resolution blocks previously inlined in
/// <see cref="PythonService"/> so a second caller — the training-orchestrator launcher — shares
/// exactly one search strategy. The behavior is unchanged: try
/// <see cref="PathDiscovery.ResolveFilePath(string?, string)"/> first, then walk up from the app
/// base directory looking for <c>&lt;dir&gt;/StockAnalyzer.Python/&lt;script&gt;</c>.
/// </remarks>
public static class PythonScriptLocator
{
    /// <summary>Folder, relative to the solution root, that holds the Python sources.</summary>
    public const string PythonRootFolderName = "StockAnalyzer.Python";

    /// <summary>
    /// Returns the absolute path of the script named by <paramref name="relativeScriptPath"/>
    /// (relative to <see cref="PythonRootFolderName"/>, e.g. <c>update_pipeline.py</c> or
    /// <c>training/run_training.py</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="relativeScriptPath"/> is null or blank.</exception>
    /// <exception cref="FileNotFoundException">No candidate path exists on disk.</exception>
    public static string Resolve(string relativeScriptPath)
    {
        if (string.IsNullOrWhiteSpace(relativeScriptPath))
        {
            throw new ArgumentException("Script path must be provided.", nameof(relativeScriptPath));
        }

        var normalized = relativeScriptPath.Replace('\\', '/').TrimStart('/');
        var resolved = PathDiscovery.ResolveFilePath(null, $"{PythonRootFolderName}/{normalized}");

        if (!File.Exists(resolved))
        {
            var current = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, PythonRootFolderName, normalized);
                if (File.Exists(candidate))
                {
                    resolved = candidate;
                    break;
                }

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"Python script not found: {resolved}");
        }

        return resolved;
    }
}
