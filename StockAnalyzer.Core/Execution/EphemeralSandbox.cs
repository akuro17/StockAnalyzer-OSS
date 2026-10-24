using System;
using System.IO;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Execution;

/// <summary>
/// Where ephemeral (throw-away) training output may live: <c>&lt;OS temp&gt;/StockAnalyzer/ephemeral</c>. The one definition of that
/// location; <c>run_training.py</c> mirrors the two folder names (a test pins that both sides agree).
/// </summary>
public static class EphemeralSandbox
{
    /// <summary>Folder below <see cref="PathDiscovery.TempFolderName"/> that holds every ephemeral session.</summary>
    public const string FolderName = "ephemeral";

    /// <summary>The sandbox root. Read on each use because the OS temp folder can differ between processes and tests.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName, FolderName);

    /// <summary>
    /// True when <paramref name="path"/>, normalised with <see cref="Path.GetFullPath(string)"/>, lies strictly below <see cref="Root"/>
    /// (the root itself does not count). A relative path is never inside: it would resolve against the current directory.
    /// </summary>
    public static bool IsStrictlyInside(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.Length > root.Length
            && full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
