#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlShortHistoryEvaluationTests
{
    private static ImmutableArray<CandleData> Bars(int count) => Enumerable.Range(0, count)
        .Select(i => new CandleData(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), 100m, 101m, 99m, 100m, 1000L)).ToImmutableArray();
    private static RlEpisodeCatalog ShortHistory(int count, bool smoke = false)
        => RlSourceSnapshot.BuildShortHistoryCatalog(Bars(count), ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty, smoke);
    private static RlEpisodeCatalog Standard(int count)
        => RlSourceSnapshot.BuildCatalog(Bars(count), ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty, false);
    private static RlEpisodeCatalog AllBlocks(int count)
        => RlSourceSnapshot.BuildCatalog(Bars(count), ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty, false,
            RlAllBlockEvaluationContract.SpecificationId);
    private static BacktestConfiguration Costs() => RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1000000m,
        SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });

    [Fact]
    public void CatalogBoundaryHasIndependentHandAnswers()
    {
        // T=1277: cut1=766, cut2=1021; the validation window 830..1021 holds 191 bars, one short of a 192-bar block.
        var error = Assert.Throws<InvalidDataException>(() => ShortHistory(1277));
        Assert.True(RlSourceContract.IsInsufficientData(error));
        // T=1278: cut1=766, cut2=1022; blocks start at 0,192,384 (train), 830 (validation), 1086 (out of sample).
        var catalog = ShortHistory(1278);
        Assert.Equal(new[] { 0, 192, 384 }, catalog.Train.Select(b => b.StartIndex));
        var validation = Assert.Single(catalog.Validation); var oos = Assert.Single(catalog.Oos);
        Assert.Equal((830, 1022), (validation.StartIndex, validation.EndExclusive));
        Assert.Equal((1086, 1278), (oos.StartIndex, oos.EndExclusive));
        Assert.Equal((766, 1022), (catalog.TrainCut, catalog.ValidationCut));
    }

    [Fact]
    public void StandardCatalogStillNeedsFiveBlocksPerPartition()
    {
        Assert.Throws<InvalidDataException>(() => Standard(5117));
        var catalog = Standard(5118);
        Assert.Equal(5, catalog.Validation.Length); Assert.Equal(5, catalog.Oos.Length);
        Assert.Throws<InvalidDataException>(() => Standard(1278));
    }

    [Fact]
    public void SmokeStillNeedsTheSameMinimumAndKeepsNoOos()
    {
        Assert.Throws<InvalidDataException>(() => ShortHistory(1277, smoke: true));
        var catalog = ShortHistory(1278, smoke: true);
        Assert.Single(catalog.Validation); Assert.Empty(catalog.Oos);
    }

    [Theory] [InlineData(6218)] [InlineData(9246)]
    public void CatalogKeepsEveryCompleteBlockLikeTheAllBlockRuleButCarriesOnlyItsOwnId(int count)
    {
        var shortHistory = ShortHistory(count); var all = AllBlocks(count); var standard = Standard(count);
        Assert.Equal(all.Train, shortHistory.Train); Assert.Equal(all.Validation, shortHistory.Validation); Assert.Equal(all.Oos, shortHistory.Oos);
        Assert.Equal(all.Excluded, shortHistory.Excluded);
        Assert.True(shortHistory.Validation.Length > standard.Validation.Length);
        Assert.Equal(RlShortHistoryEvaluationContract.SpecificationId, shortHistory.ShortHistorySpecificationId);
        Assert.Null(shortHistory.EvaluationSpecificationId); Assert.Null(shortHistory.SchemaVersion);
        string saved = JsonSerializer.Serialize(shortHistory, RlFiles.Json);
        Assert.Contains("short_history_specification_id", saved);
        Assert.DoesNotContain("evaluation_specification_id", saved); Assert.DoesNotContain("schema_version", saved);
        Assert.DoesNotContain("short_history", JsonSerializer.Serialize(standard, RlFiles.Json));
        Assert.DoesNotContain("short_history", JsonSerializer.Serialize(all, RlFiles.Json));
    }

    [Fact]
    public void KnownExclusionsAreKeptWithoutMakingUnknownHistoryComplete()
    {
        var bars = Bars(6218); var normal = ShortHistory(6218);
        var first = DateOnly.FromDateTime(bars[normal.Validation[0].StartIndex].Timestamp);
        var catalog = RlSourceSnapshot.BuildShortHistoryCatalog(bars, ImmutableArray<RlCorporateAction>.Empty, ImmutableArray.Create(first), false);
        Assert.Equal(normal.Validation.Length - 1, catalog.Validation.Length);
        Assert.Contains(catalog.Excluded, e => e.EndsWith(":KnownImputedBar", StringComparison.Ordinal));
    }

    [Fact]
    public void ContractIdsAreRecognisedAndEverythingElseIsRejected()
    {
        Assert.False(RlShortHistoryEvaluationContract.IsShortHistory(null));
        Assert.True(RlShortHistoryEvaluationContract.IsShortHistory(RlShortHistoryEvaluationContract.SpecificationId));
        Assert.Throws<InvalidDataException>(() => RlShortHistoryEvaluationContract.IsShortHistory("SA-RL-SHORT-HISTORY-v2"));
        Assert.Throws<InvalidDataException>(() => RlShortHistoryEvaluationContract.IsShortHistory(RlAllBlockEvaluationContract.SpecificationId));
        // The all-block reader must not accept the short-history id: the two contracts stay independent.
        Assert.Throws<InvalidDataException>(() => RlAllBlockEvaluationContract.IsExpanded(RlShortHistoryEvaluationContract.SpecificationId));
        Assert.NotEqual(RlAllBlockEvaluationContract.MetadataKey, RlShortHistoryEvaluationContract.MetadataKey);
        foreach (string json in new[] { "{\"short_history_specification_id\":null}", "{\"short_history_specification_id\":\"future\"}" })
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<InvalidDataException>(() => RlShortHistoryEvaluationContract.Read(document.RootElement));
        }
        using var absent = JsonDocument.Parse("{}");
        Assert.Null(RlShortHistoryEvaluationContract.Read(absent.RootElement));
        using var present = JsonDocument.Parse("{\"short_history_specification_id\":\"SA-RL-SHORT-HISTORY-v1\"}");
        Assert.Equal(RlShortHistoryEvaluationContract.SpecificationId, RlShortHistoryEvaluationContract.Read(present.RootElement));
        Assert.Matches("^[0-9A-F]{64}$", RlShortHistoryEvaluationContract.ContractHash);
        Assert.NotEqual(RlContract.ContractHash, RlShortHistoryEvaluationContract.ContractHash);
    }

    [Fact]
    public void FreezeAcceptsOnlyTheDiscreteTargetWithoutTheAllBlockId()
    {
        var job = new RlTrainingJob("AAPL", new(2002, 1, 1), new(2026, 10, 4), Costs());
        Assert.Null(RlTrainingValidation.Freeze(job).ShortHistorySpecificationId);
        var enabled = job with { ShortHistorySpecificationId = RlShortHistoryEvaluationContract.SpecificationId };
        Assert.Equal(RlShortHistoryEvaluationContract.SpecificationId, RlTrainingValidation.Freeze(enabled).ShortHistorySpecificationId);
        Assert.Throws<ArgumentException>(() => RlTrainingValidation.Freeze(enabled with { FrozenDirectionModelId = new string('a', 64) }));
        Assert.Throws<ArgumentException>(() => RlTrainingValidation.Freeze(enabled with
        { EvaluationSpecificationId = RlAllBlockEvaluationContract.SpecificationId, FrozenDirectionModelId = new string('a', 64) }));
        Assert.Throws<ArgumentException>(() => RlTrainingValidation.Freeze(enabled with { EvaluationSpecificationId = RlAllBlockEvaluationContract.SpecificationId }));
        Assert.Throws<InvalidDataException>(() => RlTrainingValidation.Freeze(job with { ShortHistorySpecificationId = "other" }));
        // The default job serialisation never acquires the new field.
        Assert.DoesNotContain("short_history", JsonSerializer.Serialize(job, RlFiles.Json));
    }

    [Fact]
    public void EstimateCountsEveryTransitionAndEpisodeStartWithIndependentHandAnswers()
    {
        // 3 seeds x 10000 training; candidates 3 x 2 checkpoints x 1 block = 6 episodes x 128; baselines (SMA, hold, buy-hold, 3 random seeds) = 6 runs x (1+1) blocks = 12 x 128; OOS 1 x 128.
        var one = RlShortHistoryEvaluation.Estimate(1, 1, 1000, false);
        Assert.Equal(30000, one.TrainingTransitions); Assert.Equal(768, one.CandidateValidationTransitions);
        Assert.Equal(1536, one.BaselineTransitions); Assert.Equal(128, one.SelectedOosTransitions);
        Assert.Equal(0, one.NativeParityTransitions); Assert.Equal(0, one.CoreQualificationTransitions);
        Assert.Equal(32432, one.MaximumTransitions); Assert.Equal(1000, one.SourceBytes);
        // Starts: 3 x (10000/128 + 1) = 237, + 6 candidate + 12 baseline + 1 OOS.
        Assert.Equal(256, one.NominalEpisodeStarts);
        // Smoke: 1 seed x 1024 steps, checkpoints at 512 and 1024 => 2 episodes; baselines 6 runs x 1 block; no OOS.
        var smoke = RlShortHistoryEvaluation.Estimate(1, 0, 0, true);
        Assert.Equal(1024, smoke.TrainingTransitions); Assert.Equal(256, smoke.CandidateValidationTransitions);
        Assert.Equal(768, smoke.BaselineTransitions); Assert.Equal(0, smoke.SelectedOosTransitions);
        Assert.Equal(2048, smoke.MaximumTransitions); Assert.Equal(17, smoke.NominalEpisodeStarts);
        var nine = RlShortHistoryEvaluation.Estimate(9, 9, 0, false);
        Assert.Equal(51888, nine.MaximumTransitions); Assert.Equal(408, nine.NominalEpisodeStarts);
        Assert.Throws<ArgumentOutOfRangeException>(() => RlShortHistoryEvaluation.Estimate(0, 1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlShortHistoryEvaluation.Estimate(1, -1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => RlShortHistoryEvaluation.Estimate(1, 1, -1, false));
    }

    [Fact]
    public void WholeJobCapacityRejectsAnOverlongRequestInsteadOfDroppingBlocks()
    {
        var limits = RlTrainingValidation.FreezeLimits();
        RlAllBlockEvaluation.RequireCapacity(RlShortHistoryEvaluation.Estimate(9, 9, 1000, false), limits);
        // 60 + 60 blocks: 360 candidate episodes + 720 baseline episodes exceed the 100000-transition whole-job bound.
        Assert.Throws<InvalidDataException>(() => RlAllBlockEvaluation.RequireCapacity(RlShortHistoryEvaluation.Estimate(60, 60, 1000, false), limits));
    }
}
