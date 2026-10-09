using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Evaluation protocol of a portfolio model (S8-S10): the greedy policy and three baselines run through the same Core-owned
/// <see cref="RlPortfolioSession"/> and cost model on every complete block of each cell (validation and OOS partition x TRAIN and HOLDOUT
/// symbols). Other symbols are masked: they are not tradable in the session and the observation never reads their bars. Every valid block of
/// a cell is evaluated; nothing is sampled or capped. Descriptive evidence only.
/// </summary>
public static class RlPortfolioEvaluator
{
    /// <summary>The policy under evaluation: account snapshot and the tradable panel symbols -> target weights (one per asset slot, cash residual).</summary>
    internal delegate RlPortfolioWeightVector Policy(RlPortfolioSnapshot snapshot, ImmutableArray<int> tradable);

    private delegate RlPortfolioStepResult Decision(RlPortfolioSession session, RlPortfolioSnapshot snapshot, int index, CancellationToken cancellationToken);

    /// <summary>Evaluates the exported actor at <paramref name="modelPath"/> on the panel; the model identity must match the panel.</summary>
    public static RlPortfolioEvaluationReport Evaluate(RlPanelSnapshot panel, string modelPath, BacktestConfiguration costs,
        int maxWeightPercent, bool smokeOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(costs);
        using var model = new RlPortfolioActorModel(modelPath);
        string Metadata(string key) => model.Metadata[key];
        if (Metadata(RlPortfolioContract.MetadataUniverseSha256) != panel.Manifest.UniverseSha256
            || Metadata(RlPortfolioContract.MetadataCalendarSha256) != panel.Manifest.CalendarSha256)
            throw new InvalidDataException("The model was trained on a different panel universe or calendar.");
        if (model.MaxSymbols < panel.SymbolCount) throw new InvalidDataException("The model has fewer asset slots than the panel has symbols.");
        string specification = SpecificationId(panel.Catalog.Mode);
        if (Metadata(RlPortfolioContract.MetadataEvaluationSpecification) != specification)
            throw new InvalidDataException("The model's evaluation specification differs from the panel catalog mode.");
        return EvaluateModel(panel, model, modelPath, costs, maxWeightPercent, smokeOnly, cancellationToken);
    }

    /// <summary>
    /// Evaluates a stored model on a panel it was NOT trained on (zero-shot transfer): the same protocol, policy, baselines and report as
    /// <see cref="Evaluate"/>, without its two identity checks (the model's universe/calendar hashes and evaluation specification belong to the panel it
    /// was trained on). The slot check stays: the model needs at least one asset slot per panel symbol. The fixed scaler of the model is used as it is
    /// (never refitted), and the report carries the model's own hash. Whether the base data lies before the panel's validation start is the business of
    /// <see cref="RlPortfolioTransferAdmission"/>, not of this function.
    /// </summary>
    public static RlPortfolioEvaluationReport EvaluateTransferred(RlPanelSnapshot panel, string modelPath, BacktestConfiguration costs,
        int maxWeightPercent, bool smokeOnly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(costs);
        using var model = new RlPortfolioActorModel(modelPath);
        if (model.MaxSymbols < panel.SymbolCount) throw new InvalidDataException("The model has fewer asset slots than the panel has symbols.");
        return EvaluateModel(panel, model, modelPath, costs, maxWeightPercent, smokeOnly, cancellationToken);
    }

    /// <summary>The shared tail of both entry points: policy over the model, every cell of the catalog, the report.</summary>
    private static RlPortfolioEvaluationReport EvaluateModel(RlPanelSnapshot panel, RlPortfolioActorModel model, string modelPath, BacktestConfiguration costs,
        int maxWeightPercent, bool smokeOnly, CancellationToken cancellationToken)
    {
        Policy policy = (snapshot, tradable) => model.Decide(
            RlPortfolioObservationBuilder.Build(panel, model.MaxSymbols, snapshot, tradable, model.Scaler),
            RlPortfolioObservationBuilder.SlotMask(panel.SymbolCount, model.MaxSymbols, tradable), maxWeightPercent);
        var cells = EvaluateCells(panel, panel.Catalog, panel.Symbols, policy, costs, maxWeightPercent, cancellationToken);
        return BuildReport(panel.Manifest, panel.Catalog, SpecificationId(panel.Catalog.Mode), ContentHash.OfFile(modelPath), costs, maxWeightPercent, smokeOnly, cells,
            model.Layout);
    }

    /// <summary>The evaluation specification id a catalog mode stands for (the model metadata and the report carry the same id).</summary>
    public static string SpecificationId(RlPanelEvaluationMode mode)
        => mode == RlPanelEvaluationMode.ShortHistory ? RlShortHistoryEvaluationContract.SpecificationId : RlFiles.EvaluationRevision;

    internal static ImmutableArray<RlPortfolioCellReport> EvaluateCells(IRlPanelBars bars, RlPanelCatalog catalog, ImmutableArray<string> symbols,
        Policy policy, BacktestConfiguration costs, int maxWeightPercent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(costs);
        if (symbols.IsDefault || symbols.Length != bars.SymbolCount || catalog.IsHoldout.Length != bars.SymbolCount)
            throw new ArgumentException("One symbol name and one holdout flag per panel symbol are required.", nameof(symbols));
        var cells = ImmutableArray.CreateBuilder<RlPortfolioCellReport>();
        foreach (var count in catalog.Cells.Where(c => c.Partition != RlPortfolioContract.TrainPartition))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = Enumerable.Range(0, symbols.Length).Where(i => catalog.IsHoldout[i] == (count.Group == RlPanelSymbolGroup.Holdout)).ToImmutableArray();
            var names = group.Select(i => symbols[i]).ToImmutableArray();
            if (!count.Evaluable)
            {
                if (count.Group == RlPanelSymbolGroup.Train)
                    throw new InvalidDataException(RlSourceContract.InsufficientData(
                        $"{count.Partition}/{count.Group} has {count.BlockCount} of {count.RequiredBlocks} blocks."));
                cells.Add(new RlPortfolioCellReport(count.Partition, count.Group, names, count.BlockCount, count.RequiredBlocks, false,
                    ImmutableArray<RlPortfolioBlockResult>.Empty, null, ImmutableArray<RlPortfolioBaselineSummary>.Empty));
                continue;
            }
            var rows = ImmutableArray.CreateBuilder<RlPortfolioBlockResult>();
            foreach (var block in catalog.CellBlocks(count.Partition, count.Group))
                rows.Add(EvaluateBlock(bars, symbols, block, catalog.EligibleIn(block, count.Group), policy, costs, maxWeightPercent, cancellationToken));
            var blocks = rows.ToImmutable();
            var (distribution, baselines) = Summarize(blocks);
            cells.Add(new RlPortfolioCellReport(count.Partition, count.Group, names, count.BlockCount, count.RequiredBlocks, true, blocks, distribution, baselines));
        }
        return cells.ToImmutable();
    }

    /// <summary>The single owner of the per-cell statistics: the report writer and its validator both call it.</summary>
    internal static (RlReturnDistribution Policy, ImmutableArray<RlPortfolioBaselineSummary> Baselines) Summarize(ImmutableArray<RlPortfolioBlockResult> blocks)
    {
        var policy = RlAllBlockEvaluation.Distribution(blocks.Select(b => b.PolicyReturn));
        RlPortfolioBaselineSummary Baseline(string id, Func<RlPortfolioBlockResult, decimal> pick)
            => new(id, RlAllBlockEvaluation.Distribution(blocks.Select(pick)), RlAllBlockEvaluation.Distribution(blocks.Select(b => b.PolicyReturn - pick(b))));
        return (policy, ImmutableArray.Create(
            Baseline(RlPortfolioEvaluationContract.CashBaseline, b => b.CashReturn),
            Baseline(RlPortfolioEvaluationContract.EqualBuyHoldBaseline, b => b.EqualBuyHoldReturn),
            Baseline(RlPortfolioEvaluationContract.EqualDailyRebalancedBaseline, b => b.EqualDailyRebalancedReturn)));
    }

    internal static RlPortfolioEvaluationReport BuildReport(RlPanelManifest manifest, RlPanelCatalog catalog, string evaluationSpecification,
        string modelSha256, BacktestConfiguration costs, int maxWeightPercent, bool smokeOnly, ImmutableArray<RlPortfolioCellReport> cells,
        RlPortfolioObservationLayout? layout = null)
    {
        bool symbolHoldout = catalog.IsHoldout.Contains(true);
        var symbols = manifest.Symbols.Select(e => e.Symbol).ToImmutableArray();
        var notes = ImmutableArray.CreateBuilder<string>();
        notes.Add(RlPortfolioEvaluationContract.SameCapNote);
        if (catalog.Mode == RlPanelEvaluationMode.ShortHistory) notes.Add(RlPortfolioEvaluationContract.ShortHistoryNote);
        if (!symbolHoldout) notes.Add(RlPortfolioEvaluationContract.TimeHoldoutOnlyNote);
        return new RlPortfolioEvaluationReport(RlPortfolioContract.SpecificationId, RlPortfolioEvaluationContract.SchemaVersion,
            RlPortfolioEvaluationContract.ContractHash, (layout ?? RlPortfolioObservationLayout.V1).ContractHash, evaluationSpecification,
            manifest.UniverseSha256, manifest.CalendarSha256, modelSha256, symbols,
            symbols.Where((_, i) => !catalog.IsHoldout[i]).ToImmutableArray(), manifest.HoldoutSymbols, symbolHoldout,
            costs, maxWeightPercent, smokeOnly, cells, RlSourceContract.Unverified, RlConstraintContract.InvestmentEfficacy,
            RlPortfolioEvaluationContract.Statement, notes.ToImmutable());
    }

    private static RlPortfolioBlockResult EvaluateBlock(IRlPanelBars bars, ImmutableArray<string> symbols, RlPanelBlock block,
        ImmutableArray<int> tradable, Policy policy, BacktestConfiguration costs, int maxWeightPercent, CancellationToken ct)
    {
        ImmutableArray<decimal> Assets(RlPortfolioWeightVector vector) => vector.Assets.Take(bars.SymbolCount).ToImmutableArray();
        var equal = Assets(RlPortfolioWeights.EqualWeights(RlPortfolioObservationBuilder.SlotMask(bars.SymbolCount, bars.SymbolCount, tradable), maxWeightPercent));
        var none = Enumerable.Repeat(0m, bars.SymbolCount).ToImmutableArray();
        decimal Run(Decision decision) => RunBlock(bars, block, tradable, costs, decision, ct);
        return new RlPortfolioBlockResult(block.Id, tradable.Select(i => symbols[i]).ToImmutableArray(),
            Run((session, snapshot, _, token) => session.Step(Assets(policy(snapshot, tradable)), token)),
            Run((session, _, _, token) => session.Step(none, token)),
            Run((session, _, index, token) => index == 0 ? session.Step(equal, token) : session.Hold(token)),
            Run((session, _, _, token) => session.Step(equal, token)));
    }

    private static decimal RunBlock(IRlPanelBars bars, RlPanelBlock block, ImmutableArray<int> tradable, BacktestConfiguration costs,
        Decision decision, CancellationToken ct)
    {
        using var session = new RlPortfolioSession(bars, block, tradable, costs);
        var snapshot = session.Reset(ct);
        for (int index = 0; index < RlContract.EpisodeHorizon; index++) snapshot = decision(session, snapshot, index, ct).Snapshot;
        if (!snapshot.Terminated) throw new InvalidOperationException("The episode did not terminate after the declared horizon.");
        return (snapshot.Equity - costs.InitialCapital) / costs.InitialCapital;
    }
}
