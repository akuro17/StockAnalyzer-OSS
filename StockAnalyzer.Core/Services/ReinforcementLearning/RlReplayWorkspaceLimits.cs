using System;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Safety bounds of the temporary external-replay folders. Code contract, not a setting: a configuration file must not be able to shorten them
/// to the point where a workspace that is still being created could be swept.
/// </summary>
internal static class RlReplayWorkspaceLimits
{
    /// <summary>
    /// A folder below <c>Data\RlReplay</c> that holds no <c>.lock</c> file is removed by the sweep only when it was created longer ago than this.
    /// A live workspace creates its folder and its lock file within milliseconds, so the bound is far above that; a day also covers a machine that
    /// was suspended between the two steps. Folders younger than this are never removed.
    /// </summary>
    public static readonly TimeSpan LocklessFolderMinimumAge = TimeSpan.FromHours(24);
}
