using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The Core side of the portfolio worker protocol driven by a scripted client (the worker is replaced by explicit JSON frames). Calendar of the
/// fixture: 1278 bars, two symbols, short-history catalog: train blocks 0, 192, 384; one validation block 830..1022; one OOS block.
/// The nominal fixture trades symbol 0 at 100 and symbol 1 at 50, initial capital 10000, no costs, a model of three slots.
/// </summary>
public sealed class RlPortfolioIpcHostTests
{
    private const int Slots = 3;
    private static readonly Guid Run = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string Session1 = Guid.NewGuid().ToString("N"), Session2 = Guid.NewGuid().ToString("N"), Session3 = Guid.NewGuid().ToString("N");
    private static readonly FixedScaler Identity = RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(0d, 1d, -100d, 100d), 5).ToArray());

    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration
        { SizingModel = PositionSizingModel.FixedQuantity, SizingParameter = 1m, InitialCapital = 10_000m });

    private static RlPanelCatalog Catalog() => RlPanelCatalogBuilder.Build(1278,
        Enumerable.Range(0, 2).Select(_ => (ReadOnlyMemory<int>)Enumerable.Range(0, 1279).ToArray()).ToArray(),
        ImmutableArray.Create(false, false), RlPanelEvaluationMode.ShortHistory);

    private static RlPanelBarsFixture Bars(Func<int, int, (decimal, decimal)?>? prices = null)
        => new(2, prices ?? ((s, t) => s == 0 ? (100m, 100m) : (50m, 50m)));

    private static RlPortfolioIpcHost Host(RlPanelBarsFixture? bars = null, IProgress<RlTrainingProgress>? progress = null)
        => new(Run, bars ?? Bars(), Catalog(), Identity, Slots, Costs(), 100, progress);

    private long _sequence;
    private JsonElement Request(string method, object payload, string session = "", long? sequence = null, string? run = null, string? hash = null, int version = 1)
    {
        var frame = new Dictionary<string, object>
        {
            ["protocol_version"] = version, ["run_id"] = run ?? Run.ToString("N"), ["contract_hash"] = hash ?? RlPortfolioContract.ContractHash,
            ["sequence"] = sequence ?? ++_sequence, ["session_id"] = session, ["method"] = method, ["payload"] = payload,
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(frame)).RootElement.Clone();
    }

    private JsonElement Call(RlPortfolioIpcHost host, string method, object payload, string session = "")
        => JsonSerializer.SerializeToElement(host.Handle(Request(method, payload, session), default), RlFiles.Json);

    private static JsonElement Result(JsonElement response) => response.GetProperty("result");

    private object Reset(string role, string block, int seed = 42, int checkpoint = 512) => new { role, block_id = block, seed, checkpoint };

    private static float[] Zeros(float cash = 0f) => new[] { cash, 0f, 0f, 0f };

    [Fact]
    public void HandshakeSequenceAndEnvelopeAreEnforced()
    {
        var host = Host();
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Ready", new { }, sequence: 2), default));      // out of order
        _sequence = 0;
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Ready", new { }, run: Guid.NewGuid().ToString("N")), default));
        _sequence = 0;
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Ready", new { }, hash: RlContract.ContractHash), default));
        _sequence = 0;
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Ready", new { }, version: 2), default));
        _sequence = 0;
        var ready = host.Handle(Request("Ready", new { }), default);
        var json = JsonSerializer.SerializeToElement(ready, RlFiles.Json);
        Assert.True(Result(json).GetProperty("ready").GetBoolean());
        Assert.Equal(RlPortfolioContract.ContractHash, json.GetProperty("contract_hash").GetString());
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Ready", new { }), default));                    // duplicate readiness
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Nope", new { }), default));                      // unknown method
        Assert.Throws<InvalidDataException>(() => host.Handle(Request("Ready", new { }, sequence: 2), default));        // a sequence is never replayed
    }

    [Fact]
    public void ResetRequiresReadinessAndAValidRoleBlockAndIdentity()
    {
        var host = Host();
        Assert.Throws<InvalidDataException>(() => Call(host, "Reset", Reset("train", "train:0:192"), Session1)); // not ready
        Call(host, "Ready", new { });
        Assert.Throws<InvalidDataException>(() => Call(host, "Reset", Reset("oos", "oos:1086:1278"), Session1));  // unsupported role in PF3b
        Assert.Throws<InvalidDataException>(() => Call(host, "Reset", Reset("train", "validation:830:1022"), Session1)); // other partition
        Assert.Throws<InvalidDataException>(() => Call(host, "Reset", Reset("train", "train:0:192"), "not-a-guid"));
        var reset = Result(Call(host, "Reset", Reset("train", "train:0:192"), Session1));
        Assert.Equal("train:0:192", reset.GetProperty("info").GetProperty("block_id").GetString());
        Assert.Equal(128, reset.GetProperty("info").GetProperty("remaining_decisions").GetInt32());
    }

    [Fact]
    public void ResetReturnsTheObservationOfTheBuilder()
    {
        var bars = Bars();
        var host = Host(bars);
        Call(host, "Ready", new { });
        var observation = Result(Call(host, "Reset", Reset("train", "train:0:192"), Session1)).GetProperty("observation")
            .EnumerateArray().Select(v => v.GetSingle()).ToArray();
        Assert.Equal(970, observation.Length);
        var session = new RlPortfolioSession(bars, RlPanelBarsFixture.Block(0, 1), ImmutableArray.Create(0, 1), Costs());
        Assert.Equal(RlPortfolioObservationBuilder.Build(bars, Slots, session.Reset(), ImmutableArray.Create(0, 1), Identity), observation);
    }

    [Fact]
    public void AStepMasksQuantizesAndStepsTheAccountantWithTheHandLedger()
    {
        // Prices: symbol 0 opens 100 and closes 110 at index 64, otherwise flat; symbol 1 flat at 50. Equal logits over cash and the two
        // tradable slots (the third slot is padding and masked): p = 1/3 -> weights 0.3333 each. Targets: 33 shares at 100, 66 shares at 50;
        // cost 3300 + 3300, cash 3400; E = 3400 + 33 * 110 + 66 * 50 = 10330; reward 330 / 10000 = 0.033.
        var host = Host(Bars((s, t) => s == 0 ? (t == 64 ? (100m, 110m) : (100m, 100m)) : (50m, 50m)));
        Call(host, "Ready", new { });
        Call(host, "Reset", Reset("train", "train:0:192"), Session1);
        var step = Result(Call(host, "Step", new { actions = Zeros() }, Session1));
        Assert.Equal((float)0.033m, step.GetProperty("reward").GetSingle());
        Assert.False(step.GetProperty("terminated").GetBoolean());
        Assert.False(step.GetProperty("truncated").GetBoolean());
        var info = step.GetProperty("info");
        Assert.Equal("10330", info.GetProperty("equity").GetString());
        Assert.Equal((3400m / 10330m).ToString(System.Globalization.CultureInfo.InvariantCulture), info.GetProperty("cash_weight").GetString());
        Assert.Equal("0.033", info.GetProperty("decimal_reward").GetString());
        Assert.Equal(2, info.GetProperty("order_count").GetInt32());
        Assert.Equal(970, step.GetProperty("observation").GetArrayLength());
        Assert.Equal(1, host.TrainingTransitionsBySeed.Sum(p => p.TrainingTransitions));
    }

    [Fact]
    public void StepRejectsMalformedActionsAndUnknownSessions()
    {
        var host = Host();
        Call(host, "Ready", new { });
        Call(host, "Reset", Reset("train", "train:0:192"), Session1);
        Assert.Throws<InvalidDataException>(() => Call(host, "Step", new { actions = new[] { 0f, 0f, 0f } }, Session1));        // too short
        Assert.Throws<InvalidDataException>(() => Call(host, "Step", new { actions = new[] { 0f, 0f, 0f, 0f, 0f } }, Session1)); // too long
        Assert.Throws<InvalidDataException>(() => Call(host, "Step", new { actions = new object[] { 0, 0, "x", 0 } }, Session1));
        Assert.Throws<InvalidDataException>(() => Call(host, "Step", new { actions = new[] { 0d, 1e60, 0d, 0d } }, Session1));  // not a finite float32
        Assert.Throws<InvalidDataException>(() => Call(host, "Step", new { actions = Zeros() }, Session2));                    // unknown session
        Assert.Equal(0, host.TrainingTransitionsBySeed.Sum(p => p.TrainingTransitions));
    }

    [Fact]
    public void AnEpisodeEndsAfterTheHorizonAndReportsTheTerminalReturn()
    {
        var host = Host(Bars((s, t) => (100m + t * 0.1m * (s + 1), 100m + t * 0.1m * (s + 1))));
        Call(host, "Ready", new { });
        Call(host, "Reset", Reset("train", "train:0:192"), Session1);
        JsonElement last = default;
        for (int k = 0; k < 128; k++) last = Result(Call(host, "Step", new { actions = Zeros(20f) }, Session1)); // all cash
        Assert.True(last.GetProperty("terminated").GetBoolean());
        Assert.Equal(0m, decimal.Parse(last.GetProperty("info").GetProperty("terminal_return").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(128, host.TrainingTransitionsBySeed.Sum(p => p.TrainingTransitions));
        Assert.Throws<InvalidOperationException>(() => Call(host, "Step", new { actions = Zeros() }, Session1));
    }

    [Fact]
    public void SelectionFollowsTheExactCoreValidationMeanThenCheckpointThenSeed()
    {
        // Prices rise, so a fully invested candidate beats an all-cash candidate; ties go to the smaller checkpoint, then the smaller seed.
        var host = Host(Bars((s, t) => (100m + t * 0.1m * (s + 1), 100m + t * 0.1m * (s + 1))));
        Call(host, "Ready", new { });
        Assert.Throws<InvalidDataException>(() => Call(host, "Select", new { seed = 42, checkpoint = 512 }));   // nothing validated yet
        string Play(string session, int seed, int checkpoint, float cash)
        {
            Call(host, "Reset", Reset("validation", "validation:830:1022", seed, checkpoint), session);
            JsonElement last = default;
            for (int k = 0; k < 128; k++) last = Result(Call(host, "Step", new { actions = Zeros(cash) }, session));
            Call(host, "Dispose", new { }, session);
            return last.GetProperty("info").GetProperty("mean_return").GetString()!;
        }
        Assert.Equal(0m, decimal.Parse(Play(Session1, 42, 512, 40f), System.Globalization.CultureInfo.InvariantCulture)); // all cash
        decimal invested = decimal.Parse(Play(Session2, 43, 512, -40f), System.Globalization.CultureInfo.InvariantCulture); // all assets
        Assert.True(invested > 0m);
        Assert.Equal(0m, decimal.Parse(Play(Session3, 44, 1024, 40f), System.Globalization.CultureInfo.InvariantCulture)); // all cash again
        Assert.Throws<InvalidDataException>(() => Call(host, "Select", new { seed = 42, checkpoint = 512 }));
        Assert.Throws<InvalidDataException>(() => Call(host, "Select", new { seed = 44, checkpoint = 1024 }));
        Assert.True(Result(Call(host, "Select", new { seed = 43, checkpoint = 512 })).GetProperty("selected").GetBoolean());
        Assert.Equal((43, 512), host.Selected!.Value);
        Assert.Throws<InvalidDataException>(() => Call(host, "Select", new { seed = 43, checkpoint = 512 }));    // never twice
    }

    [Fact]
    public void CandidatesAreListedInTheSelectionOrderWithTheirExactMeansAndBlockReturns()
    {
        var host = Host(Bars((s, t) => (100m + t * 0.1m * (s + 1), 100m + t * 0.1m * (s + 1))));
        Call(host, "Ready", new { });
        Assert.Empty(host.Candidates);
        decimal Play(string session, int seed, int checkpoint, float cash)
        {
            Call(host, "Reset", Reset("validation", "validation:830:1022", seed, checkpoint), session);
            JsonElement last = default;
            for (int k = 0; k < 128; k++) last = Result(Call(host, "Step", new { actions = Zeros(cash) }, session));
            Call(host, "Dispose", new { }, session);
            return decimal.Parse(last.GetProperty("info").GetProperty("mean_return").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        }
        Play(Session1, 44, 1024, 40f);                         // all cash: 0
        decimal invested = Play(Session2, 43, 512, -40f);      // all assets: the highest mean
        Play(Session3, 42, 1024, 40f);                         // all cash: 0, same checkpoint as the first, smaller seed
        var candidates = host.Candidates;
        Assert.Equal(new[] { (43, 512), (42, 1024), (44, 1024) }, candidates.Select(c => (c.Seed, c.Checkpoint)).ToArray());
        Assert.Equal(new[] { invested, 0m, 0m }, candidates.Select(c => c.MeanReturn).ToArray());
        var block = Assert.Single(candidates[0].BlockReturns);
        Assert.Equal(("validation:830:1022", invested), (block.BlockId, block.Return));
        Assert.True(Result(Call(host, "Select", new { seed = 43, checkpoint = 512 })).GetProperty("selected").GetBoolean());
        Assert.Equal((43, 512), host.Selected!.Value);
        Assert.Empty(host.TrainingTransitionsBySeed);          // validation steps are not training transitions
    }

    [Fact]
    public void TrainingTransitionsAreCountedPerSeedInSeedOrder()
    {
        var host = Host();
        Call(host, "Ready", new { });
        void Train(string session, int seed, int steps)
        {
            Call(host, "Reset", Reset("train", "train:0:192", seed), session);
            for (int k = 0; k < steps; k++) Call(host, "Step", new { actions = Zeros() }, session);
            Call(host, "Dispose", new { }, session);
        }
        Train(Session1, 43, 3);
        Train(Session2, 42, 5);
        Assert.Equal(new[] { (42, 5), (43, 3) }, host.TrainingTransitionsBySeed.Select(p => (p.Seed, p.TrainingTransitions)).ToArray());
        Assert.Equal(8, host.TrainingTransitionsBySeed.Sum(p => p.TrainingTransitions));
    }

    [Fact]
    public void EqualMeansPreferTheSmallerCheckpointThenTheSmallerSeed()
    {
        var host = Host();
        Call(host, "Ready", new { });
        void Play(string session, int seed, int checkpoint)
        {
            Call(host, "Reset", Reset("validation", "validation:830:1022", seed, checkpoint), session);
            for (int k = 0; k < 128; k++) Call(host, "Step", new { actions = Zeros(40f) }, session);
            Call(host, "Dispose", new { }, session);
        }
        Play(Session1, 45, 1024);
        Play(Session2, 44, 512);
        Play(Session3, 43, 512);
        Assert.Throws<InvalidDataException>(() => Call(host, "Select", new { seed = 44, checkpoint = 512 }));    // the smaller seed wins at checkpoint 512
        Assert.True(Result(Call(host, "Select", new { seed = 43, checkpoint = 512 })).GetProperty("selected").GetBoolean());
    }

    [Fact]
    public void AValidationEpisodeCannotBeReportedTwiceAndTrainingNeverCountsAsValidation()
    {
        var host = Host();
        Call(host, "Ready", new { });
        void Play(string role, string block, string session)
        {
            Call(host, "Reset", Reset(role, block), session);
            for (int k = 0; k < 128; k++) Call(host, "Step", new { actions = Zeros() }, session);
            Call(host, "Dispose", new { }, session);
        }
        Play("train", "train:0:192", Session1);
        Assert.Throws<InvalidDataException>(() => Call(host, "Select", new { seed = 42, checkpoint = 512 }));
        Play("validation", "validation:830:1022", Session2);
        Call(host, "Reset", Reset("validation", "validation:830:1022"), Session3);
        for (int k = 0; k < 127; k++) Call(host, "Step", new { actions = Zeros() }, Session3);
        Assert.Throws<InvalidDataException>(() => Call(host, "Step", new { actions = Zeros() }, Session3));          // duplicate report of the same block
    }

    [Fact]
    public void SessionsAreLimitedAndDisposedIdentitiesAreNeverReused()
    {
        var host = Host();
        Call(host, "Ready", new { });
        Call(host, "Reset", Reset("train", "train:0:192"), Session1);
        Call(host, "Reset", Reset("validation", "validation:830:1022"), Session2);
        Assert.Throws<InvalidDataException>(() => Call(host, "Reset", Reset("train", "train:192:384"), Session3));    // third concurrent session
        Call(host, "Dispose", new { }, Session1);
        Assert.Throws<InvalidDataException>(() => Call(host, "Dispose", new { }, Session1));                        // already disposed
        Assert.Throws<InvalidDataException>(() => Call(host, "Reset", Reset("train", "train:0:192"), Session1));     // identity reuse
        Call(host, "Reset", Reset("train", "train:192:384"), Session3);
        host.Dispose();
    }

    [Fact]
    public void ProgressIsForwardedToTheListener()
    {
        var reports = new List<RlTrainingProgress>();
        var host = Host(progress: new SyncProgress(reports));
        Call(host, "Ready", new { });
        Call(host, "Progress", new { message = "seed 42", steps = 512 });
        var report = Assert.Single(reports);
        Assert.Equal(("seed 42", 512, Run), (report.Message, report.CompletedSteps, report.RunId));
    }

    [Fact]
    public void TheWorstCaseObservationFrameAtTheHardMaximumFitsTheFrameBound()
    {
        var limits = RlTrainingValidation.FreezeLimits(continuous: true);
        int hard = RlPortfolioResources.MaximumSymbols(limits);
        long length = RlPortfolioResources.ObservationLength(hard);
        Assert.True(RlPortfolioResources.ObservationFrameBytes(hard) < RlContract.IpcFrameByteLimit);
        // Real serialization of awkward float32 values (long mantissas, negative exponents) stays below the same bound.
        var random = new Random(3);
        var values = Enumerable.Range(0, (int)length).Select(_ => (float)((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-38, -20)))).ToArray();
        string text = JsonSerializer.Serialize(new { observation = values }, RlFiles.Json);
        Assert.True(text.Length + 4096 < RlContract.IpcFrameByteLimit, $"frame {text.Length}");
    }

    [Fact]
    public void ConstructionChecksItsArguments()
    {
        var catalog = Catalog();
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioIpcHost(Run, null!, catalog, Identity, Slots, Costs(), 100));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioIpcHost(Run, Bars(), null!, Identity, Slots, Costs(), 100));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioIpcHost(Run, Bars(), catalog, null!, Slots, Costs(), 100));
        Assert.Throws<ArgumentNullException>(() => new RlPortfolioIpcHost(Run, Bars(), catalog, Identity, Slots, null!, 100));
        Assert.Throws<ArgumentException>(() => new RlPortfolioIpcHost(Run, Bars(), catalog, Identity, 1, Costs(), 100));
    }

    private sealed class SyncProgress : IProgress<RlTrainingProgress>
    {
        private readonly List<RlTrainingProgress> _reports;
        public SyncProgress(List<RlTrainingProgress> reports) => _reports = reports;
        public void Report(RlTrainingProgress value) => _reports.Add(value);
    }
}
