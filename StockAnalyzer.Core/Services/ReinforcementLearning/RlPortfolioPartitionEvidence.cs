using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// The partition evidence of one run (<c>partition.json</c>): the method, the minimum bars asked for, the blocks they became and the cuts.
/// Written only for a non-default plan, so a default run publishes exactly the files it published before. Stored data is untrusted until
/// <see cref="Validate"/> recomputes it from the panel it claims.
/// </summary>
public sealed record RlPortfolioPartitionDocument(int Schema, string Mode, int TrainBars, int ValidationBars, int TestBars,
    int TrainBlocks, int ValidationBlocks, int TestBlocks, int CalendarLength, int TrainCut, int ValidationCut);

public static class RlPortfolioPartitionEvidence
{
    public const int SchemaVersion = 1;
    public const string FileName = "partition.json";
    public const string RatioName = "ratio";
    public const string BlockCountName = "block_count";
    /// <summary>True when a run with this plan publishes <see cref="FileName"/>.</summary>
    public static bool IsPublished(RlPanelPartitionPlan? plan) => plan is not null && !plan.IsDefault;

    public static string ModeName(RlPortfolioPartitionMode mode) => mode switch
    {
        RlPortfolioPartitionMode.Ratio => RatioName,
        RlPortfolioPartitionMode.BlockCount => BlockCountName,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static RlPortfolioPartitionDocument Create(RlPanelPartitionPlan plan, RlPanelSnapshot panel)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(panel);
        var need = plan.Requirement;
        var bars = plan.Bars ?? new RlPanelMinimumBars(RlPanelBlockRequirement.BarsOf(need.TrainBlocks),
            RlPanelBlockRequirement.BarsOf(need.ValidationBlocks), RlPanelBlockRequirement.BarsOf(need.TestBlocks));
        return new RlPortfolioPartitionDocument(SchemaVersion, ModeName(plan.Mode), bars.Train, bars.Validation, bars.Test,
            need.TrainBlocks, need.ValidationBlocks, need.TestBlocks, panel.Calendar.Length, panel.Catalog.TrainCut, panel.Catalog.ValidationCut);
    }

    public static Task SaveAsync(string path, RlPortfolioPartitionDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        return AtomicJsonFile.SaveAsync(path, document, RlFiles.Json);
    }

    /// <summary>Reads a stored document: linked or oversized files and malformed JSON are rejected; the content is still unvalidated.</summary>
    public static RlPortfolioPartitionDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > RlPortfolioEvaluationContract.MaximumReportBytes)
            throw new InvalidDataException("A linked or oversized partition document is prohibited.");
        try
        {
            return JsonSerializer.Deserialize<RlPortfolioPartitionDocument>(File.ReadAllText(path), RlFiles.Json)
                ?? throw new InvalidDataException("The partition document is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The partition document is malformed.", error); }
    }

    /// <summary>
    /// The plan a stored document describes (the plan that produced the run), or an <see cref="InvalidDataException"/> when the document is
    /// inconsistent with itself: unknown mode or schema, a minimum that does not round up to the stored blocks.
    /// </summary>
    public static RlPanelPartitionPlan ToPlan(RlPortfolioPartitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidDataException($"Partition document {detail}.");
        }
        Require(document.Schema == SchemaVersion, "has an unsupported schema version");
        var mode = document.Mode switch
        {
            RatioName => RlPortfolioPartitionMode.Ratio,
            BlockCountName => RlPortfolioPartitionMode.BlockCount,
            _ => (RlPortfolioPartitionMode?)null,
        };
        Require(mode is not null, "names an unknown partition mode");
        bool positive = document.TrainBars >= 1 && document.ValidationBars >= 1 && document.TestBars >= 1;
        Require(positive, "states a non-positive minimum");
        var bars = new RlPanelMinimumBars(document.TrainBars, document.ValidationBars, document.TestBars);
        var blocks = bars.ToRequirement();
        Require(blocks.TrainBlocks == document.TrainBlocks && blocks.ValidationBlocks == document.ValidationBlocks && blocks.TestBlocks == document.TestBlocks,
            "states blocks that do not follow from its minimum bars");
        return new RlPanelPartitionPlan(mode!.Value, blocks, bars);
    }

    /// <summary>
    /// Rejects a document that differs from what the panel gives: the blocks follow from the stored minimum bars, the calendar length and the
    /// cuts equal the panel's, and every training-group cell of the catalog requires exactly the stored blocks.
    /// </summary>
    public static RlPanelPartitionPlan Validate(RlPortfolioPartitionDocument document, RlPanelSnapshot panel)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(panel);
        var plan = ToPlan(document);
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidDataException($"Partition document {detail}.");
        }
        Require(document.CalendarLength == panel.Calendar.Length, "belongs to a different calendar length");
        var (trainCut, validationCut) = RlPanelCatalogBuilder.PlanCuts(panel.Calendar.Length, plan);
        Require(document.TrainCut == trainCut && document.ValidationCut == validationCut
            && trainCut == panel.Catalog.TrainCut && validationCut == panel.Catalog.ValidationCut, "states different cuts");
        var required = panel.Catalog.Cells.ToDictionary(c => (c.Partition, c.Group), c => c.RequiredBlocks);
        Require(required[(RlPortfolioContract.TrainPartition, RlPanelSymbolGroup.Train)] == document.TrainBlocks
            && required[(RlPortfolioContract.ValidationPartition, RlPanelSymbolGroup.Train)] == document.ValidationBlocks
            && required[(RlPortfolioContract.OosPartition, RlPanelSymbolGroup.Train)] == document.TestBlocks,
            "states block minimums that the panel catalog does not require");
        return plan;
    }
}
