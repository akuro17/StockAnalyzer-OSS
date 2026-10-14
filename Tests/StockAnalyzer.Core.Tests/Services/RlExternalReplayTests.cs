using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// External replay (any ticker, any date) on the numerical ONNX fixture generation. Like the rest of this class the numbers prove
/// integrity and equivalence only; they are never trained-market evidence.
/// </summary>
public sealed partial class RlGenerationAndQValueTests
{
    private string ExternalDataRoot => Path.Combine(_root, "external_data");
    private string ExternalReplayFolder => Path.Combine(ExternalDataRoot, RlReplayWorkspace.FolderName);
    private static readonly DateOnly ExternalFirst = RlSourceFixture.FirstDay;

    /// <summary>Redirects the Daily/Metadata folders to <see cref="ExternalDataRoot"/> for the duration of a test.</summary>
    private sealed class ExternalDataScope : IDisposable
    {
        private readonly string? _enabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
        private readonly string? _previous = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);

        public ExternalDataScope(string root)
        {
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _previous);
            Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, _enabled);
        }
    }

    private static string Serialize(RlSessionSnapshot account) => JsonSerializer.Serialize(account, RlFiles.Json);

    [Fact]
    public async Task ExternalReplay_OnTheGenerationsOwnTickerAndWindowEqualsTheSegmentReplayForEveryPolicy()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, Symbol, ExternalFirst, 5_200);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        var block = candidate.Source.Catalog.Validation[0];
        var anchor = ExternalFirst.AddDays(block.StartIndex + RlContract.WindowLength - 1);

        foreach (string policy in RlReplayPolicies.For(generation))
        foreach (int decisions in new[] { 0, 3, RlContract.EpisodeHorizon - 1 })
        {
            var segment = await service.ReplayAsync(new RlReplayRequest(generation.ModelId, candidate.Directory, block.Id, decisions, policy));
            var external = await service.ReplayExternalAsync(new RlExternalReplayRequest(generation.ModelId, Symbol, anchor, decisions, policy));
            string label = $"{policy} @ {decisions}";
            Assert.True(segment.QValues.SequenceEqual(external.Snapshot.QValues), label);
            Assert.Equal(segment.SelectedAction, external.Snapshot.SelectedAction);
            Assert.Equal(segment.Anchor, external.Snapshot.Anchor);
            Assert.Equal(segment.PolicyId, external.Snapshot.PolicyId);
            Assert.Equal(segment.Symbol, external.Snapshot.Symbol);
            Assert.Equal(Serialize(segment.Account), Serialize(external.Snapshot.Account));
            Assert.Equal(anchor, external.RequestedAnchorDate);
            Assert.Equal(anchor, external.ResolvedAnchorDate);
            Assert.Equal(RlReplayScope.SameInstrumentOverlapsTraining, external.Snapshot.ReplayScope);
            Assert.Empty(external.Snapshot.ScalerFindings);
            Assert.StartsWith(RlFiles.Disclosure, external.Snapshot.Disclosure);
            Assert.EndsWith(QValueService.ExternalWindowDisclosure, external.Snapshot.Disclosure);
            // The segment replay keeps its original, unmarked result.
            Assert.Equal(RlReplayScope.TrainingSource, segment.ReplayScope);
            Assert.Empty(segment.ScalerFindings);
            Assert.DoesNotContain("ExternalWindow", segment.Disclosure);
        }
        Assert.Empty(Directory.EnumerateDirectories(ExternalReplayFolder));
    }

    [Fact]
    public async Task ExternalReplay_ReportsASplitJumpAndAGapOnlyOnceTheShownBarsReachThemAndNeverForNormalData()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        const int anchorIndex = 150, firstBar = anchorIndex - (RlContract.WindowLength - 1);
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "NORMAL", ExternalFirst, 400);
        // 2:1 split: every price halves from episode bar 100 on (absolute bar firstBar + 100).
        int splitBar = firstBar + 100;
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "SPLIT", ExternalFirst, 1);
        RlSourceFixture.CreateParquet(Path.Combine(ExternalDataRoot, "Daily", "SPLIT.parquet"),
            "SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date," +
            $"(CASE WHEN range >= {splitBar} THEN 50 ELSE 100 END)::FLOAT AS open,(CASE WHEN range >= {splitBar} THEN 55 ELSE 110 END)::FLOAT AS high," +
            $"(CASE WHEN range >= {splitBar} THEN 45 ELSE 90 END)::FLOAT AS low,(CASE WHEN range >= {splitBar} THEN 50 ELSE 100 END)::FLOAT AS close,1000::BIGINT AS volume FROM range(400)");
        // 20-day gap: 19 calendar days are missing between absolute bars 119 and 120 (episode bar 33).
        const int gapBar = 120, missingDays = 19;
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "GAPPY", ExternalFirst, 1);
        RlSourceFixture.CreateParquet(Path.Combine(ExternalDataRoot, "Daily", "GAPPY.parquet"),
            $"SELECT DATE '2000-01-01'+CAST(range + CASE WHEN range >= {gapBar} THEN {missingDays} ELSE 0 END AS INTEGER) AS date," +
            "100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range(400)");
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);

        async Task<RlQSnapshot> Replay(string symbol, int decisions, int shift = 0)
            => (await service.ReplayExternalAsync(new(generation.ModelId, symbol, ExternalFirst.AddDays(anchorIndex + shift), decisions, RlContract.HoldBaselineId))).Snapshot;

        Assert.Empty((await Replay("NORMAL", 0)).ContinuityFindings);
        Assert.Empty((await Replay("NORMAL", RlContract.EpisodeHorizon - 1)).ContinuityFindings);

        // The split is episode bar 100: shown once the account has seen bars [0, k + 63], i.e. from k = 37 on, never before.
        Assert.Empty((await Replay("SPLIT", 0)).ContinuityFindings);
        Assert.Empty((await Replay("SPLIT", 36)).ContinuityFindings);
        var split = Assert.Single((await Replay("SPLIT", 37)).ContinuityFindings);
        Assert.Equal((RlContinuityKind.CloseChange, 0.5m, 0m, ExternalFirst.AddDays(splitBar)), (split.Kind, split.Observed, split.TrainedMaximum, split.Date));

        // The gap is at episode bar 33 (absolute bar 120), already inside the first window.
        var gap = Assert.Single((await Replay("GAPPY", 0, missingDays)).ContinuityFindings);
        Assert.Equal((RlContinuityKind.CalendarGap, 20m, 1m, ExternalFirst.AddDays(gapBar + missingDays)), (gap.Kind, gap.Observed, gap.TrainedMaximum, gap.Date));
    }

    [Fact]
    public async Task ExternalReplay_NamesThePartsOfTheAgentsSourcePeriodTheWindowOverlaps()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, Symbol, ExternalFirst, 5_200);
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "OTHER", ExternalFirst, 400);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        var catalog = candidate.Source.Catalog;
        Assert.Equal(RlPartitionCuts.Ratio(5_200), (catalog.TrainCut, catalog.ValidationCut)); // one owner of the cut formula

        async Task<RlExternalReplayResult> At(int firstBarIndex, string symbol = Symbol)
            => await service.ReplayExternalAsync(new(generation.ModelId, symbol, ExternalFirst.AddDays(firstBarIndex + RlContract.WindowLength - 1), 0, RlContract.HoldBaselineId));

        // Whole episodes inside one part: the first block of each partition.
        var train = await At(catalog.Train[0].StartIndex);
        Assert.Equal((RlReplayScope.SameInstrumentOverlapsTraining, RlSourceParts.Train), (train.Snapshot.ReplayScope, train.Snapshot.OverlappingParts));
        Assert.Equal(RlSourceParts.Validation, (await At(catalog.Validation[0].StartIndex)).Snapshot.OverlappingParts);
        Assert.Equal(RlSourceParts.FinalEvaluation, (await At(catalog.Oos[0].StartIndex)).Snapshot.OverlappingParts);
        // An episode that starts before a cut and ends after it names both sides; the last episode ending on the last bar of a part names only that part.
        Assert.Equal(RlSourceParts.Train | RlSourceParts.Validation, (await At(catalog.TrainCut - RlContract.WindowLength)).Snapshot.OverlappingParts);
        Assert.Equal(RlSourceParts.Train, (await At(catalog.TrainCut - RlContract.SupportLength)).Snapshot.OverlappingParts);
        Assert.Equal(RlSourceParts.Validation | RlSourceParts.FinalEvaluation, (await At(catalog.ValidationCut - RlContract.WindowLength)).Snapshot.OverlappingParts);
        // Another ticker names no part.
        var other = await At(100, "OTHER");
        Assert.Equal((RlReplayScope.OtherInstrument, RlSourceParts.None), (other.Snapshot.ReplayScope, other.Snapshot.OverlappingParts));
    }

    [Fact]
    public async Task ExternalReplay_AnotherTickerIsLabelledAndAPriceLevelOutsideTheTrainedRangeIsReportedPerChannel()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "OTHER", ExternalFirst, 400);
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "BIG", ExternalFirst, 400);
        RlSourceFixture.CreateParquet(Path.Combine(ExternalDataRoot, "Daily", "BIG.parquet"),
            "SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date,2000::FLOAT AS open,2010::FLOAT AS high,1990::FLOAT AS low,2000::FLOAT AS close,1000::BIGINT AS volume FROM range(400)");
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        var anchor = ExternalFirst.AddDays(150);

        var other = await service.ReplayExternalAsync(new(generation.ModelId, "OTHER", anchor, 2, RlContract.HoldBaselineId));
        Assert.Equal(RlReplayScope.OtherInstrument, other.Snapshot.ReplayScope);
        Assert.Equal("OTHER", other.Snapshot.Symbol);
        Assert.Empty(other.Snapshot.ScalerFindings);

        var big = await service.ReplayExternalAsync(new(generation.ModelId, " big ", anchor, 0, generation.Target));
        Assert.Equal(RlReplayScope.OtherInstrument, big.Snapshot.ReplayScope);
        // Trained range of every channel is [0, 1000]: open, high, low and close leave it, volume (1000) does not.
        Assert.Equal(candidate.Scaler.ChannelIdentities.Take(4), big.Snapshot.ScalerFindings.Select(f => f.Channel));
        var open = big.Snapshot.ScalerFindings[0];
        Assert.Equal((0d, 1_000d, 2_000d, 2_000d), (open.TrainedMin, open.TrainedMax, open.WindowMin, open.WindowMax));
        Assert.Equal(1_990d, big.Snapshot.ScalerFindings[2].WindowMin);
        Assert.Equal(2_010d, big.Snapshot.ScalerFindings[1].WindowMax);
    }

    [Fact]
    public async Task ExternalReplay_TheAgentsRangeCheckCoversEveryBarItObservedAndABaselineChecksOnlyTheShownWindow()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        var anchor = ExternalFirst.AddDays(150);
        int spike = 150 - (RlContract.WindowLength - 1); // episode bar 0: the only bar outside the trained range [0, 1000]
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "SPIKE", ExternalFirst, 400);
        RlSourceFixture.CreateParquet(Path.Combine(ExternalDataRoot, "Daily", "SPIKE.parquet"),
            "SELECT DATE '2000-01-01'+CAST(range AS INTEGER) AS date," +
            $"(CASE WHEN range = {spike} THEN 2000 ELSE 100 END)::FLOAT AS open,(CASE WHEN range = {spike} THEN 2010 ELSE 110 END)::FLOAT AS high," +
            $"(CASE WHEN range = {spike} THEN 1990 ELSE 90 END)::FLOAT AS low,(CASE WHEN range = {spike} THEN 2000 ELSE 100 END)::FLOAT AS close," +
            "1000::BIGINT AS volume FROM range(400)");
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);

        async Task<RlExternalReplayResult> Replay(int decisions, string policy)
            => await service.ReplayExternalAsync(new(generation.ModelId, "SPIKE", anchor, decisions, policy));

        // Decision 0 shows bars 0..63: the spike is inside the shown window for every policy.
        Assert.Equal(4, (await Replay(0, generation.Target)).Snapshot.ScalerFindings.Length);
        Assert.Equal(4, (await Replay(0, RlContract.HoldBaselineId)).Snapshot.ScalerFindings.Length);
        // Decision 1 shows bars 1..64: the spike left the window, but the agent observed it at decision 0.
        var agent = await Replay(1, generation.Target);
        Assert.Equal(candidate.Scaler.ChannelIdentities.Take(4), agent.Snapshot.ScalerFindings.Select(f => f.Channel));
        Assert.All(agent.Snapshot.ScalerFindings.Where(f => f.Channel.EndsWith("open")), f => Assert.Equal(2_000d, f.WindowMax));
        foreach (string baseline in new[] { RlContract.HoldBaselineId, RlContract.BuyHoldBaselineId, RlContract.SmaBaselineId })
            Assert.Empty((await Replay(1, baseline)).Snapshot.ScalerFindings);
    }

    [Fact]
    public async Task ExternalReplay_NoBarAfterTheShownDecisionCanChangeAnythingTheReplayShows()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        const int count = 400, anchorIndex = 150;
        var anchor = ExternalFirst.AddDays(anchorIndex);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        string[] policies = { generation.Target, RlContract.SmaBaselineId, RlContract.BuyHoldBaselineId };
        int[] decisions = { 0, 5, RlContract.WindowLength - 1, RlContract.EpisodeHorizon - 2 };

        async Task<RlQSnapshot> Replay(int k, string policy)
            => (await service.ReplayExternalAsync(new(generation.ModelId, "VARY", anchor, k, policy))).Snapshot;

        RlSourceFixture.WriteVaryingSymbol(ExternalDataRoot, "VARY", ExternalFirst, count);
        var original = new Dictionary<(string Policy, int K), RlQSnapshot>();
        foreach (string policy in policies)
        foreach (int k in decisions)
            original[(policy, k)] = await Replay(k, policy);

        // The shown state after k decisions has seen the bars up to index anchorIndex + k; everything later (even inside the 192-bar episode) is rewritten.
        foreach (int k in decisions)
        {
            RlSourceFixture.WriteVaryingSymbol(ExternalDataRoot, "VARY", ExternalFirst, count, anchorIndex + k + 1, count - 1);
            foreach (string policy in policies)
            {
                var rewritten = await Replay(k, policy);
                var expected = original[(policy, k)];
                string label = $"{policy} @ {k}";
                Assert.True(expected.QValues.SequenceEqual(rewritten.QValues), label);
                Assert.Equal(expected.SelectedAction, rewritten.SelectedAction);
                Assert.Equal(expected.Anchor, rewritten.Anchor);
                Assert.Equal(Serialize(expected.Account), Serialize(rewritten.Account));
            }
        }

        // Control: a bar INSIDE the window of decision 5 (index anchorIndex + 5 - 10) does change the shown account (it carries the market window), so the
        // comparison above can fail. The Q values of this numerical fixture do not react to the observation at all, which is why the account is the control;
        // the observation vector itself is guarded in RlExternalWindowEquivalenceTests.
        int inside = anchorIndex + 5 - 10;
        RlSourceFixture.WriteVaryingSymbol(ExternalDataRoot, "VARY", ExternalFirst, count, inside, inside);
        var changed = await Replay(5, generation.Target);
        Assert.NotEqual(Serialize(original[(generation.Target, 5)].Account), Serialize(changed.Account));
    }

    [Fact]
    public async Task ExternalReplay_LogsItsPhaseTimingsOnceAtDebugAndALoggerChangesNothing()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "ABC", ExternalFirst, 400);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var request = new RlExternalReplayRequest(generation.ModelId, "ABC", ExternalFirst.AddDays(150), 3, generation.Target);

        var recording = new CapturingLogger<QValueService>();
        var logged = await new QValueService(registry, recording, ExternalDataRoot).ReplayExternalAsync(request);
        var timings = Assert.Single(recording.Entries, entry => entry.Message.StartsWith("RL external replay timings:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, timings.Level);
        foreach (string phase in new[] { "rows=400", "rawBytes=", "captureMs=", "calendarMs=", "episodeMs=", "modelLoadMs=", "steppingMs=", "steps=3",
                     "inferenceMs=", "totalMs=", "scope=OtherInstrument", "findings=0" })
            Assert.Contains(phase, timings.Message);
        Assert.DoesNotContain("ABC", timings.Message);

        var quiet = await new QValueService(registry, null, ExternalDataRoot).ReplayExternalAsync(request);
        var throwing = await new QValueService(registry, new ThrowingLogger<QValueService>(), ExternalDataRoot).ReplayExternalAsync(request);
        foreach (var other in new[] { quiet, throwing })
        {
            Assert.True(logged.Snapshot.QValues.SequenceEqual(other.Snapshot.QValues));
            Assert.Equal(logged.Snapshot.SelectedAction, other.Snapshot.SelectedAction);
            Assert.Equal(Serialize(logged.Snapshot.Account), Serialize(other.Snapshot.Account));
            Assert.Equal(logged.ResolvedAnchorDate, other.ResolvedAnchorDate);
        }
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("the logger is broken");
    }

    [Fact]
    public async Task ExternalReplay_AGenerationOfAnotherContractIsRejectedBeforeAnyFileIsRead()
    {
        string modelPath = Path.Combine(_root, "foreign_contract_probe", RlFiles.Model);
        var registry = new ContinuousProbeRegistry(modelPath, new string('0', 64), RlFiles.Target);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReplayExternalAsync(
            new(ContinuousProbeRegistry.ModelId, "ABC", ExternalFirst.AddDays(150), 0, RlFiles.Target)));
        Assert.Equal(1, registry.Acquired);
        Assert.Equal(1, registry.Released);
        Assert.False(Directory.Exists(ExternalReplayFolder));
    }

    [Fact]
    public async Task ExternalReplay_ADayWithoutABarUsesTheEarlierTradingDayAndSaysSo()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        var days = RlSourceFixture.WriteWeekdaySymbol(ExternalDataRoot, "WK", new DateOnly(2000, 1, 3), 600);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        var friday = days.First(d => d.DayOfWeek == DayOfWeek.Friday && days.IndexOf(d) > 100);

        var weekend = await service.ReplayExternalAsync(new(generation.ModelId, "WK", friday.AddDays(1), 1, RlContract.HoldBaselineId));
        Assert.Equal(friday.AddDays(1), weekend.RequestedAnchorDate);
        Assert.Equal(friday, weekend.ResolvedAnchorDate);
        var exact = await service.ReplayExternalAsync(new(generation.ModelId, "WK", friday, 1, RlContract.HoldBaselineId));
        Assert.Equal(friday, exact.ResolvedAnchorDate);
        Assert.Equal(Serialize(exact.Snapshot.Account), Serialize(weekend.Snapshot.Account));
    }

    [Fact]
    public async Task ExternalReplay_RejectionsCarryTheirReasonAndLeaveNoTemporaryDirectory()
    {
        using var data = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "ABC", ExternalFirst, 400);
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "SHORT", ExternalFirst, 100);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);

        async Task<RlExternalReplayException> Rejected(string symbol, DateOnly anchor, string? policy = null)
            => await Assert.ThrowsAsync<RlExternalReplayException>(() =>
                service.ReplayExternalAsync(new(generation.ModelId, symbol, anchor, 0, policy ?? RlContract.HoldBaselineId)));

        var before = await Rejected("ABC", ExternalFirst.AddDays(10));
        Assert.Equal((RlExternalReplayRejection.NotEnoughBarsBefore, ExternalFirst.AddDays(RlContract.WindowLength - 1)), (before.Reason, before.EarliestAnchor));
        var after = await Rejected("ABC", ExternalFirst.AddDays(399));
        Assert.Equal((RlExternalReplayRejection.NotEnoughBarsAfter, ExternalFirst.AddDays(399 - RlContract.EpisodeHorizon)), (after.Reason, after.LatestAnchor));
        Assert.Equal(RlExternalReplayRejection.NotEnoughHistory, (await Rejected("SHORT", ExternalFirst.AddDays(50))).Reason);
        Assert.Equal(RlExternalReplayRejection.NoLocalData, (await Rejected("NONE", ExternalFirst.AddDays(150))).Reason);
        Assert.Equal(RlExternalReplayRejection.PolicyNotAvailable, (await Rejected("ABC", ExternalFirst.AddDays(150), "no-such-policy")).Reason);
        Assert.Equal(RlExternalReplayRejection.PolicyNotAvailable, (await Rejected("ABC", ExternalFirst.AddDays(150), RlContinuousContract.PairedBaselineId)).Reason);

        foreach (int bad in new[] { -1, RlContract.EpisodeHorizon })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReplayExternalAsync(
                new(generation.ModelId, "ABC", ExternalFirst.AddDays(150), bad, RlContract.HoldBaselineId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReplayExternalAsync(
            new(Revision, "ABC", ExternalFirst.AddDays(150), 0, RlContract.HoldBaselineId)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplayExternalAsync(
            new(generation.ModelId, "ABC", ExternalFirst.AddDays(150), 0, RlContract.HoldBaselineId), cancelled.Token));

        Assert.Empty(Directory.Exists(ExternalReplayFolder) ? Directory.EnumerateDirectories(ExternalReplayFolder) : Array.Empty<string>());
    }

    [Fact]
    public async Task ExternalReplay_ContinuousGenerationsAreRejectedBeforeAnyFileIsRead()
    {
        string modelPath = Path.Combine(_root, "continuous_probe", RlFiles.Model);
        var registry = new ContinuousProbeRegistry(modelPath);
        var service = new QValueService(registry, dataRoot: ExternalDataRoot);
        var error = await Assert.ThrowsAsync<RlExternalReplayException>(() => service.ReplayExternalAsync(
            new(ContinuousProbeRegistry.ModelId, "ABC", ExternalFirst.AddDays(150), 0, RlContinuousContract.Target)));
        Assert.Equal(RlExternalReplayRejection.ContinuousNotSupported, error.Reason);
        Assert.Equal(1, registry.Acquired);
        Assert.Equal(1, registry.Released);
        Assert.False(Directory.Exists(ExternalReplayFolder));
    }

    [Fact]
    public async Task DialogData_ReadsTheSourceInfoTheAnchorRangeAndForwardsTheExternalReplay()
    {
        using var scope = new ExternalDataScope(ExternalDataRoot);
        var candidate = Candidate();
        RlSourceFixture.WriteLocalSymbol(ExternalDataRoot, "ABC", ExternalFirst, 400);
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var data = new RlDialogDataService(registry, new QValueService(registry, dataRoot: ExternalDataRoot), ExternalDataRoot);

        var info = await data.LoadSourceInfoAsync(generation.ModelId, CancellationToken.None);
        Assert.Equal(new RlSourceInfo(Symbol, new DateOnly(2000, 1, 1), new DateOnly(2026, 1, 1), ExternalFirst, ExternalFirst.AddDays(5_199)), info);

        var range = await data.GetAnchorRangeAsync(generation.ModelId, "ABC", CancellationToken.None);
        Assert.Equal(new RlAnchorRange(ExternalFirst.AddDays(RlContract.WindowLength - 1), ExternalFirst.AddDays(399 - RlContract.EpisodeHorizon)), range);

        var result = await data.ReplayExternalAsync(new(generation.ModelId, "ABC", range.LatestAnchor, 4, RlContract.BuyHoldBaselineId), CancellationToken.None);
        Assert.Equal(range.LatestAnchor, result.ResolvedAnchorDate);
        Assert.Equal(RlReplayScope.OtherInstrument, result.Snapshot.ReplayScope);

        await Assert.ThrowsAsync<RlExternalReplayException>(() => data.GetAnchorRangeAsync(generation.ModelId, "NONE", CancellationToken.None));
        Assert.Empty(Directory.EnumerateDirectories(ExternalReplayFolder));
    }

    [Fact]
    public async Task DialogData_MeasuredIsTheSavedFinalEvaluationsMeanReturnAndWorstDrawdown()
    {
        var candidate = Candidate();
        using var registry = new ModelGenerationRegistry(RegistryRoot);
        var generation = await registry.RegisterRlAsync(candidate.Directory);
        var data = new RlDialogDataService(registry, new QValueService(registry));

        var blocks = await data.LoadBlocksAsync(generation.ModelId, CancellationToken.None);

        using var lease = registry.AcquireRl(generation.ModelId)!;
        using var report = JsonDocument.Parse(File.ReadAllText(lease.ModelPath + RlFiles.EvaluationSuffix));
        var episodes = report.RootElement.GetProperty("oos_episodes").EnumerateArray().ToArray();
        Assert.NotEmpty(episodes);
        Assert.NotNull(blocks.Measured);
        Assert.Equal(RlReplayDefaults.OosPartition, blocks.Measured!.Partition);
        Assert.Equal(episodes.Length, blocks.Measured.Episodes);
        Assert.Equal(generation.OosReturn, blocks.Measured.MeanReturn);
        Assert.Equal(episodes.Max(e => decimal.Parse(e.GetProperty("maximum_drawdown").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
            blocks.Measured.WorstMaximumDrawdown);
        var returns = episodes.Select(e => decimal.Parse(e.GetProperty("terminal_return").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(returns.Min(), blocks.Measured.MinReturn);
        Assert.Equal(returns.Count(value => value > 0m), blocks.Measured.PositiveEpisodes);
    }

    [Fact]
    public void AnImplementationWithoutExternalReplayReportsItAsUnsupported()
    {
        IQValueService legacy = new ReplayOnlyProbe();
        Assert.Throws<NotSupportedException>(() => { _ = legacy.ReplayExternalAsync(new("m", "ABC", ExternalFirst, 0, RlContract.HoldBaselineId)); });
    }

    private sealed class ReplayOnlyProbe : IQValueService
    {
        public Task<RlQSnapshot> ReplayAsync(RlReplayRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>
    /// A registry that only knows one generation (continuous unless told otherwise, with the given contract hash); it proves a rejection happens
    /// before any file is touched.
    /// </summary>
    private sealed class ContinuousProbeRegistry(string modelPath, string? contractHash = null, string? target = null) : IRlModelGenerationRegistry
    {
        public const string ModelId = "continuous-probe";
        public int Acquired { get; private set; }
        public int Released { get; private set; }
        public string? RlActiveId => null;
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<RlGeneration> ListRl() => Array.Empty<RlGeneration>();
        public Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ModelGenerationLease? AcquireRl(string modelId)
        {
            Acquired++;
            return new ModelGenerationLease(modelId, modelPath, () => Released++);
        }
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string modelId) => new(modelId, "CONT", Revision2, contractHash ?? RlContinuousContract.ContractHash, Revision2, 0m, 0m,
            new Dictionary<string, string>(), DateTimeOffset.UnixEpoch) { Target = target ?? RlContinuousContract.Target, FrozenDirectionModelId = "direction" };
        private const string Revision2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    }
}
