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

public sealed class RlAllBlockEvaluationTests
{
    private static ImmutableArray<CandleData> Bars(int count) => Enumerable.Range(0, count)
        .Select(i => new CandleData(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i), 100m, 101m, 99m, 100m, 1000L)).ToImmutableArray();
    private static RlEpisodeCatalog Catalog(int count, bool expanded, bool smoke = false)
        => RlSourceSnapshot.BuildCatalog(Bars(count), ImmutableArray<RlCorporateAction>.Empty, ImmutableArray<DateOnly>.Empty,
            smoke, expanded ? RlAllBlockEvaluationContract.SpecificationId : null);

    [Theory] [InlineData(9246, 9)] [InlineData(6218, 6)] [InlineData(5316, 5)]
    public void ExpandedSelectsEveryCompletePartitionBlockWhileLegacyKeepsFive(int count, int expected)
    {
        var expanded = Catalog(count, true); var legacy = Catalog(count, false);
        Assert.Equal(expected, expanded.Validation.Length); Assert.Equal(expected, expanded.Oos.Length);
        Assert.Equal(5, legacy.Validation.Length); Assert.Equal(5, legacy.Oos.Length);
        Assert.Equal(legacy.Train, expanded.Train); Assert.Empty(expanded.Excluded);
        Assert.Equal(RlAllBlockEvaluationContract.SpecificationId, expanded.EvaluationSpecificationId);
        Assert.Equal(2, expanded.SchemaVersion); Assert.Null(legacy.EvaluationSpecificationId); Assert.Null(legacy.SchemaVersion);
        foreach (var partition in new[] { expanded.Validation, expanded.Oos })
        {
            int end = partition[0].Partition == "validation" ? expanded.ValidationCut : count;
            Assert.True(partition[^1].EndExclusive <= end);
            Assert.True(end - partition[^1].EndExclusive < 192);
            Assert.All(partition, b => Assert.Equal(192, b.EndExclusive - b.StartIndex));
            Assert.Equal(partition.Length, partition.Select(b => b.Id).Distinct().Count());
        }
        Assert.Equal(expanded.TrainCut + 64, expanded.Validation[0].StartIndex);
        Assert.Equal(expanded.ValidationCut + 64, expanded.Oos[0].StartIndex);
    }
    [Fact] public void ExpandedSmokePreservesAllValidationWithoutInventingOosEvidence()
    {
        var catalog = Catalog(6218, true, true); Assert.Equal(6, catalog.Validation.Length); Assert.Empty(catalog.Oos);
        Assert.Single(Catalog(6218, false, true).Validation);
    }
    [Fact] public void CompleteSupportIsRequiredAndNoPartialBlockIsAdded()
    {
        Assert.Throws<InvalidDataException>(() => Catalog(1277, true));
        var catalog = Catalog(1278, true); Assert.Single(catalog.Validation); Assert.Single(catalog.Oos);
        Assert.Throws<InvalidDataException>(() => Catalog(1280, false));
    }
    [Fact] public void KnownExclusionsAreKeptWithoutMakingUnknownHistoryComplete()
    {
        var bars = Bars(6218); var normal = Catalog(6218, true);
        var first = DateOnly.FromDateTime(bars[normal.Validation[0].StartIndex].Timestamp);
        var catalog = RlSourceSnapshot.BuildCatalog(bars, ImmutableArray<RlCorporateAction>.Empty,
            ImmutableArray.Create(first), false, RlAllBlockEvaluationContract.SpecificationId);
        Assert.Equal(5, catalog.Validation.Length); Assert.Equal(6, catalog.Oos.Length);
        Assert.Single(catalog.Excluded); Assert.EndsWith(":KnownImputedBar", catalog.Excluded[0]);
        Assert.Equal(normal.Validation[1].Id, catalog.Validation[0].Id);
    }
    [Fact] public void DecimalDistributionHasIndependentEvenAndOddHandAnswers()
    {
        Assert.Equal(new RlReturnDistribution(4, .075m, .05m, -.1m, .3m, .5m),
            RlAllBlockEvaluation.Distribution(new[] { .3m, 0m, -.1m, .1m }));
        Assert.Equal(new RlReturnDistribution(3, 0m, 0m, -.1m, .1m, 1m / 3m),
            RlAllBlockEvaluation.Distribution(new[] { .1m, -.1m, 0m }));
        Assert.Equal(0m, RlAllBlockEvaluation.Distribution(new[] { 0m, 0m }).PositiveRate);
        Assert.Throws<InvalidDataException>(() => RlAllBlockEvaluation.Distribution(Array.Empty<decimal>()));
        Assert.Throws<OverflowException>(() => RlAllBlockEvaluation.Distribution(new[] { decimal.MaxValue, decimal.MaxValue }));
    }
    [Fact] public void CheckedWholeJobEstimateIncludesEveryFinancialWorkOwner()
    {
        var full = RlAllBlockEvaluation.Estimate(6, 6, 6218, 1000, false);
        Assert.Equal(30720, full.TrainingTransitions); Assert.Equal(4608, full.CandidateValidationTransitions);
        Assert.Equal(10752, full.BaselineTransitions); Assert.Equal(768, full.SelectedOosTransitions);
        Assert.Equal(768, full.NativeParityTransitions); Assert.Equal(4608, full.CoreQualificationTransitions);
        Assert.Equal(52224, full.MaximumTransitions); Assert.Equal(411, full.NominalEpisodeStarts);
        var smoke = RlAllBlockEvaluation.Estimate(6, 0, 6218, 1000, true);
        Assert.Equal(11008, smoke.MaximumTransitions); Assert.Equal(87, smoke.NominalEpisodeStarts);
        Assert.True(full.SourceBytes > 2000);
        RlAllBlockEvaluation.RequireCapacity(full, RlTrainingValidation.FreezeLimits(continuous: true));
        Assert.Throws<InvalidDataException>(() => RlAllBlockEvaluation.RequireCapacity(
            RlAllBlockEvaluation.Estimate(100, 100, 100000, 1000, false), RlTrainingValidation.FreezeLimits(continuous: true)));
        Assert.Throws<InvalidDataException>(() => RlAllBlockEvaluation.RequireCapacity(
            full with { SourceBytes = long.MaxValue }, RlTrainingValidation.FreezeLimits(continuous: true)));
        Assert.Throws<OverflowException>(() => RlAllBlockEvaluation.Estimate(6, 6, 6218, long.MaxValue, false));
    }
    [Theory] [InlineData("{\"evaluation_specification_id\":null}")] [InlineData("{\"evaluation_specification_id\":\"future\"}")]
    public void UnsupportedOrExplicitNullContractIsRejected(string json)
    { using var document = JsonDocument.Parse(json); Assert.Throws<InvalidDataException>(() => RlAllBlockEvaluationContract.Read(document.RootElement)); }
    [Fact] public void LegacySerializationAndDefaultJobDoNotAcquireExpandedContract()
    {
        string saved = JsonSerializer.Serialize(Catalog(6218, false), RlFiles.Json);
        Assert.DoesNotContain("evaluation_specification_id", saved); Assert.DoesNotContain("schema_version", saved);
        var costs = RlContract.CreateConfiguration(new BacktestConfiguration { InitialCapital = 1000000m,
            SizingModel = BacktestDefaults.SizingModel, SizingParameter = BacktestDefaults.SizingParameter });
        var job = new RlTrainingJob("AAPL", new(2002, 1, 1), new(2026, 10, 4), costs);
        Assert.Null(RlTrainingValidation.Freeze(job).EvaluationSpecificationId);
        Assert.Throws<ArgumentException>(() => RlTrainingValidation.Freeze(job with { EvaluationSpecificationId = RlAllBlockEvaluationContract.SpecificationId }));
    }
}
