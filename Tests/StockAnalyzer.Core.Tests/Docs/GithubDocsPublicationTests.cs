using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Docs;

/// <summary>Verifies that top-level user guides in docs/Github/ are safe for public release and included in StockAnalyzer-OSS export plumbing.</summary>
public sealed class GithubDocsPublicationTests
{
    private const string AddTickerGuideFileName = "add-ticker-and-sync.md";

    private static readonly string[] ProhibitedSubstrings =
    {
        @"i:\", @"y:\", @"c:\users", @"\stock\", "AGENTS.md", "CLAUDE.md", ".agent", "sa_ui_interaction", "sa_architecture",
        "StockAnalyzer.Avalonia/", "localhost",
    };

    private static readonly Regex SourceFileName = new(@"\.cs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EmailAddress = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant);
    private static readonly Lazy<string> RepoRoot = new(TestRepositoryRoot.Find);

    private static string ReadRepoFile(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot.Value }.Concat(parts).ToArray()));

    [Fact]
    public void AddTickerAndSyncGuide_ExistsAndMeetsPublicDocStandards()
    {
        string path = Path.Combine(RepoRoot.Value, "docs", "Github", AddTickerGuideFileName);
        Assert.True(File.Exists(path), $"Expected guide at {path}");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Guide must be UTF-8 without a byte order mark.");

        string content = File.ReadAllText(path);
        Assert.StartsWith("# ", content, StringComparison.Ordinal);

        foreach (string prohibited in ProhibitedSubstrings)
        {
            Assert.False(content.Contains(prohibited, StringComparison.OrdinalIgnoreCase),
                "The guide contains prohibited internal reference: " + prohibited);
        }

        Assert.False(SourceFileName.IsMatch(content), "The guide names a C# source file.");
        Assert.False(EmailAddress.IsMatch(content), "The guide contains an e-mail address.");
    }

    [Fact]
    public void ReleaseAndPatchPlumbing_ExportTopLevelGithubDocsToDocsFolder()
    {
        string ossFilesScript = ReadRepoFile(".agent", "skills", "sa_release_file", "scripts", "prepare_oss_files.ps1");
        string ossPatchScript = ReadRepoFile(".agent", "skills", "sa_release_patch_file", "scripts", "prepare_oss_patch.ps1");

        Assert.Contains("Get-ChildItem -Path $GithubDocsSrcRoot -File", ossFilesScript, StringComparison.Ordinal);
        Assert.Contains("$githubDocsFileNames", ossFilesScript, StringComparison.Ordinal);
        Assert.Contains("($relPath -notmatch '[\\\\/]') -and ($githubDocsFileNames -contains $_.Name)", ossFilesScript, StringComparison.Ordinal);

        Assert.Contains("docs\\Github\\", ossPatchScript, StringComparison.Ordinal);
        Assert.Contains("$isGithubDoc", ossPatchScript, StringComparison.Ordinal);
    }
}
