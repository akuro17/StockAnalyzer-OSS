using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The selection evidence file of a portfolio run: built only from a selection that follows the documented order, saved exactly.</summary>
public sealed class RlPortfolioTrainingSummaryTests
{
    private static readonly Guid Run = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static RlPortfolioCandidate Candidate(int seed, int checkpoint, decimal mean)
        => new(seed, checkpoint, mean, ImmutableArray.Create(new RlPortfolioBlockReturn("validation:830:1022", mean)));

    private static ImmutableArray<RlPortfolioCandidate> Ordered() => ImmutableArray.Create(
        Candidate(43, 512, 0.0125m), Candidate(42, 512, 0.01m), Candidate(44, 1024, 0.01m), Candidate(42, 1024, -0.002m));

    private static ImmutableArray<RlPortfolioSeedProgress> Progress() => ImmutableArray.Create(new RlPortfolioSeedProgress(42, 1024), new RlPortfolioSeedProgress(43, 1024));

    [Fact]
    public void ASelectionThatFollowsTheOrderBecomesTheSummary()
    {
        var summary = RlPortfolioTrainingSummaries.Create(Run, smokeOnly: true, (43, 512), Ordered(), Progress());
        Assert.Equal((RlPortfolioTrainingSummaries.SchemaVersion, RlPortfolioContract.SpecificationId, Run), (summary.SchemaVersion, summary.SpecificationId, summary.RunId));
        Assert.Equal((RlPortfolioContract.ValidationPartition, "Train", true), (summary.SelectionPartition, summary.SelectionGroup, summary.SmokeOnly));
        Assert.Equal((43, 512, 0.0125m), (summary.Selected.Seed, summary.Selected.Checkpoint, summary.Selected.MeanReturn));
        Assert.Equal(4, summary.Candidates.Length);
        Assert.Equal(RlPortfolioTrainingSummaries.SelectionRule, summary.SelectionRule);
    }

    [Fact]
    public void ASelectionThatContradictsTheOrderIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingSummaries.Create(Run, false, (42, 512), Ordered(), Progress()));          // not the first
        Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingSummaries.Create(Run, false, (99, 512), Ordered(), Progress()));          // not a candidate
        Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingSummaries.Create(Run, false, (43, 512), ImmutableArray<RlPortfolioCandidate>.Empty, Progress()));
        Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingSummaries.Create(Run, false, (43, 512), default, Progress()));
        var unordered = ImmutableArray.Create(Candidate(43, 512, 0.0125m), Candidate(44, 1024, 0.01m), Candidate(42, 512, 0.01m));     // equal means: smaller checkpoint must come first
        Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingSummaries.Create(Run, false, (43, 512), unordered, Progress()));
        var worseFirst = ImmutableArray.Create(Candidate(42, 512, 0.01m), Candidate(43, 512, 0.0125m));
        Assert.Throws<InvalidDataException>(() => RlPortfolioTrainingSummaries.Create(Run, false, (42, 512), worseFirst, Progress()));
    }

    [Fact]
    public async Task TheFileKeepsTheDecimalReturnsExactlyAndTheDocumentedNames()
    {
        string path = Path.Combine(Path.GetTempPath(), "sa_rl_summary_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var summary = RlPortfolioTrainingSummaries.Create(Run, false, (43, 512), Ordered(), Progress());
            await RlPortfolioTrainingSummaries.SaveAsync(path, summary);
            string text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
            Assert.Equal("validation", root.GetProperty("selection_partition").GetString());
            Assert.Equal(512, root.GetProperty("selected").GetProperty("checkpoint").GetInt32());
            Assert.Equal(0.0125m, root.GetProperty("selected").GetProperty("mean_return").GetDecimal());
            Assert.Equal(4, root.GetProperty("candidates").GetArrayLength());
            Assert.Equal(1024, root.GetProperty("seed_progress")[0].GetProperty("training_transitions").GetInt32());
            var back = JsonSerializer.Deserialize<RlPortfolioTrainingSummary>(text, RlFiles.Json)!;
            Assert.Equal(summary.Selected.MeanReturn, back.Selected.MeanReturn);
            Assert.Equal(summary.Candidates.Select(c => (c.Seed, c.Checkpoint, c.MeanReturn)), back.Candidates.Select(c => (c.Seed, c.Checkpoint, c.MeanReturn)));
            Assert.Equal(summary.Candidates[0].BlockReturns[0], back.Candidates[0].BlockReturns[0]);
        }
        finally { File.Delete(path); }
    }
}
