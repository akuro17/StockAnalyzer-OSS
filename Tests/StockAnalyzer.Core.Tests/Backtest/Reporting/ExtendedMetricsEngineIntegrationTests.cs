#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using StockAnalyzer.Core.Tests.Backtest.Verification;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest.Reporting;

/// <summary>
/// The 15 extended metrics on results produced by the REAL <see cref="BacktestEngine"/> (Y:\Temp\sa_implementation_plan_BacktestReportVerificationOracle.md, T10),
/// compared exactly with the independent <see cref="ExtendedMetricsReference"/> (whose exposure is rebuilt from the fills), plus a concurrency check.
/// Until now the metrics were only checked on hand-built results.
/// </summary>
public class ExtendedMetricsEngineIntegrationTests
{
    private static readonly DateTime Bar0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeInForce Gtc = new(true, -1);

    private static CandleData Bar(int day, decimal open, decimal high, decimal low, decimal close) => new(Bar0.AddDays(day), open, high, low, close, 1000);

    private static CandleData Flat(int day, decimal price) => Bar(day, price, price, price, price);

    private static StrategyOrderRequest Req(SignalType type) => new(type, OrderType.Market, null, null, Gtc, "r");

    private static BacktestConfiguration Cash() => new()
    {
        InitialCapital = 1_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 2m,
        InitialMarginRatio = 1m,
        MaintenanceMarginRatio = 0.5m,
    };

    private static BacktestConfiguration Margin() => new()
    {
        InitialCapital = 1_000m,
        SizingModel = PositionSizingModel.FixedQuantity,
        SizingParameter = 20m,
        InitialMarginRatio = 0.3m,
        MaintenanceMarginRatio = 0.2m,
    };

    private static BacktestResult Run(
        BacktestConfiguration config, ImmutableArray<CandleData> bars,
        Dictionary<int, StrategyOrderRequest> primary, Dictionary<int, StrategyOrderRequest>? accompanyingExit = null)
    {
        var input = new BacktestInput(bars, "TEST", TimeFrame.D1, BacktestInput.CurrentDataVersion, Bar0, Bar0.AddDays(bars.Length + 1), 0, 0);
        return VerificationHarness.CreateEngine().Run(input, config, new ScriptedStrategy(primary, accompanyingExit));
    }

    private static BacktestReport Generate(BacktestResult result) =>
        new BacktestReportGenerator().Generate(result, ReportTestHelpers.Options());

    private static void AssertMatchesReference(string label, BacktestResult result) =>
        ExtendedMetricsReference.AssertMatches(label, result, Generate(result), historyStartIndex: 0, tradingStartIndex: 0);

    // ---- Scripted engine scenarios ----------------------------------------------------------------------------------------------

    [Fact]
    public void MultipleRoundTrips_OnACashAccount_MatchTheReference()
    {
        BacktestResult result = Run(Cash(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 102m), Flat(3, 101m), Flat(4, 100m), Flat(5, 98m), Flat(6, 99m), Flat(7, 99m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [3] = Req(SignalType.ShortEntry) },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit), [5] = Req(SignalType.ShortExit) });

        Assert.Equal(2, result.Trades.Length);
        AssertMatchesReference("cash round trips", result);
    }

    [Fact]
    public void PositionStillOpenAtTheLastBar_IsCountedInTheMarket_AndMatchesTheReference()
    {
        BacktestResult result = Run(Cash(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 101m), Flat(3, 103m), Flat(4, 102m)),
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongEntry) });

        Assert.Empty(result.Trades);                                   // the engine does not force-close at the end
        Assert.NotEqual(0m, result.EquityPoints[^1].MarketValue);
        BacktestReport report = Generate(result);
        Assert.True(report.TimeInMarket!.Value.Value > 0m);
        AssertMatchesReference("open at the end", result);
    }

    [Fact]
    public void ForcedLiquidationOnTheEntryBar_IsCountedAsOneInMarketBar_AndMatchesTheReference()
    {
        BacktestResult result = Run(Margin(),
            ImmutableArray.Create(Flat(0, 100m), Bar(1, 100m, 100m, 60m, 100m), Flat(2, 100m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry) });

        BacktestTrade trade = Assert.Single(result.Trades);
        Assert.Equal(0, trade.HoldingBars);
        BacktestReport report = Generate(result);
        Assert.Equal(1m, report.TimeInMarket!.Value.Value);
        AssertMatchesReference("liquidation on the entry bar", result);
    }

    [Fact]
    public void ReversalPairThenLiquidation_OnTheSameBar_MatchesTheReference()
    {
        BacktestResult result = Run(Margin(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Bar(2, 100m, 130m, 100m, 120m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.LongEntry), [1] = Req(SignalType.ShortEntry) },
            new Dictionary<int, StrategyOrderRequest> { [1] = Req(SignalType.LongExit) });

        Assert.Equal(2, result.Trades.Length);
        Assert.Equal(result.Trades[0].ExitBar, result.Trades[1].ExitBar);
        AssertMatchesReference("reversal + liquidation", result);
    }

    [Fact]
    public void ShortOnAMarginAccount_HeldAcrossSeveralBars_MatchesTheReference()
    {
        BacktestResult result = Run(Margin(),
            ImmutableArray.Create(Flat(0, 100m), Flat(1, 100m), Flat(2, 98m), Flat(3, 96m), Flat(4, 97m), Flat(5, 97m)),
            new Dictionary<int, StrategyOrderRequest> { [0] = Req(SignalType.ShortEntry) },
            new Dictionary<int, StrategyOrderRequest> { [3] = Req(SignalType.ShortExit) });

        Assert.Single(result.Trades);
        Assert.Equal(TradeSide.Short, result.Trades[0].Side);
        AssertMatchesReference("margin short", result);
    }

    // ---- Seeded random engine runs (the fuzz corpus of the ledger tests: all order types, liquidations, reversals) ----------------------------

    [Fact]
    public void SeededRandomEngineRuns_MatchTheReference()
    {
        BacktestConfiguration[] configs =
        {
            VerificationHarness.MakeConfig(10_000m, sizingParameter: 10m),
            VerificationHarness.MakeConfig(10_000m, commissionFlat: 1m, slippageRatio: 0.002m, sizingParameter: 20m, initialMarginRatio: 0.5m, maintenanceMarginRatio: 0.25m),
            VerificationHarness.MakeConfig(10_000m, slippageRatio: 0.001m, sizingParameter: 400m, initialMarginRatio: 0.1m, maintenanceMarginRatio: 0.05m, liquidationPenaltyRatio: 0.01m),
            // 25x leverage: a bar's own range is often enough to liquidate a position on the bar that opened it.
            VerificationHarness.MakeConfig(10_000m, sizingParameter: 1_000m, initialMarginRatio: 0.04m, maintenanceMarginRatio: 0.03m),
        };
        int runs = 0;
        int sameBarTrades = 0;
        int withTrades = 0;

        for (int seed = 1; seed <= VerificationParameters.FuzzSeedCount; seed++)
        {
            ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(seed, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
            BacktestInput input = VerificationHarness.MakeInput(bars);
            for (int c = 0; c < configs.Length; c++)
            {
                BacktestResult result = VerificationHarness.Run(input, configs[c], new SeededRandomStrategy(seed));
                AssertMatchesReference($"engine seed {seed} config {c}", result);
                runs++;
                if (result.Trades.Length > 0) withTrades++;
                sameBarTrades += result.Trades.Count(trade => trade.EntryBar == trade.ExitBar);
            }
        }

        // Guards the test itself: the corpus must actually contain trades and the same-bar case that motivated the exposure rule.
        Assert.Equal(VerificationParameters.FuzzSeedCount * configs.Length, runs);
        Assert.True(withTrades > runs / 2, $"only {withTrades} of {runs} runs produced trades");
        Assert.True(sameBarTrades > 0, "the corpus never produced a same-bar round trip");
    }

    // ---- Concurrency -------------------------------------------------------------------------------------------------------------------

    private const int ParallelIterations = 64;

    private static string IdentityOf(BacktestReport report, BacktestResult result, BacktestReportOptions options)
    {
        var metadata = new BacktestEvaluationMetadata(
            "CONC", 1, TimeFrame.D1, Bar0, Bar0.AddDays(400), Bar0.AddDays(result.EquityPoints.Length), 0, 0, result.EquityPoints.Length,
            252, 0m, 0m, options.BootstrapSeed, options.BootstrapIterations, BacktestReport.CurrentFormulaVersion,
            "concurrency-assembly", "concurrency-runtime", InputDataReference.Unavailable());
        var fingerprint = new BacktestReportRunFingerprint { IsAvailable = true, Sha256 = new string('C', 64), SchemaVersion = 1, ExecutionSemanticsVersion = 2 };
        var sampling = new SamplingQualification(SamplingStatus.Unverified, SamplingReason.NoEvidence, TimeFrame.D1, result.EquityPoints.Length, null, null);
        var bootstrap = BootstrapDiagnostics.NotComputed(BootstrapDiagnostics.LegacyEntryPointReason);
        (DrawdownEpisodeResult ratio, DrawdownEpisodeResult amount) = DrawdownEpisodeCalculator.Compute(result, options, CancellationToken.None);
        return BacktestReportIdentityEncoder.Compute(fingerprint, metadata, result.Status, sampling, bootstrap, report, ratio, amount).Sha256!;
    }

    [Fact]
    public void ConcurrentGenerationOfTheSameResult_GivesIdenticalReportsAndIdentityHashes()
    {
        ImmutableArray<CandleData> bars = SyntheticBars.SeededRandomWalk(7, VerificationParameters.FuzzBarCount, VerificationParameters.FuzzStartPrice);
        BacktestResult result = VerificationHarness.Run(
            VerificationHarness.MakeInput(bars),
            VerificationHarness.MakeConfig(10_000m, slippageRatio: 0.001m, sizingParameter: 400m, initialMarginRatio: 0.1m, maintenanceMarginRatio: 0.05m, liquidationPenaltyRatio: 0.01m),
            new SeededRandomStrategy(7));
        Assert.NotEmpty(result.Trades);

        BacktestReportOptions options = ReportTestHelpers.Options();
        var reports = new string[ParallelIterations];
        var hashes = new string[ParallelIterations];
        Parallel.For(0, ParallelIterations, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, index =>
        {
            BacktestReport report = new BacktestReportGenerator().Generate(result, options);
            reports[index] = JsonSerializer.Serialize(report);
            hashes[index] = IdentityOf(report, result, options);
        });

        // The code keeps no shared mutable state and takes no locks: this is the evidence, not a proof.
        Assert.Single(reports.Distinct());
        Assert.Single(hashes.Distinct());
        Assert.Equal(IdentityOf(new BacktestReportGenerator().Generate(result, options), result, options), hashes[0]);
    }
}
