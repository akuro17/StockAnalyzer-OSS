using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Persistence and consistency validation of <see cref="RlPortfolioEvaluationReport"/>: a stored report is data until it is validated against the panel and model it claims.</summary>
public static class RlPortfolioEvaluationReports
{
    public static Task SaveAsync(string path, RlPortfolioEvaluationReport report, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        return AtomicJsonFile.SaveAsync(path, report, RlFiles.Json);
    }

    /// <summary>Reads a stored report: linked or oversized files and malformed JSON are rejected; the content is still unvalidated.</summary>
    public static RlPortfolioEvaluationReport Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > RlPortfolioEvaluationContract.MaximumReportBytes)
            throw new InvalidDataException("A linked or oversized portfolio evaluation report is prohibited.");
        try
        {
            return JsonSerializer.Deserialize<RlPortfolioEvaluationReport>(File.ReadAllText(path), RlFiles.Json)
                ?? throw new InvalidDataException("The portfolio evaluation report is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The portfolio evaluation report is malformed.", error); }
    }

    /// <summary>
    /// Rejects a report that is not exactly what the protocol would produce for this panel, model, costs and cap: wrong or downgraded
    /// schema, contract, specification or identity, a cell set or minimum that differs, or statistics that do not follow from the stored
    /// per-block returns. Every statistic is recomputed (decimal, exact). A non-null <paramref name="plan"/> (short-history only) sets the expected
    /// block minimum of the validation and test cells to the plan's counts; null expects the minimum of the evaluation mode.
    /// </summary>
    public static void Validate(RlPortfolioEvaluationReport report, RlPanelManifest manifest, RlPanelEvaluationMode mode, string modelSha256,
        BacktestConfiguration costs, int maxWeightPercent, RlPanelPartitionPlan? plan = null, RlPortfolioObservationLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(costs);
        if (plan is not null && mode != RlPanelEvaluationMode.ShortHistory)
            throw new ArgumentException("Partition plans apply to the short-history evaluation only.", nameof(plan));
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidDataException($"Portfolio evaluation report {detail}.");
        }
        Require(report.SpecificationId == RlPortfolioContract.SpecificationId, "belongs to a different specification");
        Require(report.SchemaVersion == RlPortfolioEvaluationContract.SchemaVersion, "has an unsupported schema version");
        Require(report.ContractHash == RlPortfolioEvaluationContract.ContractHash && report.PortfolioContractHash == (layout ?? RlPortfolioObservationLayout.V1).ContractHash,
            "was produced under a different contract");
        Require(report.EvaluationSpecificationId == RlPortfolioEvaluator.SpecificationId(mode), "names a different evaluation specification (downgrade or upgrade)");
        Require(report.UniverseSha256 == manifest.UniverseSha256 && report.CalendarSha256 == manifest.CalendarSha256, "belongs to a different panel");
        Require(report.ModelSha256 == modelSha256, "belongs to a different model file");
        Require(!report.Symbols.IsDefault && !report.TrainSymbols.IsDefault && !report.HoldoutSymbols.IsDefault && !report.Cells.IsDefault && !report.Notes.IsDefault,
            "is missing a required list");
        var symbols = manifest.Symbols.Select(e => e.Symbol).ToImmutableArray();
        Require(report.Symbols.SequenceEqual(symbols) && report.HoldoutSymbols.SequenceEqual(manifest.HoldoutSymbols)
            && report.TrainSymbols.SequenceEqual(symbols.Where(s => !manifest.HoldoutSymbols.Contains(s))), "lists a different symbol split");
        Require(report.SymbolHoldout == manifest.HoldoutEnabled && report.SymbolHoldout == !manifest.HoldoutSymbols.IsEmpty, "states a different holdout mode");
        Require(report.Costs is not null && JsonSerializer.Serialize(report.Costs, RlFiles.Json) == JsonSerializer.Serialize(costs, RlFiles.Json), "was run under different costs");
        Require(report.MaxWeightPercent == maxWeightPercent, "was run under a different weight cap");
        Require(report.PricePit == RlSourceContract.Unverified && report.InvestmentEfficacy == RlConstraintContract.InvestmentEfficacy
            && report.Statement == RlPortfolioEvaluationContract.Statement, "does not carry the mandatory limitation statements");
        var notes = ImmutableArray.CreateBuilder<string>();
        notes.Add(RlPortfolioEvaluationContract.SameCapNote);
        if (mode == RlPanelEvaluationMode.ShortHistory) notes.Add(RlPortfolioEvaluationContract.ShortHistoryNote);
        if (!manifest.HoldoutEnabled) notes.Add(RlPortfolioEvaluationContract.TimeHoldoutOnlyNote);
        Require(report.Notes.SequenceEqual(notes), "does not carry the notes of its mode");

        int required = mode == RlPanelEvaluationMode.ShortHistory ? 1 : RlContract.EvaluationEpisodeCount;
        int RequiredFor(string partition) => plan is null ? required
            : partition == RlPortfolioContract.ValidationPartition ? plan.Requirement.ValidationBlocks : plan.Requirement.TestBlocks;
        var expected = new System.Collections.Generic.List<(string Partition, RlPanelSymbolGroup Group)>
        {
            (RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Train), (RlPortfolioContract.OosPartition, RlPanelSymbolGroup.Train),
        };
        if (manifest.HoldoutEnabled)
        {
            expected.Add((RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Holdout));
            expected.Add((RlPortfolioContract.OosPartition, RlPanelSymbolGroup.Holdout));
        }
        Require(report.Cells.Select(c => (c.Partition, c.Group)).SequenceEqual(expected), "has a different set of evaluation cells");
        foreach (var cell in report.Cells)
        {
            string name = $"cell {cell.Partition}/{cell.Group}";
            var groupSymbols = cell.Group == RlPanelSymbolGroup.Holdout ? manifest.HoldoutSymbols : report.TrainSymbols;
            Require(!cell.Symbols.IsDefault && cell.Symbols.SequenceEqual(groupSymbols), $"{name} lists different symbols");
            Require(cell.RequiredBlocks == RequiredFor(cell.Partition) && cell.BlockCount >= 0, $"{name} states a different block minimum");
            Require(!cell.Blocks.IsDefault && !cell.Baselines.IsDefault, $"{name} is missing a required list");
            Require(cell.Evaluable == cell.BlockCount >= cell.RequiredBlocks, $"{name} states a wrong evaluability");
            if (!cell.Evaluable)
            {
                Require(cell.Group == RlPanelSymbolGroup.Holdout, $"{name} is a training cell below its minimum (InsufficientData)");
                Require(cell.Blocks.IsEmpty && cell.Policy is null && cell.Baselines.IsEmpty, $"{name} is not evaluable but carries results");
                continue;
            }
            Require(cell.Blocks.Length == cell.BlockCount && cell.Blocks.Select(b => b.BlockId).Distinct(StringComparer.Ordinal).Count() == cell.Blocks.Length,
                $"{name} lists a different or duplicated block set");
            foreach (var block in cell.Blocks)
                Require(block.BlockId.StartsWith(cell.Partition + ":", StringComparison.Ordinal) && !block.TradableSymbols.IsDefault
                    && block.TradableSymbols.Length >= RlPortfolioContract.MinimumBlockSymbols && block.TradableSymbols.All(cell.Symbols.Contains),
                    $"{name} holds a block outside its partition or symbol group");
            var (policy, baselines) = RlPortfolioEvaluator.Summarize(cell.Blocks);
            Require(cell.Policy == policy, $"{name} policy statistics do not follow from its block returns");
            Require(cell.Baselines.Select(b => b.Id).SequenceEqual(RlPortfolioEvaluationContract.Baselines) && cell.Baselines.SequenceEqual(baselines),
                $"{name} baseline or paired-delta statistics do not follow from its block returns");
        }
    }
}
