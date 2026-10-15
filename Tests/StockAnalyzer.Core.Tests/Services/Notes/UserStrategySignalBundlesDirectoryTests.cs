#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services.Notes;

/// <summary>
/// The signal-bundle files are read and written below <see cref="UserStrategyMetadataRepository.SignalBundlesDirectory"/>, so a test
/// that saves bundles through the singleton never touches the user's real Data/Metadata (it used to replace real files such as
/// <c>MSFT.signals.json</c> and left thousands of <c>TEST_*.signals.json</c> behind).
/// </summary>
[Collection("UserStrategyMetadataRepository MarketDataProvider")]
public sealed class UserStrategySignalBundlesDirectoryTests : IDisposable
{
    private const int PollAttempts = 100;
    private const int PollIntervalMilliseconds = 50;

    private readonly string _directory = TestDataIsolationBootstrap.NewTempDirectory(TestDataIsolationBootstrap.SignalBundlesFolderName);
    private readonly string? _previous = UserStrategyMetadataRepository.SignalBundlesDirectory;

    public UserStrategySignalBundlesDirectoryTests()
    {
        Directory.CreateDirectory(_directory);
        UserStrategyMetadataRepository.SignalBundlesDirectory = _directory;
    }

    public void Dispose()
    {
        UserStrategyMetadataRepository.SignalBundlesDirectory = _previous;
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { /* best-effort cleanup */ }
    }

    private static string RealMetadataFile(string ticker) =>
        Path.Combine(PathDiscovery.ResolveDataPath(null, "Data/Metadata"), $"{ticker}.signals.json");

    [Fact]
    public void TestRun_IsIsolatedFromTheRealMetadataFolder_BeforeAnyTestSetsADirectory()
    {
        Assert.NotNull(_previous);
        string isolated = Path.GetFullPath(_previous!);
        string realFolder = Path.GetFullPath(PathDiscovery.ResolveDataPath(null, "Data/Metadata"));
        Assert.NotEqual(realFolder, isolated.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), isolated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SaveSignalBundles_WritesBelowTheConfiguredDirectory_AndNeverIntoTheRealMetadataFolder()
    {
        string ticker = $"SIGNAL_DIR_{Guid.NewGuid():N}";
        var bundles = new List<BundledSignalCondition>
        {
            new("Set1", SignalTargetType.Long, Array.Empty<ScreenerIndicatorEntry>()),
        };

        UserStrategyMetadataRepository.Instance.SaveSignalBundles(ticker, bundles);

        string expected = Path.Combine(_directory, $"{ticker}.signals.json");
        WaitUntil(() => File.Exists(expected));
        Assert.True(File.Exists(expected), expected);
        Assert.False(File.Exists(RealMetadataFile(ticker)));
    }

    [Fact]
    public void SaveSignalBundles_UsesTheDirectoryInForceWhenCalled_EvenIfItIsChangedAfterwards()
    {
        string ticker = $"SIGNAL_DIR_{Guid.NewGuid():N}";
        string other = Path.Combine(_directory, "other");
        Directory.CreateDirectory(other);

        UserStrategyMetadataRepository.Instance.SaveSignalBundles(ticker, new List<BundledSignalCondition>());
        UserStrategyMetadataRepository.SignalBundlesDirectory = other;

        string expected = Path.Combine(_directory, $"{ticker}.signals.json");
        WaitUntil(() => File.Exists(expected));
        Assert.True(File.Exists(expected), expected);
        Assert.False(File.Exists(Path.Combine(other, $"{ticker}.signals.json")));
    }

    [Fact]
    public void SaveSignalBundles_ReplacesAnExistingFile_AndLeavesNoTemporaryOrBackupFile()
    {
        string ticker = $"SIGNAL_DIR_{Guid.NewGuid():N}";
        string path = Path.Combine(_directory, $"{ticker}.signals.json");
        var first = new List<BundledSignalCondition> { new("First", SignalTargetType.Long, Array.Empty<ScreenerIndicatorEntry>()) };
        var second = new List<BundledSignalCondition> { new("Second", SignalTargetType.Short, Array.Empty<ScreenerIndicatorEntry>()) };

        UserStrategyMetadataRepository.Instance.SaveSignalBundles(ticker, first);
        WaitUntil(() => File.Exists(path));
        UserStrategyMetadataRepository.Instance.SaveSignalBundles(ticker, second);
        WaitUntil(() => File.Exists(path) && ReadText(path).Contains("Second", StringComparison.Ordinal));

        Assert.Contains("Second", ReadText(path));
        Assert.DoesNotContain("First", ReadText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory, $"{ticker}*"));
    }

    [Fact]
    public void SaveSignalBundles_RapidSavesOfOneTicker_EndWithTheLastBundles_AndLeaveNoTemporaryFile()
    {
        string ticker = $"SIGNAL_DIR_{Guid.NewGuid():N}";
        string path = Path.Combine(_directory, $"{ticker}.signals.json");
        const int saves = 40;
        string lastName = $"Set{saves - 1}";

        for (int i = 0; i < saves; i++)
        {
            UserStrategyMetadataRepository.Instance.SaveSignalBundles(ticker,
                new List<BundledSignalCondition> { new($"Set{i}", SignalTargetType.Long, Array.Empty<ScreenerIndicatorEntry>()) });
        }

        WaitUntil(() => File.Exists(path) && ReadText(path).Contains($"\"{lastName}\"", StringComparison.Ordinal)
            && Directory.GetFiles(_directory, $"{ticker}*").Length == 1);

        Assert.Contains($"\"{lastName}\"", ReadText(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory, $"{ticker}*"));
    }

    private static void WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < PollAttempts && !condition(); i++) Thread.Sleep(PollIntervalMilliseconds);
    }

    private static string ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return string.Empty; } // the background save is replacing the file right now
    }

    [Fact]
    public void GetSignalBundles_ReadsFromTheConfiguredDirectory()
    {
        string ticker = $"SIGNAL_DIR_{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(_directory, $"{ticker}.signals.json"),
            "[{\"Name\":\"FromDisk\",\"TargetType\":0,\"Conditions\":[]}]");

        var loaded = UserStrategyMetadataRepository.Instance.GetSignalBundles(ticker);

        Assert.Single(loaded);
        Assert.Equal("FromDisk", loaded[0].Name);
    }
}
