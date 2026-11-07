#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StockAnalyzer.Core.Discussion;
using Xunit;

namespace StockAnalyzer.Core.Tests.Discussion;

/// <summary>Task T7 of the discussion board: which servers share one board (spec 5.12). The key is the only thing that decides it, so every spelling of one folder must give one key.</summary>
public sealed class DiscussionSharingKeyTests
{
    private const string Folder = @"C:\Data\Daily";

    private static string Key(string path) => DiscussionSharingKey.DataHash8(path);

    [Theory]
    [InlineData(@"c:\data\daily\")]
    [InlineData(@"C:\Data\Daily\\")]
    [InlineData(@"C:/Data/Daily")]
    [InlineData(@"C:\Data\.\Daily")]
    [InlineData(@"C:\Data\X\..\Daily")]
    [InlineData(@"C:\DATA\DAILY")]
    [InlineData(@"C:/Data/Daily/")]
    public void EverySpellingOfOneFolderGivesTheKeyOfTheStandardSpelling(string spelling)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the pipe hub (and so the rule on case) exists on Windows only
        }

        Assert.Equal(Key(Folder), Key(spelling));
    }

    [Fact]
    public void ARelativeSpellingIsResolvedAgainstTheCurrentFolderOfTheProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string absolute = Path.Combine(Environment.CurrentDirectory, "Data", "Daily");

        Assert.Equal(Key(absolute), Key(Path.Combine("Data", "Daily")));
        Assert.Equal(Key(absolute), Key(@".\Data\Daily\"));
    }

    [Fact]
    public void DifferentFoldersGiveDifferentKeys()
    {
        string[] folders = { @"C:\Data\Daily", @"C:\Data\Weekly", @"D:\Data\Daily", @"C:\Data\Daily2", @"C:\Data", @"\\server\share\Data\Daily" };

        Assert.Equal(folders.Length, folders.Select(Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ARootKeepsItsSeparatorAndIsNotEmptiedByTheTrimming()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(@"c:\", DiscussionSharingKey.Normalize(@"C:\"));
        Assert.Equal(@"c:\", DiscussionSharingKey.Normalize("C:/"));
        Assert.Equal(@"c:\data", DiscussionSharingKey.Normalize(@"C:\Data\\"));
    }

    [Fact]
    public void TheKeyIsEightLowerCaseHexCharactersAndTheSameForTheSameInput()
    {
        string key = Key(Folder);

        Assert.Matches(new Regex("^[0-9a-f]{8}$"), key);
        Assert.Equal(key, Key(Folder));
        Assert.Matches(new Regex("^[0-9a-f]{8}$"), DiscussionSharingKey.UserHash8("someone"));
    }

    [Fact]
    public void ThePipeNameCarriesTheUserTheFolderAndTheTestSuffix()
    {
        string name = DiscussionSharingKey.PipeName("someone", Folder);
        string privateName = DiscussionSharingKey.PipeName("someone", Folder, "abc123");

        Assert.Equal($"StockAnalyzer.McpServer.Discussion.v1.{DiscussionSharingKey.UserHash8("someone")}.{Key(Folder)}", name);
        Assert.Equal($"{name}.abc123", privateName);
        Assert.NotEqual(name, DiscussionSharingKey.PipeName("somebody-else", Folder));
    }

    [Fact]
    public void AnEmptyFolderIsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => DiscussionSharingKey.Normalize(" "));
    }

    [Fact]
    public void TheFolderIsTheOneTheDataToolsReadAndAConfiguredAbsolutePathIsKeptAsItIs()
    {
        string absolute = Path.Combine(Path.GetTempPath(), "dhub-key-" + Guid.NewGuid().ToString("N"));

        // A rooted configured path is returned unchanged by the shared resolver; the spelling is removed by Normalize afterwards.
        Assert.Equal(absolute, DiscussionSharingKey.ResolveDataFolder(absolute));
    }
}
