using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// What decides which MCP servers share one board (spec 5.12): the OS user and the daily data folder the tools really read. The pipe name is built from both, so two
/// servers of different users, or pointing at different data folders, never meet. <see cref="Normalize"/> is the only definition of "the same folder".
/// </summary>
public static class DiscussionSharingKey
{
    /// <summary>Fixed part of the pipe name; the number is the wire generation of the name layout.</summary>
    public const string PipeNamePrefix = "StockAnalyzer.McpServer.Discussion.v1";

    private const int HashCharacters = 8;

    /// <summary>
    /// The folder as one spelling: absolute (<c>.</c>/<c>..</c> collapsed, <c>/</c> turned into the platform separator), without trailing separators (a drive or file-system
    /// root keeps its own), and on Windows in lower case. Junctions, <c>subst</c> drives, 8.3 names and UNC-versus-mapped spellings are deliberately NOT resolved.
    /// </summary>
    public static string Normalize(string resolvedDailyDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedDailyDataPath);
        string full = Path.GetFullPath(resolvedDailyDataPath);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (full.Length > root.Length)
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>The folder the data tools read: the same call <c>ReadOnlyDataTree</c> makes, so the key follows the folder the tools really use.</summary>
    public static string ResolveDataFolder(string? configuredDailyDataPath)
        => PathDiscovery.ResolveDataPathWithoutCreating(configuredDailyDataPath, MarketDataSettings.DefaultDailyDataPath, "*.parquet");

    /// <summary>First eight lower-case hex characters of the SHA-256 of the UTF-8 text.</summary>
    public static string Hash8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant()[..HashCharacters];
    }

    /// <summary><c>dataHash8</c>: the part of the key the status shows so that the owner can compare two clients.</summary>
    public static string DataHash8(string resolvedDailyDataPath) => Hash8(Normalize(resolvedDailyDataPath));

    public static string UserHash8(string userName) => Hash8(userName);

    /// <summary>The pipe name of one user and one data folder. A non-empty <paramref name="testSuffix"/> (tests only) gives a private name that never meets a real board.</summary>
    public static string PipeName(string userName, string resolvedDailyDataPath, string? testSuffix = null)
    {
        string name = $"{PipeNamePrefix}.{UserHash8(userName)}.{DataHash8(resolvedDailyDataPath)}";
        return string.IsNullOrEmpty(testSuffix) ? name : $"{name}.{testSuffix}";
    }
}
