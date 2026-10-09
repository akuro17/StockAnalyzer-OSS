#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Panel snapshots built from real local Daily/Metadata Parquet files in an isolated Data root and re-validated by the loader. The bar
/// values are bounded integrity fixtures, never market data or qualification evidence.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlPanelSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_panel_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private readonly RlPortfolioSettings _settings = new();
    private int _counter;
    private static readonly DateOnly First = RlSourceFixture.FirstDay;
    private string DataRoot => Path.Combine(_root, "data");

    public RlPanelSnapshotTests()
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

    private string NewDirectory() => Path.Combine(_root, "panel_" + _counter++);

    private void Symbols(int count, params string[] names)
    {
        foreach (string name in names) RlSourceFixture.WriteLocalSymbol(DataRoot, name, First, count);
    }

    private Task<RlPanelSnapshot> Prepare(string[] symbols, int days, string directory, bool holdout = false,
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory, CancellationToken ct = default, RlPortfolioSettings? settings = null)
        => RlPanelSnapshotFactory.PrepareAsync(symbols, First, First.AddDays(days), directory, RlSourceFixture.Limits, settings ?? _settings, holdout, mode, ct);

    private RlPanelSnapshot Load(string directory, RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory)
        => RlPanelSnapshot.Load(directory, RlSourceFixture.Limits, _settings, mode);

    private static void Mutate(string directory, Action<JsonNode> change) => RlSourceFixture.Mutate(directory, RlPortfolioContract.PanelManifest, change);

    [Fact]
    public async Task PreparesAnImmutableHashedPanelWithTheDocumentedIdentity()
    {
        Symbols(1_278, "DDD", "BBB", "AAA", "CCC");
        string directory = NewDirectory();
        var panel = await Prepare(new[] { "DDD", "BBB", "AAA", "CCC" }, 1_278, directory, holdout: true);

        Assert.Equal(new[] { "AAA", "BBB", "CCC", "DDD" }, panel.Symbols.ToArray());
        var manifest = panel.Manifest;
        Assert.Equal(RlPortfolioContract.SpecificationId, manifest.SpecificationId);
        Assert.Equal(1_278, manifest.CalendarLength);
        Assert.Equal(1_278, panel.Calendar.Length);
        Assert.Equal(First.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), panel.Calendar[0]);
        Assert.Equal(First.AddDays(1_277).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), panel.Calendar[^1]);
        // Independent recomputation of every identity value from the files on disk.
        var sourceHashes = panel.Symbols.Select(s => ContentHash.OfFile(Path.Combine(directory, "symbols", s, RlFiles.SourceManifest))).ToArray();
        Assert.Equal(sourceHashes, manifest.Symbols.Select(e => e.SourceManifestSha256).ToArray());
        string dates = string.Join("\n", Enumerable.Range(0, 1_278).Select(i => First.AddDays(i).ToString("yyyy-MM-dd")));
        Assert.Equal(ContentHash.OfText(dates), manifest.CalendarSha256);
        string membershipJson = "{\"schema\":2,\"symbols\":[\"AAA\",\"BBB\",\"CCC\",\"DDD\"],\"source_manifest_sha256\":[\""
            + string.Join("\",\"", sourceHashes) + "\"]}";
        Assert.Equal(ContentHash.OfText(membershipJson), manifest.MembershipSha256);
        // The split seed derives from the symbol list alone: no capture timestamp, period or file hash is an input.
        Assert.Equal(Convert.ToUInt32(ContentHash.OfText("{\"schema\":2,\"symbols\":[\"AAA\",\"BBB\",\"CCC\",\"DDD\"]}")[..8], 16), manifest.SplitSeed);
        Assert.True(manifest.HoldoutEnabled);
        Assert.Equal(25, manifest.HoldoutPercent);
        Assert.Equal(2, manifest.HoldoutSymbols.Length);
        string universeJson = "{\"schema\":2,\"membership_sha256\":\"" + manifest.MembershipSha256 + "\",\"split_seed\":" + manifest.SplitSeed
            + ",\"holdout_enabled\":true,\"holdout_percent\":25,\"holdout_symbols\":[\"" + string.Join("\",\"", manifest.HoldoutSymbols) + "\"]}";
        Assert.Equal(ContentHash.OfText(universeJson), manifest.UniverseSha256);
        Assert.Equal(manifest.HoldoutSymbols.ToArray(), panel.Symbols.Where((_, i) => panel.Catalog.IsHoldout[i]).ToArray());
        Assert.Equal(ContentHash.OfFile(Path.Combine(directory, RlPortfolioContract.PanelManifest)), panel.Revision);
        Assert.Equal("USD", manifest.Currency);
        Assert.Equal("UTC", manifest.ExchangeTimezone);
        Assert.All(manifest.Symbols, e => Assert.Equal((1_278, First, First.AddDays(1_277)), (e.BarCount, e.FirstDate, e.LastDate)));
        // Complete symbols: every calendar index has a bar for every symbol.
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(1_278, panel.PresentBars(i)));
        Assert.True(panel.TryGetBar(2, 5, out var bar));
        Assert.Equal(First.AddDays(5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), bar.Timestamp);
    }

    [Fact]
    public async Task ThePanelFeedsThePortfolioAccountantThroughTheBarsInterface()
    {
        Symbols(1_278, "AAA", "BBB", "CCC", "DDD");
        var panel = await Prepare(new[] { "AAA", "BBB", "CCC", "DDD" }, 1_278, NewDirectory(), holdout: true);
        IRlPanelBars bars = panel;
        Assert.Equal(4, bars.SymbolCount);
        var block = panel.Catalog.CellBlocks(RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Holdout).Single();
        var holdout = panel.Catalog.EligibleIn(block, RlPanelSymbolGroup.Holdout);
        var costs = RlContract.CreateConfiguration(new StockAnalyzer.Core.Models.Backtest.Engine.BacktestConfiguration
            { SizingModel = StockAnalyzer.Core.Models.Backtest.Engine.PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 100_000m });
        using var session = new RlPortfolioSession(bars, block, holdout, costs);
        session.Reset();
        // Constant fixture prices: holding half of the account in each holdout symbol keeps equity at the initial capital.
        var weights = Enumerable.Range(0, 4).Select(i => holdout.Contains(i) ? 0.5m : 0m).ToImmutableArray();
        RlPortfolioStepResult last = null!;
        for (int k = 0; k < RlContract.EpisodeHorizon; k++) last = session.Step(weights);
        Assert.True(last.Terminated);
        Assert.Equal(100_000m, last.Snapshot.Equity);
        Assert.Equal(block.EndExclusive - 1, last.Snapshot.AnchorIndex);
    }

    [Fact]
    public async Task ASymbolListedMidHistoryGetsAMaskAndTheShortSnapshotLoads()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "OLD", First, 1_500);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "NEW", First.AddDays(300), 1_200);
        string directory = NewDirectory();
        var panel = await Prepare(new[] { "NEW", "OLD" }, 1_500, directory);

        Assert.Equal(new[] { "NEW", "OLD" }, panel.Symbols.ToArray());
        Assert.Equal(1_500, panel.Calendar.Length);
        Assert.Equal(1_200, panel.PresentBars(0));
        Assert.Equal(1_500, panel.PresentBars(1));
        Assert.False(panel.IsPresent(0, 299));
        Assert.False(panel.TryGetBar(0, 0, out _));
        Assert.True(panel.TryGetBar(0, 300, out var bar));
        Assert.Equal(First.AddDays(300).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), bar.Timestamp);
        // Hand table (T = 1500): cut1 = 900, cut2 = 1200; the first two train blocks precede the listing at 300.
        Assert.Equal(new[] { "train:0:192:InsufficientAssets", "train:192:384:InsufficientAssets" }, panel.Catalog.Excluded.ToArray());
        Assert.Equal(new[] { 384, 576 }, panel.Catalog.Train.Select(b => b.StartIndex).ToArray());
        Assert.Equal(new[] { 964 }, panel.Catalog.Validation.Select(b => b.StartIndex).ToArray());
        Assert.Equal(new[] { 1264 }, panel.Catalog.Oos.Select(b => b.StartIndex).ToArray());
        Assert.Equal(1_200, panel.Manifest.Symbols[0].BarCount);
        Assert.Equal(First.AddDays(300), panel.Manifest.Symbols[0].FirstDate);
    }

    [Fact]
    public async Task LoadReproducesThePreparedPanelAndNeverWrites()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        var prepared = await Prepare(new[] { "AAA", "BBB" }, 1_278, directory);
        string before = RlSourceFixture.TreeDigest(directory);
        var loaded = Load(directory);
        Assert.Equal(before, RlSourceFixture.TreeDigest(directory));
        Assert.Equal(prepared.Manifest, loaded.Manifest, new ManifestComparer());
        Assert.Equal(prepared.Revision, loaded.Revision);
        Assert.Equal(prepared.Calendar.ToArray(), loaded.Calendar.ToArray());
        Assert.Equal(prepared.Catalog.Validation.Select(b => b.Id).ToArray(), loaded.Catalog.Validation.Select(b => b.Id).ToArray());
    }

    [Fact]
    public async Task SymbolOrderOfTheRequestDoesNotChangeTheCanonicalPanelOrSplit()
    {
        Symbols(1_278, "AAA", "BBB", "CCC", "DDD", "EEE");
        var a = await Prepare(new[] { "EEE", "AAA", "CCC", "BBB", "DDD" }, 1_278, NewDirectory(), holdout: true);
        var b = await Prepare(new[] { "AAA", "BBB", "CCC", "DDD", "EEE" }, 1_278, NewDirectory(), holdout: true);
        Assert.Equal(a.Symbols.ToArray(), b.Symbols.ToArray());
        foreach (var panel in new[] { a, b })
        {
            var expected = RlPanelUniverse.AssignHoldout(panel.Symbols, panel.Manifest.SplitSeed, 25);
            Assert.Equal(expected.ToArray(), panel.Catalog.IsHoldout.ToArray());
        }
    }

    [Fact]
    public async Task RecapturingTheSameSymbolsKeepsTheSplitButChangesTheCapturedUniverseIdentity()
    {
        Symbols(1_278, "AAA", "BBB", "CCC", "DDD", "EEE", "FFF", "GGG", "HHH");
        string[] names = { "AAA", "BBB", "CCC", "DDD", "EEE", "FFF", "GGG", "HHH" };
        var first = await Prepare(names, 1_278, NewDirectory(), holdout: true);
        await Task.Delay(20); // distinct capture timestamps in source.json
        var second = await Prepare(names, 1_278, NewDirectory(), holdout: true);

        Assert.NotEqual(first.Manifest.Symbols[0].SourceManifestSha256, second.Manifest.Symbols[0].SourceManifestSha256);
        Assert.NotEqual(first.Manifest.MembershipSha256, second.Manifest.MembershipSha256);
        Assert.NotEqual(first.Manifest.UniverseSha256, second.Manifest.UniverseSha256);
        Assert.Equal(first.Manifest.SplitSeed, second.Manifest.SplitSeed);
        Assert.Equal(first.Manifest.HoldoutSymbols.ToArray(), second.Manifest.HoldoutSymbols.ToArray());
        Assert.Equal(first.Catalog.IsHoldout.ToArray(), second.Catalog.IsHoldout.ToArray());
    }

    [Fact]
    public async Task ThePeriodAndTheHoldoutShareDoNotChangeTheSeed()
    {
        Symbols(1_400, "AAA", "BBB", "CCC", "DDD");
        string[] names = { "AAA", "BBB", "CCC", "DDD" };
        var shortPeriod = await Prepare(names, 1_300, NewDirectory(), holdout: true);
        var longPeriod = await Prepare(names, 1_400, NewDirectory(), holdout: true, settings: new RlPortfolioSettings { HoldoutPercent = 40 });
        Assert.Equal(shortPeriod.Manifest.SplitSeed, longPeriod.Manifest.SplitSeed);
    }

    [Fact]
    public async Task CurrencyAndTimezoneMismatchesAreRejectedAndLeaveNothingBehind()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "AAA", First, 1_278);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "BBB", First, 1_278, currency: "JPY");
        RlSourceFixture.WriteLocalSymbol(DataRoot, "CCC", First, 1_278, timezone: "America/New_York");
        string currency = NewDirectory(), timezone = NewDirectory();
        var jpy = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(new[] { "AAA", "BBB" }, 1_278, currency));
        Assert.StartsWith("PortfolioUniverse:", jpy.Message);
        Assert.Contains("BBB: currency JPY differs from the panel currency USD", jpy.Message);
        var zone = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(new[] { "AAA", "CCC" }, 1_278, timezone));
        Assert.Contains("CCC: exchange timezone America/New_York differs from the panel timezone UTC", zone.Message);
        Assert.False(Directory.Exists(currency));
        Assert.False(Directory.Exists(timezone));
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedBeforeAnyCapture()
    {
        Symbols(1_278, "AAA", "BBB", "CCC");
        string directory = NewDirectory();
        await Assert.ThrowsAsync<ArgumentException>(() => Prepare(new[] { "AAA", "aaa" }, 1_278, directory));
        await Assert.ThrowsAsync<ArgumentException>(() => Prepare(new[] { "AAA" }, 1_278, directory));
        await Assert.ThrowsAsync<ArgumentException>(() => Prepare(new[] { "AAA", "BBB", "CCC" }, 1_278, directory, holdout: true));
        await Assert.ThrowsAsync<ArgumentException>(() => Prepare(new[] { "AAA", "BBB", "CCC" }, 1_278, directory, settings: new RlPortfolioSettings { MaxSymbols = 2 }));
        await Assert.ThrowsAsync<ArgumentException>(() => RlPanelSnapshotFactory.PrepareAsync(new[] { "AAA", "BBB" }, First, First,
            directory, RlSourceFixture.Limits, _settings, false));
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        await Assert.ThrowsAsync<ArgumentException>(() => Prepare(new[] { "AAA", "BBB" }, 1_278, directory));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task AMissingOrNonEquitySymbolIsNamedAndTheDirectoryRemoved()
    {
        Symbols(1_278, "AAA");
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ETF", First, 1_278, quoteType: "ETF");
        string missing = NewDirectory(), etf = NewDirectory();
        var none = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(new[] { "AAA", "ZZZ" }, 1_278, missing));
        Assert.StartsWith("PortfolioUniverse: ZZZ:", none.Message);
        Assert.IsType<FileNotFoundException>(none.InnerException);
        var nonEquity = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(new[] { "AAA", "ETF" }, 1_278, etf));
        Assert.StartsWith("PortfolioUniverse: ETF:", nonEquity.Message);
        Assert.False(Directory.Exists(missing));
        Assert.False(Directory.Exists(etf));
    }

    [Fact]
    public async Task InsufficientCalendarSupportIsReportedAndRemoved()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(new[] { "AAA", "BBB" }, 1_278, directory, mode: RlPanelEvaluationMode.Standard));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        Assert.False(Directory.Exists(directory));
        var shortRun = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(new[] { "AAA", "BBB" }, 1_277, NewDirectory()));
        Assert.True(RlSourceContract.IsInsufficientData(shortRun));
    }

    [Fact]
    public async Task CancellationLeavesNoPartialArtifact()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(new[] { "AAA", "BBB" }, 1_278, directory, ct: cancelled.Token));
        Assert.False(Directory.Exists(directory));

        string loadDirectory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB" }, 1_278, loadDirectory);
        Assert.Throws<OperationCanceledException>(() => RlPanelSnapshot.Load(loadDirectory, RlSourceFixture.Limits, _settings,
            RlPanelEvaluationMode.ShortHistory, cancelled.Token));
    }

    [Fact]
    public async Task ATamperedSymbolFileIsDetectedByHash()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB" }, 1_278, directory);
        File.AppendAllText(Path.Combine(directory, "symbols", "BBB", RlFiles.Bars), " ");
        var error = Assert.Throws<InvalidDataException>(() => Load(directory));
        Assert.Contains("BBB:", error.Message);
        Assert.Contains("Source snapshot hash mismatch.", error.Message);
    }

    [Fact]
    public async Task ARewrittenSymbolManifestIsDetectedAgainstThePanelManifest()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB" }, 1_278, directory);
        // A still-valid source manifest (later completion time) has a different hash than the one the panel recorded.
        RlSourceFixture.Mutate(Path.Combine(directory, "symbols", "AAA"), RlFiles.SourceManifest,
            root => root["retrieval_completed_utc"] = DateTimeOffset.Parse(root["retrieval_completed_utc"]!.GetValue<string>()).AddMinutes(1).ToString("O"));
        var error = Assert.Throws<InvalidDataException>(() => Load(directory));
        Assert.Contains("AAA: the source manifest hash differs from the panel manifest.", error.Message);
    }

    [Fact]
    public async Task AMissingSymbolFolderIsReportedWithTheSymbol()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB" }, 1_278, directory);
        Directory.Delete(Path.Combine(directory, "symbols", "BBB"), true);
        var error = Assert.Throws<InvalidDataException>(() => Load(directory));
        Assert.Contains("BBB:", error.Message);
    }

    [Theory]
    [InlineData("schema_version", 1, "schema or specification is unsupported")] // the previous panel schema (capture-time split seed)
    [InlineData("schema_version", 3, "schema or specification is unsupported")]
    [InlineData("specification_id", "other", "schema or specification is unsupported")]
    [InlineData("calendar_sha256", "0000000000000000000000000000000000000000000000000000000000000000", "union calendar differs")]
    [InlineData("calendar_length", 1_277, "union calendar differs")]
    [InlineData("membership_sha256", "0000000000000000000000000000000000000000000000000000000000000000", "membership hash or split seed differs")]
    [InlineData("split_seed", 7, "membership hash or split seed differs")]
    [InlineData("universe_sha256", "0000000000000000000000000000000000000000000000000000000000000000", "symbol split or universe hash differs")]
    [InlineData("holdout_percent", 30, "symbol split or universe hash differs")]
    [InlineData("currency", "JPY", "currency USD differs from the panel currency JPY")]
    [InlineData("exchange_timezone", "America/New_York", "exchange timezone UTC differs")]
    [InlineData("captured_exchange_date", "2001-01-01", "captured exchange date")]
    [InlineData("period_start_inclusive", "2000-01-02", "stored period differs")]
    public async Task ATamperedPanelManifestIsRejected(string field, object value, string message)
    {
        Symbols(1_278, "AAA", "BBB", "CCC", "DDD");
        string directory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB", "CCC", "DDD" }, 1_278, directory, holdout: true);
        Mutate(directory, root => root[field] = System.Text.Json.JsonSerializer.SerializeToNode(value));
        var error = Assert.Throws<InvalidDataException>(() => Load(directory));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task StoredSymbolsMustBeCanonicalAndWithinTheConfiguredBounds()
    {
        Symbols(1_278, "AAA", "BBB", "CCC");
        string directory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB", "CCC" }, 1_278, directory);
        Assert.Throws<InvalidDataException>(() => RlPanelSnapshot.Load(directory, RlSourceFixture.Limits,
            new RlPortfolioSettings { MaxSymbols = 2 }, RlPanelEvaluationMode.ShortHistory));
        Mutate(directory, root =>
        {
            var symbols = root["symbols"]!.AsArray();
            var first = symbols[0]!.DeepClone(); var last = symbols[2]!.DeepClone();
            symbols[0] = last; symbols[2] = first;
        });
        var error = Assert.Throws<InvalidDataException>(() => Load(directory));
        Assert.Contains("canonical order", error.Message);
    }

    [Fact]
    public async Task LoadAppliesTheRequestedEvaluationMode()
    {
        Symbols(1_278, "AAA", "BBB");
        string directory = NewDirectory();
        await Prepare(new[] { "AAA", "BBB" }, 1_278, directory);
        var error = Assert.Throws<InvalidDataException>(() => Load(directory, RlPanelEvaluationMode.Standard));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        Assert.Equal(RlPanelEvaluationMode.ShortHistory, Load(directory).Catalog.Mode);
    }

    [Fact]
    public void LoadChecksItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => RlPanelSnapshot.Load(_root, null!, _settings));
        Assert.Throws<ArgumentNullException>(() => RlPanelSnapshot.Load(_root, RlSourceFixture.Limits, null!));
        Assert.ThrowsAny<IOException>(() => Load(Path.Combine(_root, "absent")));
    }

    private sealed class ManifestComparer : System.Collections.Generic.IEqualityComparer<RlPanelManifest>
    {
        public bool Equals(RlPanelManifest? x, RlPanelManifest? y) => x is not null && y is not null
            && x with { Symbols = default, HoldoutSymbols = default } == y with { Symbols = default, HoldoutSymbols = default }
            && x.Symbols.SequenceEqual(y.Symbols) && x.HoldoutSymbols.SequenceEqual(y.HoldoutSymbols);
        public int GetHashCode(RlPanelManifest obj) => obj.UniverseSha256.GetHashCode();
    }
}
