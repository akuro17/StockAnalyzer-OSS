using System;
using System.IO;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Discussion;

/// <summary>
/// Where the discussion board may keep command metadata files (only when <see cref="DiscussionSettings.CommandLogToFile"/> is on):
/// <c>&lt;OS temp&gt;/StockAnalyzer/discussion</c>. The one definition of that location. It is a sibling of the execution sandbox folder, never below it: the
/// start-up clean-up of the execution gateway deletes orphan folders below <c>ephemeral</c>.
/// </summary>
public static class DiscussionTempFolder
{
    /// <summary>Folder below <see cref="PathDiscovery.TempFolderName"/> that holds the discussion command files.</summary>
    public const string FolderName = "discussion";

    /// <summary>The folder. Read on each use because the OS temp folder can differ between processes and tests; nothing is created here.</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), PathDiscovery.TempFolderName, FolderName);
}
