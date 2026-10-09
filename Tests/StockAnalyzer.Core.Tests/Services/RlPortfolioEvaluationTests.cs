using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Evaluation protocol on real in-memory bars with hand-computed ledgers. Calendar of the fixture: 1278 bars, short-history catalog: validation block
/// 830..1022 (decisions at 893..1020), OOS block 1086..1278 (decisions at 1149..1276). Initial capital 10000, no costs unless stated.
/// Prices (open = close): symbol 0 is 100 until 899, 110 from 900 and 121 from 951; symbol 1 is 50 until 1159 and 55 from 1160;
/// symbols 2 and 3 (the holdout group) stay at 20 and 40.
/// </summary>
public sealed class RlPortfolioEvaluationTests : IDisposable
{
    private const int Length = 1278;
    private static readonly string[] Names = { "AAA", "BBB", "HHH", "III" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_portfolio_eval_" + Guid.NewGuid().ToString("N"));
    public RlPortfolioEvaluationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private static BacktestConfiguration Costs(decimal commissionFlat = 0m) => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m, CommissionFlat = commissionFlat });

    private static ReadOnlyMemory<int> Prefix(int first) => Enumerable.Range(0, Length + 1).Select(t => Math.Max(0, t - first)).ToArray();

    /// <summary>Four symbols; the last two are the holdout group and are present from <paramref name="holdoutFirst"/>.</summary>
    private static RlPanelCatalog Catalog(int holdoutFirst = 0, bool holdout = true, RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory)
        => RlPanelCatalogBuilder.Build(Length, new[] { Prefix(0), Prefix(0), Prefix(holdoutFirst), Prefix(holdoutFirst) },
            ImmutableArray.Create(false, false, holdout, holdout), mode);

    private static RlPanelBarsFixture Bars(int holdoutFirst = 0) => new(4, (s, t) => s switch
    {
        0 => t < 900 ? (100m, 100m) : t < 951 ? (110m, 110m) : (121m, 121m),
        1 => t < 1160 ? (50m, 50m) : (55m, 55m),
        _ => t < holdoutFirst ? null : s == 2 ? (20m, 20m) : (40m, 40m),
    });

    /// <summary>The policy puts the whole account into the first tradable symbol.</summary>
    private static RlPortfolioEvaluator.Policy AllInFirst => (_, tradable) =>
    {
        var assets = new decimal[4];
        assets[tradable[0]] = 1m;
        return new RlPortfolioWeightVector(assets.ToImmutableArray(), 0m);
    };

    private static RlPortfolioBlockResult Single(RlPortfolioCellReport cell) => Assert.Single(cell.Blocks);

    private static void AssertDistribution(RlReturnDistribution actual, decimal value)
    {
        Assert.Equal(1, actual.Count);
        Assert.Equal(value, actual.Mean); Assert.Equal(value, actual.Median);
        Assert.Equal(value, actual.Minimum); Assert.Equal(value, actual.Maximum);
        Assert.Equal(value > 0m ? 1m : 0m, actual.PositiveRate);
    }

    [Fact]
    public void HoldKeepsTheQuantitiesAndOnlyMarksTheAccount()
    {
        // Step 0: weights 0.5/0.5 -> 50 shares at 100 and 100 at 50, no cash. Symbol 0 is 110 from index 65 on. Hold: no order, no cost,
        // E = 50 * 110 + 100 * 50 = 10500, reward 500 / 10000 = 0.05.
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m }, new Dictionary<(int, int), (decimal, decimal)>
            { [(0, 65)] = (110m, 110m) });
        using var session = new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), Costs(commissionFlat: 1m));
        Assert.Throws<InvalidOperationException>(() => session.Hold());     // Created, not Ready
        session.Reset();
        var first = session.Step(ImmutableArray.Create(0.4m, 0.4m));
        var held = session.Hold();
        Assert.Equal(first.Snapshot.OrderCount, held.Snapshot.OrderCount);
        Assert.Equal(first.Snapshot.CommissionPaid, held.Snapshot.CommissionPaid);
        Assert.Equal(first.Snapshot.Quantities, held.Snapshot.Quantities);
        Assert.Equal(1, first.Snapshot.CompletedDecisions);
        Assert.Equal(2, held.Snapshot.CompletedDecisions);
        // 0.4 weights: 40 shares at 100 (4000 + 1) and 80 at 50 (4000 + 1): cash 1998; E after Hold = 40 * 110 + 80 * 50 + 1998 = 10398.
        Assert.Equal(new[] { 40m, 80m }, held.Snapshot.Quantities.ToArray());
        Assert.Equal(9_998m, first.Snapshot.Equity);      // 40 * 100 + 80 * 50 + 1998 cash
        Assert.Equal(10_398m, held.Snapshot.Equity);
        Assert.Equal(0.04m, held.Reward);                  // (10398 - 9998) / 10000
        for (int i = 2; i < RlContract.EpisodeHorizon - 1; i++) Assert.False(session.Hold().Terminated);
        Assert.True(session.Hold().Terminated);
        Assert.Throws<InvalidOperationException>(() => session.Hold());       // Terminated
    }

    [Fact]
    public void HoldRespectsCancellationBeforeTheMutation()
    {
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m });
        using var session = new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), Costs());
        session.Reset();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Hold(cancelled.Token));
        Assert.Equal(1, session.Hold().Snapshot.CompletedDecisions);          // the cancelled call changed nothing
    }

    [Fact]
    public void EqualWeightsFloorToTheContractDecimalsAndCashHoldsTheResidual()
    {
        var three = RlPortfolioWeights.EqualWeights(new[] { true, true, false, true }, 100);
        Assert.Equal(new[] { 0.3333m, 0.3333m, 0m, 0.3333m }, three.Assets.ToArray());      // floor(10000 / 3) / 10000
        Assert.Equal(0.0001m, three.Cash);
        var two = RlPortfolioWeights.EqualWeights(new[] { true, true }, 100);
        Assert.Equal(new[] { 0.5m, 0.5m }, two.Assets.ToArray());
        Assert.Equal(0m, two.Cash);
        var capped = RlPortfolioWeights.EqualWeights(new[] { true, true }, 40);              // the policy's cap applies to the baselines
        Assert.Equal(new[] { 0.4m, 0.4m }, capped.Assets.ToArray());
        Assert.Equal(0.2m, capped.Cash);
        var none = RlPortfolioWeights.EqualWeights(new[] { false, false }, 100);
        Assert.Equal(new[] { 0m, 0m }, none.Assets.ToArray());
        Assert.Equal(1m, none.Cash);
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioWeights.EqualWeights(new[] { true }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlPortfolioWeights.EqualWeights(new[] { true }, 101));
    }

    [Fact]
    public void CellsMatchTheHandLedgersOfThePolicyAndEveryBaseline()
    {
        var cells = RlPortfolioEvaluator.EvaluateCells(Bars(), Catalog(), Names.ToImmutableArray(), AllInFirst, Costs(), 100, default);
        Assert.Equal(new[] { ("validation", RlPanelSymbolGroup.Train), ("oos", RlPanelSymbolGroup.Train),
            ("validation", RlPanelSymbolGroup.Holdout), ("oos", RlPanelSymbolGroup.Holdout) }, cells.Select(c => (c.Partition, c.Group)).ToArray());
        Assert.All(cells, c => { Assert.True(c.Evaluable); Assert.Equal(1, c.RequiredBlocks); Assert.Equal(1, c.BlockCount); });

        // validation/Train: all-in symbol 0 (100 shares) ends at 121 -> 12100, return 0.21. cash 0. equal buy-and-hold: 50 and 100 shares,
        // 50 * 121 + 100 * 50 = 11050, return 0.105. Equal daily rebalanced: after the jump to 110 it holds 47 / 105 and cash 80 (E 10500); after the
        // jump to 121, E = 47 * 121 + 5250 + 80 = 11017 and it moves to 45 / 110 with cash 72: return 0.1017.
        var validation = cells[0];
        Assert.Equal(new[] { "AAA", "BBB" }, validation.Symbols.ToArray());
        var row = Single(validation);
        Assert.Equal("validation:830:1022", row.BlockId);
        Assert.Equal(new[] { "AAA", "BBB" }, row.TradableSymbols.ToArray());
        Assert.Equal((0.21m, 0m, 0.105m, 0.1017m), (row.PolicyReturn, row.CashReturn, row.EqualBuyHoldReturn, row.EqualDailyRebalancedReturn));
        AssertDistribution(validation.Policy!, 0.21m);
        Assert.Equal(new[] { "cash", "equal_buy_hold", "equal_daily_rebalanced" }, validation.Baselines.Select(b => b.Id).ToArray());
        AssertDistribution(validation.Baselines[0].Returns, 0m); AssertDistribution(validation.Baselines[0].PairedDelta, 0.21m);
        AssertDistribution(validation.Baselines[1].Returns, 0.105m); AssertDistribution(validation.Baselines[1].PairedDelta, 0.105m);
        AssertDistribution(validation.Baselines[2].Returns, 0.1017m); AssertDistribution(validation.Baselines[2].PairedDelta, 0.1083m);

        // oos/Train: symbol 0 stays at 121 (all-in policy: 82 shares, return 0); symbol 1 rises to 55 at index 1160. Buy-and-hold: 41 and 100
        // shares, cash 39, E = 4961 + 5500 + 39 = 10500; daily rebalanced ends at 10500 as well (43 / 95, cash 72).
        var oos = Single(cells[1]);
        Assert.Equal("oos:1086:1278", oos.BlockId);
        Assert.Equal((0m, 0m, 0.05m, 0.05m), (oos.PolicyReturn, oos.CashReturn, oos.EqualBuyHoldReturn, oos.EqualDailyRebalancedReturn));
        AssertDistribution(cells[1].Baselines[1].PairedDelta, -0.05m);
        AssertDistribution(cells[1].Baselines[2].PairedDelta, -0.05m);

        // Holdout group: symbols 2 and 3 never move, so every arm returns 0; the policy only ever traded its own group's first symbol.
        foreach (var cell in cells.Skip(2))
        {
            Assert.Equal(new[] { "HHH", "III" }, cell.Symbols.ToArray());
            var block = Single(cell);
            Assert.Equal((0m, 0m, 0m, 0m), (block.PolicyReturn, block.CashReturn, block.EqualBuyHoldReturn, block.EqualDailyRebalancedReturn));
        }
    }

    [Fact]
    public void ARejectedBuyIsCountedWholeAndNeverClippedInEveryArm()
    {
        // Flat 100 / 50, commission 1 per order, two symbols and no holdout. Policy: 100 shares cost 10001 > 10000, rejected every decision, return 0.
        // Equal buy-and-hold: buy 50 of symbol 0 (5001), symbol 1 (5001 > 4999) rejected: E = 4999 + 5000 = 9999, return -0.0001, then no more orders.
        // Equal daily rebalanced: decision 1 targets 49 / 99 (E 9999): sells 1 (+99), buys 99 (4951): E = 4900 + 4950 + 147 = 9997, then stable: -0.0003.
        var catalog = RlPanelCatalogBuilder.Build(Length, new[] { Prefix(0), Prefix(0) }, ImmutableArray.Create(false, false), RlPanelEvaluationMode.ShortHistory);
        var bars = RlPanelBarsFixture.Flat(new[] { 100m, 50m });
        var cells = RlPortfolioEvaluator.EvaluateCells(bars, catalog, ImmutableArray.Create("AAA", "BBB"),
            (_, tradable) => new RlPortfolioWeightVector(ImmutableArray.Create(1m, 0m), 0m), Costs(commissionFlat: 1m), 100, default);
        Assert.Equal(2, cells.Length);
        foreach (var cell in cells)
        {
            var row = Single(cell);
            Assert.Equal((0m, 0m, -0.0001m, -0.0003m), (row.PolicyReturn, row.CashReturn, row.EqualBuyHoldReturn, row.EqualDailyRebalancedReturn));
        }
    }

    [Fact]
    public void AHoldoutCellBelowItsMinimumIsStatedNotEvaluableAndNeverDropped()
    {
        // Holdout symbols are listed at index 1000: the validation block (830..1022) is incomplete for them, the OOS block (1086..1278) is complete.
        var catalog = Catalog(holdoutFirst: 1000);
        var cells = RlPortfolioEvaluator.EvaluateCells(Bars(1000), catalog, Names.ToImmutableArray(), AllInFirst, Costs(), 100, default);
        Assert.Equal(4, cells.Length);
        var missing = cells[2];
        Assert.Equal(("validation", RlPanelSymbolGroup.Holdout, 0, 1, false), (missing.Partition, missing.Group, missing.BlockCount, missing.RequiredBlocks, missing.Evaluable));
        Assert.True(missing.Blocks.IsEmpty && missing.Policy is null && missing.Baselines.IsEmpty);
        Assert.True(cells[3].Evaluable);
        Assert.Equal(1, cells[3].BlockCount);
    }

    [Fact]
    public void ATrainingCellBelowItsMinimumIsInsufficientDataAtCatalogTime()
    {
        // Two of four symbols are the whole training group; if they are listed at 1000 the validation block is incomplete for them.
        var presence = new[] { Prefix(1000), Prefix(1000), Prefix(0), Prefix(0) };
        var error = Assert.Throws<InvalidDataException>(() => RlPanelCatalogBuilder.Build(Length, presence,
            ImmutableArray.Create(false, false, true, true), RlPanelEvaluationMode.ShortHistory));
        Assert.True(RlSourceContract.IsInsufficientData(error));
    }

    [Fact]
    public void TrainingNeverReadsHoldoutSymbolsOrLaterPartitions()
    {
        var bars = Bars();
        var catalog = Catalog();
        Assert.Equal(766, catalog.TrainCut);
        // The scaler fit sees training blocks of training symbols only.
        var scaler = RlPortfolioScaler.Fit(bars, catalog);
        Assert.NotEmpty(bars.Reads);
        Assert.All(bars.Reads, r => { Assert.True(r.Symbol is 0 or 1, "a holdout symbol was read"); Assert.True(r.Index < catalog.TrainCut, "a later partition was read"); });

        // The host: one full training episode and one full validation episode, driven like the worker does.
        bars.Reads.Clear();
        var host = new RlPortfolioIpcHost(Guid.Parse("11111111-2222-3333-4444-555555555555"), bars, catalog, scaler, 4, Costs(), 100);
        long sequence = 0;
        void Call(string method, object payload, string session = "")
        {
            var frame = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["protocol_version"] = 1, ["run_id"] = "11111111222233334444555555555555", ["contract_hash"] = RlPortfolioContract.ContractHash,
                ["sequence"] = ++sequence, ["session_id"] = session, ["method"] = method, ["payload"] = payload,
            });
            host.Handle(frame, default);
        }
        string train = Guid.NewGuid().ToString("N"), validation = Guid.NewGuid().ToString("N");
        Call("Ready", new { });
        Call("Reset", new { role = "train", block_id = catalog.Train[0].Id, seed = 42, checkpoint = 0 }, train);
        for (int i = 0; i < RlContract.EpisodeHorizon; i++) Call("Step", new { actions = new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f } }, train);
        Assert.All(bars.Reads, r => { Assert.True(r.Symbol is 0 or 1, "a holdout symbol was read"); Assert.True(r.Index < catalog.TrainCut, "a later partition was read"); });
        bars.Reads.Clear();
        Call("Reset", new { role = "validation", block_id = catalog.Validation[0].Id, seed = 42, checkpoint = 0 }, validation);
        for (int i = 0; i < RlContract.EpisodeHorizon; i++) Call("Step", new { actions = new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f } }, validation);
        Assert.All(bars.Reads, r =>
        {
            Assert.True(r.Symbol is 0 or 1, "a holdout symbol was read");
            Assert.InRange(r.Index, catalog.Validation[0].StartIndex, catalog.Validation[0].EndExclusive - 1);
        });
    }

    [Fact]
    public void EvaluationReadsOnlyTheCellsOwnSymbolsInsideItsBlocks()
    {
        var bars = Bars();
        var catalog = Catalog();
        var cells = RlPortfolioEvaluator.EvaluateCells(bars, catalog, Names.ToImmutableArray(), AllInFirst, Costs(), 100, default);
        Assert.Equal(4, cells.Length);
        var ranges = catalog.Validation.Concat(catalog.Oos).Select(b => (b.StartIndex, b.EndExclusive)).ToArray();
        Assert.NotEmpty(bars.Reads);
        Assert.All(bars.Reads, r =>
            Assert.True(ranges.Any(range => r.Index >= range.StartIndex && r.Index < range.EndExclusive), "a bar outside the evaluation blocks was read"));
    }

    // ----- report -----

    private static RlPanelManifest Manifest(bool holdout = true)
        => new(RlPortfolioContract.PanelSchemaVersion, RlPortfolioContract.SpecificationId,
            Names.Select(n => new RlPanelSymbolEntry(n, new string('a', 64), Length, new DateOnly(2000, 1, 1), new DateOnly(2003, 6, 30))).ToImmutableArray(),
            "USD", "UTC", new DateOnly(2030, 1, 1), new DateOnly(2000, 1, 1), new DateOnly(2003, 7, 1), new string('b', 64), Length, new string('c', 64),
            1234u, holdout, 50, holdout ? ImmutableArray.Create("HHH", "III") : ImmutableArray<string>.Empty, new string('d', 64));

    private static (RlPortfolioEvaluationReport Report, RlPanelManifest Manifest) Report(bool holdout = true,
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory)
    {
        var catalog = holdout ? Catalog(mode: mode) : RlPanelCatalogBuilder.Build(Length,
            new[] { Prefix(0), Prefix(0), Prefix(0), Prefix(0) }, ImmutableArray.Create(false, false, false, false), mode);
        var cells = RlPortfolioEvaluator.EvaluateCells(Bars(), catalog, Names.ToImmutableArray(), AllInFirst, Costs(), 100, default);
        var manifest = Manifest(holdout);
        return (RlPortfolioEvaluator.BuildReport(manifest, catalog, RlPortfolioEvaluator.SpecificationId(mode), new string('e', 64), Costs(), 100, false, cells), manifest);
    }

    private static void Validate(RlPortfolioEvaluationReport report, RlPanelManifest manifest, RlPanelEvaluationMode mode = RlPanelEvaluationMode.ShortHistory,
        string? modelHash = null) => RlPortfolioEvaluationReports.Validate(report, manifest, mode, modelHash ?? new string('e', 64), Costs(), 100);

    [Fact]
    public async Task AReportRoundTripsThroughDiskAndValidates()
    {
        var (report, manifest) = Report();
        Assert.Equal((RlPortfolioContract.SpecificationId, RlPortfolioEvaluationContract.SchemaVersion, RlPortfolioEvaluationContract.ContractHash),
            (report.SpecificationId, report.SchemaVersion, report.ContractHash));
        Assert.Equal(RlSourceContract.Unverified, report.PricePit);
        Assert.Equal(RlConstraintContract.InvestmentEfficacy, report.InvestmentEfficacy);
        Assert.Contains("not an efficacy claim", report.Statement);
        Assert.Contains(RlPortfolioEvaluationContract.ShortHistoryNote, report.Notes);
        Assert.DoesNotContain(RlPortfolioEvaluationContract.TimeHoldoutOnlyNote, report.Notes);
        Validate(report, manifest);
        string path = Path.Combine(_root, RlPortfolioEvaluationContract.ReportFile);
        await RlPortfolioEvaluationReports.SaveAsync(path, report);
        var read = RlPortfolioEvaluationReports.Read(path);
        Assert.Equal(JsonSerializer.Serialize(report, RlFiles.Json), JsonSerializer.Serialize(read, RlFiles.Json));
        Validate(read, manifest);
    }

    [Fact]
    public void AReportWithoutASymbolHoldoutSaysTimeHoldoutOnly()
    {
        var (report, manifest) = Report(holdout: false);
        Assert.Equal(2, report.Cells.Length);
        Assert.Contains(RlPortfolioEvaluationContract.TimeHoldoutOnlyNote, report.Notes);
        Assert.False(report.SymbolHoldout);
        Validate(report, manifest);
    }

    [Fact]
    public void AStandardModeReportCarriesTheStandardSpecificationAndMinimum()
    {
        // The 1278-bar fixture cannot satisfy the standard minimum, so the standard-mode report is assembled from a catalog built for 5118 bars.
        var catalog = RlPanelCatalogBuilder.Build(5118, new[] { (ReadOnlyMemory<int>)Enumerable.Range(0, 5119).ToArray(), Enumerable.Range(0, 5119).ToArray() },
            ImmutableArray.Create(false, false), RlPanelEvaluationMode.Standard);
        Assert.All(catalog.Cells.Where(c => c.Partition != RlPortfolioContract.TrainPartition),
            c => Assert.Equal(RlContract.EvaluationEpisodeCount, c.RequiredBlocks));
        Assert.Equal(RlFiles.EvaluationRevision, RlPortfolioEvaluator.SpecificationId(RlPanelEvaluationMode.Standard));
        var (shortReport, manifest) = Report();
        var downgraded = shortReport with { EvaluationSpecificationId = RlFiles.EvaluationRevision };
        Assert.Throws<InvalidDataException>(() => Validate(downgraded, manifest));
        Assert.Throws<InvalidDataException>(() => Validate(shortReport, manifest, RlPanelEvaluationMode.Standard));
    }

    public static IEnumerable<object[]> Tamperings()
    {
        yield return new object[] { "schema", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { SchemaVersion = 2 }) };
        yield return new object[] { "specification", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { SpecificationId = "SA-RL-PORTFOLIO-v0" }) };
        yield return new object[] { "evaluation contract hash", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { ContractHash = new string('0', 64) }) };
        yield return new object[] { "portfolio contract hash", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { PortfolioContractHash = new string('0', 64) }) };
        yield return new object[] { "universe", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { UniverseSha256 = new string('0', 64) }) };
        yield return new object[] { "calendar", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { CalendarSha256 = new string('0', 64) }) };
        yield return new object[] { "model hash", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { ModelSha256 = new string('0', 64) }) };
        yield return new object[] { "holdout list", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { HoldoutSymbols = ImmutableArray.Create("AAA", "BBB") }) };
        yield return new object[] { "train list", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { TrainSymbols = ImmutableArray.Create("AAA") }) };
        yield return new object[] { "costs", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Costs = Costs(commissionFlat: 1m) }) };
        yield return new object[] { "cap", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { MaxWeightPercent = 50 }) };
        yield return new object[] { "pit", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { PricePit = "verified" }) };
        yield return new object[] { "efficacy", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { InvestmentEfficacy = "Established" }) };
        yield return new object[] { "statement", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Statement = "" }) };
        yield return new object[] { "notes", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Notes = r.Notes.RemoveAt(1) }) };
        yield return new object[] { "missing cell", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Cells = r.Cells.RemoveAt(3) }) };
        yield return new object[] { "cell order", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Cells = r.Cells.Reverse().ToImmutableArray() }) };
        yield return new object[] { "block minimum", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Cells = r.Cells.SetItem(0, r.Cells[0] with { RequiredBlocks = 5 }) }) };
        yield return new object[] { "cell symbols", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Cells = r.Cells.SetItem(0, r.Cells[0] with { Symbols = ImmutableArray.Create("AAA") }) }) };
        yield return new object[] { "block return", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(0, r.Cells[0] with { Blocks = r.Cells[0].Blocks.SetItem(0, r.Cells[0].Blocks[0] with { PolicyReturn = 0.5m }) }) }) };
        yield return new object[] { "baseline return", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(1, r.Cells[1] with { Blocks = r.Cells[1].Blocks.SetItem(0, r.Cells[1].Blocks[0] with { EqualBuyHoldReturn = 0.9m }) }) }) };
        yield return new object[] { "policy statistics", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(0, r.Cells[0] with { Policy = r.Cells[0].Policy! with { Mean = 9m } }) }) };
        yield return new object[] { "paired delta", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(0, r.Cells[0] with { Baselines = r.Cells[0].Baselines.SetItem(0, r.Cells[0].Baselines[0] with { PairedDelta = r.Cells[0].Baselines[0].Returns }) }) }) };
        yield return new object[] { "baseline order", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(0, r.Cells[0] with { Baselines = r.Cells[0].Baselines.Reverse().ToImmutableArray() }) }) };
        yield return new object[] { "block count", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Cells = r.Cells.SetItem(0, r.Cells[0] with { BlockCount = 2 }) }) };
        yield return new object[] { "foreign block", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(0, r.Cells[0] with { Blocks = r.Cells[0].Blocks.SetItem(0, r.Cells[0].Blocks[0] with { BlockId = "oos:1086:1278" }) }) }) };
        yield return new object[] { "training cell not evaluable", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(0, r.Cells[0] with { BlockCount = 0, Evaluable = false, Blocks = ImmutableArray<RlPortfolioBlockResult>.Empty,
                Policy = null, Baselines = ImmutableArray<RlPortfolioBaselineSummary>.Empty }) }) };
        yield return new object[] { "not evaluable with results", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with
            { Cells = r.Cells.SetItem(2, r.Cells[2] with { BlockCount = 0, Evaluable = false }) }) };
        yield return new object[] { "wrong evaluability", (Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport>)(r => r with { Cells = r.Cells.SetItem(2, r.Cells[2] with { Evaluable = false }) }) };
    }

    [Theory]
    [MemberData(nameof(Tamperings))]
    public void AnyDeviationFromTheProtocolIsRejected(string label, Func<RlPortfolioEvaluationReport, RlPortfolioEvaluationReport> tamper)
    {
        var (report, manifest) = Report();
        Validate(report, manifest);
        var error = Assert.Throws<InvalidDataException>(() => Validate(tamper(report), manifest));
        Assert.StartsWith("Portfolio evaluation report", error.Message);
        Assert.NotEmpty(label);
    }

    [Fact]
    public void AReadRejectsLinkedOversizedAndMalformedFiles()
    {
        string path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluationReports.Read(path));
        File.WriteAllText(path, "null");
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluationReports.Read(path));
        File.WriteAllBytes(path, new byte[RlPortfolioEvaluationContract.MaximumReportBytes + 1]);
        Assert.Throws<InvalidDataException>(() => RlPortfolioEvaluationReports.Read(path));
    }

    [Fact]
    public void TheEvaluationContractIsPinnedAndLeavesTheTrainingContractsUntouched()
    {
        Assert.Equal(64, RlPortfolioEvaluationContract.ContractHash.Length);
        Assert.Equal("A9F7C8AC29790AF92714161DF24A39CC49C6BB470C0AD1EBFB38E617F066F00F", RlPortfolioContract.ContractHash);
        Assert.StartsWith("02434908AD71", RlContract.ContractHash);
        Assert.StartsWith("EFD84A0512E7", RlContinuousContract.ContractHash);
        Assert.Equal(new[] { "cash", "equal_buy_hold", "equal_daily_rebalanced" }, RlPortfolioEvaluationContract.Baselines.ToArray());
    }

    [Fact]
    public void EvaluateCellsRejectsMismatchedInputs()
    {
        var bars = Bars();
        var catalog = Catalog();
        Assert.Throws<ArgumentException>(() => RlPortfolioEvaluator.EvaluateCells(bars, catalog, ImmutableArray.Create("AAA"), AllInFirst, Costs(), 100, default));
        Assert.Throws<ArgumentNullException>(() => RlPortfolioEvaluator.EvaluateCells(bars, catalog, Names.ToImmutableArray(), null!, Costs(), 100, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RlPortfolioEvaluator.EvaluateCells(bars, catalog, Names.ToImmutableArray(), AllInFirst, Costs(), 100, cancelled.Token));
    }
}
