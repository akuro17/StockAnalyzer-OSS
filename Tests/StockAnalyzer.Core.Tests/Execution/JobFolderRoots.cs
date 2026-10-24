#nullable enable
using System;
using System.IO;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;
using Xunit.Sdk;

namespace StockAnalyzer.Core.Tests.Execution;

/// <summary>A sandbox root (below the real one, as the trainer demands) that makes a job folder exactly as long as a path-budget test needs.</summary>
internal static class JobFolderRoots
{
    private const int UniquePrefixChars = 8;

    /// <summary>The root for which <c>&lt;root&gt;\&lt;session id&gt;\&lt;job id&gt;</c> is <paramref name="jobFolderChars"/> characters long.</summary>
    public static string WithJobFolderLength(int jobFolderChars)
    {
        int jobId = TrainingRunId.New(DateTimeOffset.UtcNow).Length;
        int sessionAndJob = 1 + EphemeralArtifactStore.SessionIdLength + 1 + jobId;
        int folderChars = jobFolderChars - sessionAndJob - EphemeralSandbox.Root.Length - 1;
        if (folderChars <= UniquePrefixChars)
        {
            throw SkipException.ForSkip($"The sandbox root {EphemeralSandbox.Root} is too long to build a job folder of {jobFolderChars} characters.");
        }

        return Path.Combine(EphemeralSandbox.Root, Guid.NewGuid().ToString("N")[..UniquePrefixChars] + new string('p', folderChars - UniquePrefixChars));
    }

    /// <summary>The longest job folder that still fits when the longest file below it has <paramref name="longestRelativePathChars"/> characters.</summary>
    public static int LongestJobFolderThatFits(int longestRelativePathChars)
        => ExecutionLimits.PathCharsLimit - longestRelativePathChars - 2; // one separator, and the path must be shorter than the limit
}
