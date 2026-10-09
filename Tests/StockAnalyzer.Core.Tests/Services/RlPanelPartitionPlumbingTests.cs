#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The partition plan through the layers around the catalog: panel snapshot, preflight and roles, the plan evidence file, the report
/// validator and the plan owner of the service. Panels are built from real local Parquet files in an isolated Data root; the bars are
/// bounded integrity fixtures (flat prices on consecutive days), never market data.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlPanelPartitionPlumbingTests : IDisposable
{
    private static readonly DateOnly First = RlSourceFixture.FirstDay;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_partition_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private readonly RlPortfolioSettings _settings = new();
    private int _counter;
    private string DataRoot => Path.Combine(_root, "data");
    private string Daily => Path.Combine(DataRoot, "Daily");
    private string Meta => Path.Combine(DataRoot, "Metadata");

    public RlPanelPartitionPlumbingTests()
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

    private static RlPanelPartitionPlan BlockCount(int train = 1, int validation = 1, int test = 1)
        => new(RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(train, validation, test));

    private static RlPanelPartitionPlan Ratio(int train = 1, int validation = 1, int test = 1)
        => new(RlPortfolioPartitionMode.Ratio, new RlPanelBlockRequirement(train, validation, test));

    private string NewDirectory() => Path.Combine(_root, "panel_" + _counter++);

    private void Write(string symbol, int count, int offset = 0) => RlSourceFixture.WriteLocalSymbol(DataRoot, symbol, First.AddDays(offset), count);

    private void WriteFour(int count) { foreach (string s in new[] { "AAA", "BBB", "CCC", "DDD" }) Write(s, count); }

    private static readonly string[] Four = { "AAA", "BBB", "CCC", "DDD" };

    private Task<RlPanelSnapshot> Prepare(string[] symbols, int days, string directory, RlPanelPartitionPlan? plan,
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory, bool holdout = false)
        => RlPanelSnapshotFactory.PrepareAsync(symbols, First, First.AddDays(days), directory, RlSourceFixture.Limits, _settings, holdout, mode, plan);

    private PanelPreflight Preflight(string[] symbols, int days, RlPanelPartitionPlan? plan, RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory)
        => RlPanelPreflightEvaluator.Evaluate(symbols, First, First.AddDays(days), false, mode, _settings, RlSourceFixture.Limits, Daily, Meta,
            CancellationToken.None, plan);

    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m });

    // ----- panel snapshot -----

    [Fact]
    public async Task APanelOf1218BarsLoadsOnlyUnderTheBlockCountPlan()
    {
        WriteFour(1_218);
        // Ratio default: the same InsufficientData as before (1218 < 1278), and the failed capture removes its folder.
        string failed = NewDirectory();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(Four, 1_218, failed, null));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        Assert.False(Directory.Exists(failed));

        string directory = NewDirectory();
        var panel = await Prepare(Four, 1_218, directory, BlockCount());
        Assert.Equal(("train:514:706", "validation:770:962", "oos:1026:1218"),
            (panel.Catalog.Train.Single().Id, panel.Catalog.Validation.Single().Id, panel.Catalog.Oos.Single().Id));
        Assert.Equal((706, 962), (panel.Catalog.TrainCut, panel.Catalog.ValidationCut));
        // The stored panel does not know its plan: loading it again with the plan gives the same catalog, without a plan it is rejected.
        var again = RlPanelSnapshot.Load(directory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory, BlockCount());
        Assert.Equal(panel.Catalog.Train.Single().Id, again.Catalog.Train.Single().Id);
        Assert.Throws<InvalidDataException>(() => RlPanelSnapshot.Load(directory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory));
    }

    [Fact]
    public async Task ANullPlanAndTheDefaultPlanLoadTheSamePanelAsTheMethodWithoutAPlan()
    {
        WriteFour(1_400);
        string directory = NewDirectory();
        var panel = await Prepare(Four, 1_400, directory, null);
        var withoutPlan = RlPanelSnapshot.Load(directory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory);
        var nullPlan = RlPanelSnapshot.Load(directory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory, null);
        var defaultPlan = RlPanelSnapshot.Load(directory, RlSourceFixture.Limits, _settings, RlPanelEvaluationMode.ShortHistory, Ratio());
        foreach (var other in new[] { withoutPlan, nullPlan, defaultPlan })
        {
            Assert.Equal(panel.Revision, other.Revision);
            Assert.Equal(panel.Catalog.Train.Select(b => b.Id), other.Catalog.Train.Select(b => b.Id));
            Assert.Equal(panel.Catalog.Validation.Select(b => b.Id), other.Catalog.Validation.Select(b => b.Id));
            Assert.Equal(panel.Catalog.Oos.Select(b => b.Id), other.Catalog.Oos.Select(b => b.Id));
            Assert.Equal((panel.Catalog.TrainCut, panel.Catalog.ValidationCut), (other.Catalog.TrainCut, other.Catalog.ValidationCut));
        }
    }

    [Fact]
    public async Task APlanOnTheStandardModeIsRejectedByTheSnapshot()
    {
        WriteFour(1_400);
        string directory = NewDirectory();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Prepare(Four, 1_400, directory, Ratio(), RlPanelEvaluationMode.Standard));
        Assert.False(Directory.Exists(directory));
    }

    // ----- preflight and roles -----

    [Fact]
    public void FourSymbolsOf1218BarsAreInfeasibleByRatioAndFeasibleByBlockCountWithRoleTrains()
    {
        WriteFour(1_218);
        var ratio = Preflight(Four, 1_218, null);
        Assert.False(ratio.Feasible);
        Assert.Equal(1_278, ratio.RequiredCalendarLength);

        var block = Preflight(Four, 1_218, BlockCount());
        Assert.True(block.Feasible);
        Assert.Equal(704, block.RequiredCalendarLength);
        Assert.Equal(1_218, block.CalendarLength);
        Assert.All(block.Symbols, s => Assert.Equal((RlPanelSymbolStatus.Ready, RlPanelSymbolRole.Trains), (s.Status, s.Role)));
        Assert.Equal(new[] { ("train", 1, 1), ("validation", 1, 1), ("oos", 1, 1) },
            block.Cells.Select(c => (c.Partition, c.BlockCount, c.RequiredBlocks)).ToArray());
    }

    [Fact]
    public void ARatioPreflightWithoutAPlanAndWithTheDefaultPlanAgree()
    {
        WriteFour(1_400);
        var none = Preflight(Four, 1_400, null);
        var plan = Preflight(Four, 1_400, Ratio());
        Assert.Equal((none.Feasible, none.CalendarLength, none.RequiredCalendarLength), (plan.Feasible, plan.CalendarLength, plan.RequiredCalendarLength));
        Assert.Equal(none.Cells.Select(c => (c.Partition, c.BlockCount, c.RequiredBlocks)), plan.Cells.Select(c => (c.Partition, c.BlockCount, c.RequiredBlocks)));
        Assert.Equal(none.Symbols.Select(s => (s.Symbol, s.Status, s.BarsInPeriod)), plan.Symbols.Select(s => (s.Symbol, s.Status, s.BarsInPeriod)));
        Assert.All(none.Symbols, s => Assert.Equal(RlPanelSymbolRole.Trains, s.Role));
    }

    [Theory]
    [InlineData(500, RlPanelSymbolStatus.Ready, RlPanelSymbolRole.EvaluatedOnly)]   // 448 <= 500 < 704
    [InlineData(300, RlPanelSymbolStatus.Ready, RlPanelSymbolRole.EvaluatedOnly)]   // 192 <= 300 < 448: test block only
    [InlineData(150, RlPanelSymbolStatus.NoValidBlock, RlPanelSymbolRole.None)]     // fewer than one block
    public void ANewlyListedSymbolNextToLongOnesGetsItsRoleFromItsBars(int bars, RlPanelSymbolStatus status, RlPanelSymbolRole role)
    {
        const int days = 1_500;
        Write("AAA", days); Write("BBB", days); Write("NEW", bars, days - bars);
        var result = Preflight(new[] { "AAA", "BBB", "NEW" }, days, BlockCount());
        var symbol = result.Symbols.Single(s => s.Symbol == "NEW");
        Assert.Equal((status, role), (symbol.Status, symbol.Role));
        Assert.True(result.Feasible);
        Assert.All(result.Symbols.Where(s => s.Symbol != "NEW"), s => Assert.Equal(RlPanelSymbolRole.Trains, s.Role));
    }

    [Fact]
    public void ARatioSymbolPresentOnlyInTheLastFortyPercentIsEvaluatedOnly()
    {
        const int days = 1_400;
        Write("AAA", days); Write("BBB", days); Write("NEW", days - 840, 840);
        var result = Preflight(new[] { "AAA", "BBB", "NEW" }, days, null);
        Assert.True(result.Feasible);
        Assert.Equal(RlPanelSymbolRole.EvaluatedOnly, result.Symbols.Single(s => s.Symbol == "NEW").Role);
    }

    [Fact]
    public void ANotReadySymbolHasNoRole()
    {
        Write("AAA", 1_218); Write("BBB", 1_218);
        var result = Preflight(new[] { "AAA", "BBB", "MISSING" }, 1_218, BlockCount());
        Assert.Equal((RlPanelSymbolStatus.MissingFile, RlPanelSymbolRole.None), (result.Symbols.Single(s => s.Symbol == "MISSING").Status,
            result.Symbols.Single(s => s.Symbol == "MISSING").Role));
    }

    [Fact]
    public void ThePreflightRejectsAPlanOnTheStandardMode()
    {
        WriteFour(1_400);
        var error = Assert.Throws<ArgumentException>(() => Preflight(Four, 1_400, BlockCount(), RlPanelEvaluationMode.Standard));
        Assert.StartsWith(RlPortfolioContract.UniverseErrorPrefix, error.Message);
    }

    // ----- partition evidence -----

    [Fact]
    public void OnlyANonDefaultPlanPublishesPartitionEvidence()
    {
        Assert.False(RlPortfolioPartitionEvidence.IsPublished(null));
        Assert.False(RlPortfolioPartitionEvidence.IsPublished(Ratio()));
        Assert.True(RlPortfolioPartitionEvidence.IsPublished(Ratio(1, 2, 1)));
        Assert.True(RlPortfolioPartitionEvidence.IsPublished(BlockCount()));
        Assert.Equal("ratio", RlPortfolioPartitionEvidence.ModeName(RlPortfolioPartitionMode.Ratio));
        Assert.Equal("block_count", RlPortfolioPartitionEvidence.ModeName(RlPortfolioPartitionMode.BlockCount));
    }

    private async Task<(RlPanelSnapshot Panel, RlPanelPartitionPlan Plan)> BlockCountPanel()
    {
        WriteFour(1_218);
        var bars = new RlPanelMinimumBars(250, 192, 192);
        var plan = new RlPanelPartitionPlan(RlPortfolioPartitionMode.BlockCount, bars.ToRequirement(), bars);   // 250 bars = 2 train blocks
        return (await Prepare(Four, 1_218, NewDirectory(), plan), plan);
    }

    [Fact]
    public async Task PartitionEvidenceRoundTripsAndValidatesAgainstThePanel()
    {
        var (panel, plan) = await BlockCountPanel();
        var document = RlPortfolioPartitionEvidence.Create(plan, panel);
        // L = 1218, 2/1/1: cut2 = 962, cut1 = 706; 250 bars asked for the train partition = 2 blocks.
        Assert.Equal(new RlPortfolioPartitionDocument(1, "block_count", 250, 192, 192, 2, 1, 1, 1_218, 706, 962), document);
        string path = Path.Combine(_root, RlPortfolioPartitionEvidence.FileName);
        await RlPortfolioPartitionEvidence.SaveAsync(path, document);
        var read = RlPortfolioPartitionEvidence.Read(path);
        Assert.Equal(document, read);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(new[] { "calendar_length", "mode", "schema", "test_bars", "test_blocks", "train_bars", "train_blocks", "train_cut", "validation_bars", "validation_blocks", "validation_cut" },
            json.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        var validated = RlPortfolioPartitionEvidence.Validate(read, panel);
        Assert.Equal(plan.Requirement, validated.Requirement);
        Assert.Equal(plan.Mode, validated.Mode);
        Assert.Equal(plan.Bars, validated.Bars);
    }

    public static IEnumerable<object[]> Tamperings()
    {
        yield return new object[] { "schema", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { Schema = 2 }) };
        yield return new object[] { "mode", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { Mode = "other" }) };
        yield return new object[] { "mode swapped", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { Mode = "ratio" }) };
        yield return new object[] { "blocks", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { TrainBlocks = 3 }) };
        yield return new object[] { "bars", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { TrainBars = 400 }) };
        yield return new object[] { "zero bars", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { TestBars = 0 }) };
        yield return new object[] { "calendar", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { CalendarLength = 1_219 }) };
        yield return new object[] { "train cut", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { TrainCut = d.TrainCut + 1 }) };
        yield return new object[] { "validation cut", (Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument>)(d => d with { ValidationCut = d.ValidationCut - 1 }) };
    }

    [Theory]
    [MemberData(nameof(Tamperings))]
    public async Task ATamperedPartitionDocumentIsRejected(string name, Func<RlPortfolioPartitionDocument, RlPortfolioPartitionDocument> tamper)
    {
        var (panel, plan) = await BlockCountPanel();
        var document = RlPortfolioPartitionEvidence.Create(plan, panel);
        RlPortfolioPartitionEvidence.Validate(document, panel);
        var error = Assert.Throws<InvalidDataException>(() => RlPortfolioPartitionEvidence.Validate(tamper(document), panel));
        Assert.StartsWith("Partition document", error.Message);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public async Task APartitionDocumentWithoutBarsFallsBackToWholeBlocks()
    {
        WriteFour(1_218);
        var plan = BlockCount(2, 1, 1);
        var panel = await Prepare(Four, 1_218, NewDirectory(), plan);
        var document = RlPortfolioPartitionEvidence.Create(plan, panel);
        Assert.Equal((384, 192, 192), (document.TrainBars, document.ValidationBars, document.TestBars));
        Assert.Equal(plan.Requirement, RlPortfolioPartitionEvidence.Validate(document, panel).Requirement);
    }

    [Fact]
    public void LinkedOrMalformedPartitionFilesAreRejected()
    {
        string path = Path.Combine(_root, "malformed.json");
        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidDataException>(() => RlPortfolioPartitionEvidence.Read(path));
        File.WriteAllText(path, "null");
        Assert.Throws<InvalidDataException>(() => RlPortfolioPartitionEvidence.Read(path));
        Assert.Throws<ArgumentException>(() => RlPortfolioPartitionEvidence.Read(" "));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioPartitionEvidence.ToPlan(null!));
    }

    // ----- report validator -----

    [Fact]
    public async Task AReportIsValidatedAgainstThePlanMinimumsOfItsCells()
    {
        // 1/2/2 block-count plan on a 1278-bar panel: validation and test cells require two blocks each; without the plan they would require one.
        WriteFour(1_278);
        var plan = BlockCount(1, 2, 2);
        var panel = await Prepare(Four, 1_278, NewDirectory(), plan);
        var cells = RlPortfolioEvaluator.EvaluateCells(new RlPanelBarsFixture(4, (_, _) => (100m, 100m)), panel.Catalog, panel.Symbols, (_, tradable) =>
        {
            var assets = new decimal[4];
            assets[tradable[0]] = 1m;
            return new RlPortfolioWeightVector(assets.ToImmutableArray(), 0m);
        }, Costs(), 100, default);
        var report = RlPortfolioEvaluator.BuildReport(panel.Manifest, panel.Catalog, RlPortfolioEvaluator.SpecificationId(RlPanelEvaluationMode.ShortHistory),
            new string('e', 64), Costs(), 100, false, cells);
        Assert.All(report.Cells.Where(c => c.Partition != "train"), c => Assert.Equal(2, c.RequiredBlocks));
        string model = new string('e', 64);
        RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, model, Costs(), 100, plan);
        var withoutPlan = Assert.Throws<InvalidDataException>(() =>
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, model, Costs(), 100));
        Assert.Contains("block minimum", withoutPlan.Message);
        var wrongPlan = Assert.Throws<InvalidDataException>(() =>
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.ShortHistory, model, Costs(), 100, BlockCount(1, 1, 1)));
        Assert.Contains("block minimum", wrongPlan.Message);
        Assert.Throws<ArgumentException>(() =>
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, RlPanelEvaluationMode.Standard, model, Costs(), 100, plan));
    }

    // ----- the plan owner of the service -----

    private RlPortfolioService Service(RlPortfolioSettings settings) => new(settings, dataRoot: Path.Combine(_root, "service"));

    [Fact]
    public void DefaultSettingsResolveToNoPlanInEveryMode()
    {
        var service = Service(new RlPortfolioSettings());
        Assert.Null(service.ResolvePlan(RlPanelEvaluationMode.ShortHistory, null));
        Assert.Null(service.ResolvePlan(RlPanelEvaluationMode.Standard, null));
        Assert.Null(service.ResolvePlan(RlPanelEvaluationMode.ShortHistory, Ratio()));
    }

    [Fact]
    public void BlockCountSettingsResolveToAPlanForShortHistoryOnly()
    {
        var service = Service(new RlPortfolioSettings { PartitionMode = RlPortfolioPartitionMode.BlockCount, MinimumTrainBars = 400 });
        var plan = service.ResolvePlan(RlPanelEvaluationMode.ShortHistory, null)!;
        Assert.Equal((RlPortfolioPartitionMode.BlockCount, new RlPanelBlockRequirement(3, 1, 1), new RlPanelMinimumBars(400, 192, 192)),
            (plan.Mode, plan.Requirement, plan.Bars));
        // The Standard evaluation ignores the settings: its minimum is pinned.
        Assert.Null(service.ResolvePlan(RlPanelEvaluationMode.Standard, null));
        // A plan of the request wins over the settings, and a default request plan means no plan even when the settings say BlockCount.
        Assert.Null(service.ResolvePlan(RlPanelEvaluationMode.ShortHistory, Ratio()));
        Assert.Equal(Ratio(1, 2, 1), service.ResolvePlan(RlPanelEvaluationMode.ShortHistory, Ratio(1, 2, 1)));
    }

    [Fact]
    public void ARequestPlanOnTheStandardModeIsRejected()
    {
        var service = Service(new RlPortfolioSettings());
        var error = Assert.Throws<ArgumentException>(() => service.ResolvePlan(RlPanelEvaluationMode.Standard, BlockCount()));
        Assert.StartsWith(RlPortfolioContract.UniverseErrorPrefix, error.Message);
    }

    [Fact]
    public async Task TheServicePreflightFollowsTheSettingsPlan()
    {
        WriteFour(1_218);
        var symbols = Four;
        // The service reads the Data root through PathDiscovery (isolated here), so settings and the explicit plan reach the same preflight.
        var ratioService = Service(new RlPortfolioSettings());
        var blockService = Service(new RlPortfolioSettings { PartitionMode = RlPortfolioPartitionMode.BlockCount });
        var end = First.AddDays(1_218);
        var ratio = await ratioService.PreflightAsync(symbols, First, end, false, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        var block = await blockService.PreflightAsync(symbols, First, end, false, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        var explicitPlan = await ratioService.PreflightAsync(symbols, First, end, false, RlPanelEvaluationMode.ShortHistory, BlockCount(), CancellationToken.None);
        Assert.False(ratio.Feasible);
        Assert.True(block.Feasible);
        Assert.True(explicitPlan.Feasible);
        Assert.Equal(704, block.RequiredCalendarLength);
    }

    [Fact]
    public async Task TheServiceSuggestionFollowsThePlanToo()
    {
        WriteFour(1_218);
        var end = First.AddDays(1_218);
        var ratio = await Service(new RlPortfolioSettings()).SuggestStartAsync(Four, end, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        var settingsPlan = await Service(new RlPortfolioSettings { PartitionMode = RlPortfolioPartitionMode.BlockCount })
            .SuggestStartAsync(Four, end, RlPanelEvaluationMode.ShortHistory, CancellationToken.None);
        var explicitPlan = await Service(new RlPortfolioSettings()).SuggestStartAsync(Four, end, RlPanelEvaluationMode.ShortHistory, BlockCount(), CancellationToken.None);
        Assert.Equal(RlStartSuggestionStatus.InsufficientHistory, ratio!.Status);
        Assert.Equal(RlStartSuggestionStatus.Found, settingsPlan!.Status);
        Assert.Equal(settingsPlan, explicitPlan);
        Assert.Equal(704, settingsPlan.RequiredBars);
    }

    [Fact]
    public async Task TheRunnerRejectsAPlanOnTheStandardModeBeforeTouchingThePanel()
    {
        var runner = new RlPortfolioTrainingRunner(dataRoot: Path.Combine(_root, "service"));
        var request = new RlPortfolioTrainingRequest(Path.Combine(_root, "does-not-exist"), Costs(), _settings, true, null,
            RlPanelEvaluationMode.Standard, BlockCount());
        var error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(request));
        Assert.StartsWith(RlPortfolioContract.UniverseErrorPrefix, error.Message);
    }

    private sealed class LegacyService : IRlPortfolioService
    {
        public RlPortfolioSettings Settings { get; } = new();
        public int Calls;
        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested,
            RlPanelEvaluationMode mode, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PanelPreflight(start, endExclusive, mode, ImmutableArray<RlPanelSymbolPreflight>.Empty, 0, 0,
                ImmutableArray<RlPanelCellCount>.Empty, false, false));
        }
        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task AServiceThatPredatesPlansKeepsWorkingAndRefusesANonNullPlan()
    {
        var legacy = new LegacyService();
        IRlPortfolioService service = legacy;
        _ = await service.PreflightAsync(new[] { "A", "B" }, First, First.AddDays(10), false, RlPanelEvaluationMode.ShortHistory, null, CancellationToken.None);
        Assert.Equal(1, legacy.Calls);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.PreflightAsync(new[] { "A", "B" }, First, First.AddDays(10), false, RlPanelEvaluationMode.ShortHistory, BlockCount(), CancellationToken.None));
        Assert.Equal(1, legacy.Calls);
    }
}
