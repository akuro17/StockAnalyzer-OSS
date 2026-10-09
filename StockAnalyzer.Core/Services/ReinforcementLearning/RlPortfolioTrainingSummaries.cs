using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Builds and saves <see cref="RlPortfolioTrainingSummary"/>. The file name and schema live here, not in <see cref="RlPortfolioContract"/>, because every
/// public constant of the contract feeds its pinned hash.
/// </summary>
internal static class RlPortfolioTrainingSummaries
{
    public const string FileName = "training_summary.json";
    public const int SchemaVersion = 1;
    public const string SelectionRule = "max mean validation return over all validation blocks; ties: smaller checkpoint, then smaller seed";

    /// <summary>
    /// The candidates must already be in selection order (see <c>RlPortfolioIpcHost.Candidates</c>). The selected pair must be a candidate and the first
    /// of them; anything else means the evidence would contradict the rule and is refused.
    /// </summary>
    public static RlPortfolioTrainingSummary Create(Guid runId, bool smokeOnly, (int Seed, int Checkpoint) selected,
        ImmutableArray<RlPortfolioCandidate> candidates, ImmutableArray<RlPortfolioSeedProgress> seedProgress)
    {
        if (candidates.IsDefaultOrEmpty) throw new InvalidDataException("A training summary needs at least one complete validation candidate.");
        var best = candidates[0];
        if (best.Seed != selected.Seed || best.Checkpoint != selected.Checkpoint)
            throw new InvalidDataException("The confirmed selection is not the first candidate of the selection order.");
        for (int i = 1; i < candidates.Length; i++)
        {
            var before = candidates[i - 1]; var after = candidates[i];
            bool ordered = before.MeanReturn > after.MeanReturn
                || (before.MeanReturn == after.MeanReturn && (before.Checkpoint, before.Seed).CompareTo((after.Checkpoint, after.Seed)) < 0);
            if (!ordered) throw new InvalidDataException("The candidates are not in the documented selection order.");
        }
        return new RlPortfolioTrainingSummary(SchemaVersion, RlPortfolioContract.SpecificationId, runId, SelectionRule,
            RlPortfolioSelectionCell.Partition, RlPortfolioSelectionCell.Group.ToString(), smokeOnly, best, candidates, seedProgress);
    }

    public static Task SaveAsync(string path, RlPortfolioTrainingSummary summary) => AtomicJsonFile.SaveAsync(path, summary, RlFiles.Json);
}
