using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Anchor resolution, limits L1-L3 and temporary-directory ownership of the external replay episode, on real local Parquet files.</summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlExternalEpisodeFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_external_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private string DataRoot => Path.Combine(_root, "data");
    private static readonly DateOnly First = RlSourceFixture.FirstDay;

    public RlExternalEpisodeFactoryTests()
    {
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, DataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _previousRoot);
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, _previousEnabled);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private RlReplayWorkspace Workspace() => RlReplayWorkspace.Create(DataRoot);
    private string ReplayFolder => Path.Combine(DataRoot, RlReplayWorkspace.FolderName);

    private static Task<RlAnchorRange> Range(string symbol, RlReplayWorkspace workspace)
        => RlExternalEpisodeFactory.ReadAnchorRangeAsync(symbol, RlSourceFixture.Limits, workspace, CancellationToken.None);

    private static Task<RlExternalEpisode> Episode(string symbol, DateOnly anchor, RlReplayWorkspace workspace, CancellationToken ct = default)
        => RlExternalEpisodeFactory.PrepareAsync(symbol, anchor, RlSourceFixture.Limits, workspace, ct);

    [Fact]
    public async Task TheAnchorRangeIsBarSixtyThreeToTheBarOneHundredTwentyEightBeforeTheLast()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 300);
        using var workspace = Workspace();
        var range = await Range("ABC", workspace);
        Assert.Equal(new RlAnchorRange(First.AddDays(RlContract.WindowLength - 1), First.AddDays(299 - RlContract.EpisodeHorizon)), range);
    }

    [Fact]
    public async Task ExactlyOneEpisodeOfBarsGivesOneAnchorAndFewerBarsAreRejected()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "EXACT", First, RlContract.SupportLength);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "SHORT", First, RlContract.SupportLength - 1);
        using var workspace = Workspace();
        var single = await Range("EXACT", workspace);
        Assert.Equal(single.EarliestAnchor, single.LatestAnchor);
        Assert.Equal(First.AddDays(RlContract.WindowLength - 1), single.EarliestAnchor);
        var error = await Assert.ThrowsAsync<RlExternalReplayException>(() => Range("SHORT", workspace));
        Assert.Equal(RlExternalReplayRejection.NotEnoughHistory, error.Reason);
        error = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("SHORT", First.AddDays(100), workspace));
        Assert.Equal(RlExternalReplayRejection.NotEnoughHistory, error.Reason);
    }

    [Fact]
    public async Task OneHundredNinetyOneBarsGiveNoAnchorOneHundredNinetyTwoGiveOneAndOneHundredNinetyThreeGiveTwo()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "N191", First, RlContract.SupportLength - 1);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "N192", First, RlContract.SupportLength);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "N193", First, RlContract.SupportLength + 1);
        using var workspace = Workspace();
        DateOnly earliest = First.AddDays(RlContract.WindowLength - 1);

        Assert.Equal(RlExternalReplayRejection.NotEnoughHistory, (await Assert.ThrowsAsync<RlExternalReplayException>(() => Range("N191", workspace))).Reason);
        Assert.Equal(RlExternalReplayRejection.NotEnoughHistory, (await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("N191", earliest, workspace))).Reason);

        Assert.Equal(new RlAnchorRange(earliest, earliest), await Range("N192", workspace));
        Assert.Equal(RlContract.SupportLength, (await Episode("N192", earliest, workspace)).Bars.Length);
        var tooLate = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("N192", earliest.AddDays(1), workspace));
        Assert.Equal((RlExternalReplayRejection.NotEnoughBarsAfter, earliest), (tooLate.Reason, tooLate.LatestAnchor));

        Assert.Equal(new RlAnchorRange(earliest, earliest.AddDays(1)), await Range("N193", workspace));
        foreach (var anchor in new[] { earliest, earliest.AddDays(1) })
        {
            var episode = await Episode("N193", anchor, workspace);
            Assert.Equal((RlContract.SupportLength, anchor), (episode.Bars.Length, episode.ResolvedAnchorDate));
        }
        tooLate = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("N193", earliest.AddDays(2), workspace));
        Assert.Equal((RlExternalReplayRejection.NotEnoughBarsAfter, earliest.AddDays(1)), (tooLate.Reason, tooLate.LatestAnchor));
    }

    [Fact]
    public async Task AnEpisodeHasExactlyOneHundredNinetyTwoBarsAndTheAnchorIsTheSixtyFourth()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using var workspace = Workspace();
        DateOnly anchor = First.AddDays(150);
        var episode = await Episode("ABC", anchor, workspace);
        Assert.Equal(RlContract.SupportLength, episode.Bars.Length);
        Assert.Equal(anchor.AddDays(-(RlContract.WindowLength - 1)), episode.FirstBar);
        Assert.Equal(anchor.AddDays(RlContract.EpisodeHorizon), episode.LastBar);
        Assert.Equal(anchor, episode.ResolvedAnchorDate);
        Assert.Equal(anchor, DateOnly.FromDateTime(episode.Bars[RlContract.WindowLength - 1].Timestamp));
        Assert.Equal("ABC", episode.Manifest.Symbol);
        Assert.Equal(anchor.AddDays(-(RlContract.WindowLength - 1)), episode.Manifest.PeriodStartInclusive);
        Assert.Equal(episode.LastBar.AddDays(1), episode.Manifest.PeriodEndExclusive);
        var input = episode.CreateInput();
        Assert.Equal(RlContract.SupportLength, input.Bars.Length);
    }

    [Fact]
    public async Task TheEarliestAnchorIsBarSixtyThreeAndTheBarBeforeItIsRejectedWithTheEarliestDate()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using var workspace = Workspace();
        var earliest = First.AddDays(RlContract.WindowLength - 1);
        Assert.Equal(earliest, (await Episode("ABC", earliest, workspace)).ResolvedAnchorDate);
        var error = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("ABC", earliest.AddDays(-1), workspace));
        Assert.Equal(RlExternalReplayRejection.NotEnoughBarsBefore, error.Reason);
        Assert.Equal(earliest, error.EarliestAnchor);
        Assert.Null(error.LatestAnchor);
    }

    [Fact]
    public async Task TheLatestAnchorIsOneHundredTwentyEightBarsBeforeTheLastAndTheNextBarIsRejectedWithTheLatestDate()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using var workspace = Workspace();
        var latest = First.AddDays(399 - RlContract.EpisodeHorizon);
        Assert.Equal(latest, (await Episode("ABC", latest, workspace)).ResolvedAnchorDate);
        var error = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("ABC", latest.AddDays(1), workspace));
        Assert.Equal(RlExternalReplayRejection.NotEnoughBarsAfter, error.Reason);
        Assert.Equal(latest, error.LatestAnchor);
        Assert.Null(error.EarliestAnchor);
    }

    [Fact]
    public async Task ADateBeforeTheFirstBarResolvesToTheFirstBarAndADateAfterTheLastResolvesToTheLastBar()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using var workspace = Workspace();
        var before = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("ABC", First.AddDays(-30), workspace));
        Assert.Equal(RlExternalReplayRejection.NotEnoughBarsBefore, before.Reason);
        var after = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("ABC", First.AddDays(399 + 30), workspace));
        Assert.Equal(RlExternalReplayRejection.NotEnoughBarsAfter, after.Reason);
    }

    [Fact]
    public async Task TheCalendarAndTheEpisodeComeFromOneCaptureSoARewriteOfTheStoredFileAfterItIsNotSeen()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using var workspace = Workspace();
        string captured = "";
        int copies = 0;
        var episode = await RlExternalEpisodeFactory.PrepareAsync("ABC", First.AddDays(150), RlSourceFixture.Limits, workspace, CancellationToken.None, directory =>
        {
            captured = directory;
            copies = Directory.GetFiles(workspace.Directory, RlFiles.RawParquet, SearchOption.AllDirectories).Length;
            // Same rows and dates, different closes: the file a second capture would have read.
            RlSourceFixture.CreateParquet(Path.Combine(DataRoot, "Daily", "ABC.parquet"),
                $"SELECT DATE '{First:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,200::FLOAT AS open,210::FLOAT AS high,190::FLOAT AS low,200::FLOAT AS close,1000::BIGINT AS volume FROM range(400)");
        });
        Assert.True(File.Exists(Path.Combine(captured, RlFiles.RawParquet)));
        Assert.Equal(1, copies);
        Assert.Equal(1, Directory.GetFiles(workspace.Directory, RlFiles.RawParquet, SearchOption.AllDirectories).Length);
        Assert.Equal(RlContract.SupportLength, episode.Bars.Length);
        Assert.All(episode.Bars, bar => Assert.Equal(100m, bar.Close));
        Assert.Equal(First.AddDays(150), episode.ResolvedAnchorDate);

        // The rewritten file is what the NEXT call captures.
        var next = await Episode("ABC", First.AddDays(150), workspace);
        Assert.All(next.Bars, bar => Assert.Equal(200m, bar.Close));
        Assert.NotEqual(episode.SourceRevision, next.SourceRevision);
    }

    [Fact]
    public async Task TheTimingsReceiveTheCapturedSizeAndEveryPhaseOfThePreparation()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using var workspace = Workspace();
        var timings = new RlReplayTimings();
        _ = await RlExternalEpisodeFactory.PrepareAsync("ABC", First.AddDays(150), RlSourceFixture.Limits, workspace, CancellationToken.None, timings: timings);
        Assert.Equal(400, timings.Rows);
        Assert.Equal(new FileInfo(Path.Combine(DataRoot, "Daily", "ABC.parquet")).Length, timings.RawBytes);
        Assert.All(new[] { timings.CaptureMs, timings.CalendarMs, timings.EpisodeMs }, ms => Assert.True(ms > 0d));
    }

    [Fact]
    public async Task ADayWithoutABarResolvesToTheEarlierTradingDayAndTheResolvedDateIsReported()
    {
        var days = RlSourceFixture.WriteWeekdaySymbol(DataRoot, "WK", new DateOnly(2000, 1, 3), 600);
        using var workspace = Workspace();
        var friday = days.First(d => d.DayOfWeek == DayOfWeek.Friday && days.IndexOf(d) > 100);
        foreach (var weekend in new[] { friday.AddDays(1), friday.AddDays(2) })
        {
            var episode = await Episode("WK", weekend, workspace);
            Assert.Equal(friday, episode.ResolvedAnchorDate);
            Assert.Equal(days[days.IndexOf(friday) - (RlContract.WindowLength - 1)], episode.FirstBar);
            Assert.Equal(days[days.IndexOf(friday) + RlContract.EpisodeHorizon], episode.LastBar);
        }
        var monday = days[days.IndexOf(friday) + 1];
        Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
        Assert.Equal(monday, (await Episode("WK", monday, workspace)).ResolvedAnchorDate);
    }

    [Fact]
    public async Task TheBarOfTheCurrentExchangeDateIsNeverUsed()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        int count = today.DayNumber - First.DayNumber + 1; // includes today's bar
        RlSourceFixture.WriteLocalSymbol(DataRoot, "LIVE", First, count);
        using var workspace = Workspace();
        var range = await Range("LIVE", workspace);
        // The last completed bar is yesterday: the latest anchor is yesterday minus the horizon.
        Assert.Equal(today.AddDays(-1 - RlContract.EpisodeHorizon), range.LatestAnchor);
        var episode = await Episode("LIVE", range.LatestAnchor, workspace);
        Assert.Equal(today.AddDays(-1), episode.LastBar);
    }

    [Fact]
    public async Task AHistoryLongerThanTheRowCeilingStillGivesARangeAndAnEpisode()
    {
        int count = RlSourceFixture.Limits.MaxSamples + 500;
        DateOnly first = new(1990, 1, 1); // ends in 2018: every bar is completed
        RlSourceFixture.WriteLocalSymbol(DataRoot, "LONG", first, count);
        using var workspace = Workspace();
        var range = await Range("LONG", workspace);
        Assert.Equal(first.AddDays(count - 1 - RlContract.EpisodeHorizon), range.LatestAnchor);
        var episode = await Episode("LONG", range.LatestAnchor, workspace);
        Assert.Equal(RlContract.SupportLength, episode.Bars.Length);
    }

    [Fact]
    public async Task MissingOrUnusableLocalDataIsReportedAsARejection()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "NOMETA", First, 400, metadata: false);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ETF1", First, 400, quoteType: "ETF");
        using var workspace = Workspace();
        foreach (string symbol in new[] { "NONE", "NOMETA", "", "  ", "../ABC", "A/B" })
        {
            var missing = await Assert.ThrowsAsync<RlExternalReplayException>(() => Range(symbol, workspace));
            Assert.Equal(RlExternalReplayRejection.NoLocalData, missing.Reason);
        }
        var etf = await Assert.ThrowsAsync<RlExternalReplayException>(() => Range("ETF1", workspace));
        Assert.Equal(RlExternalReplayRejection.SourceUnavailable, etf.Reason);
        etf = await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("ETF1", First.AddDays(150), workspace));
        Assert.Equal(RlExternalReplayRejection.SourceUnavailable, etf.Reason);
    }

    [Fact]
    public async Task ACancelledTokenStopsPreparationAndTheWorkspaceStillRemovesItsDirectory()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        string directory;
        using (var workspace = Workspace())
        {
            directory = workspace.Directory;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Episode("ABC", First.AddDays(150), workspace, cancelled.Token));
            Assert.True(Directory.Exists(directory));
        }
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task EveryWorkspaceIsDeletedAfterSuccessAndAfterARejection()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", First, 400);
        using (var workspace = Workspace())
        {
            _ = await Episode("ABC", First.AddDays(150), workspace);
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(workspace.Directory));
        }
        using (var workspace = Workspace())
            await Assert.ThrowsAsync<RlExternalReplayException>(() => Episode("ABC", First.AddDays(-5), workspace));
        Assert.Empty(Directory.EnumerateDirectories(ReplayFolder));
    }

    [Fact]
    public void AWorkspaceHoldsItsLockAndSweepingSkipsItButRemovesStaleDirectories()
    {
        Directory.CreateDirectory(ReplayFolder);
        string stale = Path.Combine(ReplayFolder, "stale");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "payload.bin"), "x");
        File.WriteAllText(Path.Combine(stale, RlReplayWorkspace.LockFileName), "");
        string unlocked = Path.Combine(ReplayFolder, "nolockfile");
        Directory.CreateDirectory(unlocked);

        using var live = RlReplayWorkspace.Create(DataRoot); // first use of this root: sweeps
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(unlocked));
        Assert.True(Directory.Exists(live.Directory));

        RlReplayWorkspace.SweepStale(ReplayFolder);
        Assert.True(Directory.Exists(live.Directory));
        using (var other = RlReplayWorkspace.Create(DataRoot))
        {
            RlReplayWorkspace.SweepStale(ReplayFolder);
            Assert.True(Directory.Exists(other.Directory));
            Assert.True(Directory.Exists(live.Directory));
        }
    }

    [Fact]
    public void ADirectoryWithoutALockFileIsSweptOnlyWhenOlderThanTheSafetyBound()
    {
        Directory.CreateDirectory(ReplayFolder);
        var bound = RlReplayWorkspaceLimits.LocklessFolderMinimumAge;
        string Folder(string name, TimeSpan age, bool withLock = false)
        {
            string path = Path.Combine(ReplayFolder, name);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "payload.bin"), "x");
            if (withLock) File.WriteAllText(Path.Combine(path, RlReplayWorkspace.LockFileName), "");
            Directory.SetCreationTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }
        string old = Folder("old", bound + TimeSpan.FromHours(1));
        string justUnder = Folder("just_under", bound - TimeSpan.FromHours(1));
        string fresh = Folder("fresh", TimeSpan.Zero);
        string oldWithLock = Folder("old_with_lock", bound + TimeSpan.FromDays(30), withLock: true);   // stale by its lock, not by its age

        RlReplayWorkspace.SweepStale(ReplayFolder);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(justUnder));
        Assert.True(Directory.Exists(fresh));
        Assert.False(Directory.Exists(oldWithLock));
    }

    [Fact]
    public void AnOldFolderWhoseLockIsStillHeldIsNeverSwept()
    {
        using var live = RlReplayWorkspace.Create(DataRoot);
        Directory.SetCreationTimeUtc(live.Directory, DateTime.UtcNow - RlReplayWorkspaceLimits.LocklessFolderMinimumAge - TimeSpan.FromDays(3));
        RlReplayWorkspace.SweepStale(ReplayFolder);
        Assert.True(Directory.Exists(live.Directory));
    }

    [Fact]
    public void AWorkspaceThatIsStillOpenIsNeverSweptAndItsDirectoryGoesOnDispose()
    {
        var workspace = RlReplayWorkspace.Create(DataRoot);
        string directory = workspace.Directory;
        RlReplayWorkspace.SweepStale(ReplayFolder);
        Assert.True(Directory.Exists(directory));
        Assert.Throws<IOException>(() => new FileStream(Path.Combine(directory, RlReplayWorkspace.LockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        workspace.Dispose();
        Assert.False(Directory.Exists(directory));
    }
}
