using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>Pure wording of the portfolio dialog: every text comes from a locale key, every number from the Core result.</summary>
public static class PortfolioRlPresentation
{
    public static string SymbolStatus(RlPanelSymbolStatus status, ILocalizationService localization)
        => localization.GetString("Prl.SymbolStatus." + status);

    public static string Blocker(RlPanelStartBlocker blocker, RlPortfolioSettings settings, ILocalizationService localization)
        => blocker switch
        {
            RlPanelStartBlocker.None => "",
            RlPanelStartBlocker.TooFewSymbols => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Blocker.TooFewSymbols"), RlPortfolioContract.MinimumSymbols),
            RlPanelStartBlocker.TooManySymbols => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Blocker.TooManySymbols"), settings.MaxSymbols),
            RlPanelStartBlocker.Infeasible => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Blocker.Infeasible"), localization.GetString("Prl.Start.Recalculate")),
            _ => localization.GetString("Prl.Blocker." + blocker),
        };

    public static string Role(RlPanelSymbolRole role, ILocalizationService localization) => localization.GetString("Prl.Role." + role);

    /// <summary>"250 bars = 2 block(s) = 384 bars": what a typed minimum becomes once it is rounded up to whole blocks.</summary>
    public static string EffectiveBars(int bars, ILocalizationService localization)
    {
        int blocks = RlPanelBlockRequirement.BlocksFor(bars);
        return string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Minimum.Effective"), bars, blocks, RlPanelBlockRequirement.BarsOf(blocks));
    }

    /// <summary>The training consequences of a block-count plan: how often each train block is replayed per seed, and the hint to raise a single train block.</summary>
    public static IReadOnlyList<string> PartitionNotices(RlPanelPartitionPlan plan, ILocalizationService localization)
    {
        var notices = new List<string>();
        int replays = plan.TrainingBlockReplays;
        notices.Add(replays >= 1 ? string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Partition.Replays"), replays)
            : localization.GetString("Prl.Partition.ReplaysFew"));
        if (plan.Requirement.TrainBlocks == 1) notices.Add(localization.GetString("Prl.Partition.RaiseTrain"));
        return notices;
    }

    private static string Partition(string partition, ILocalizationService localization) => localization.GetString("Rl.Ui.Partition." + partition);

    /// <summary>Calendar length against the smallest accepted length, and the usable symbols against the selection.</summary>
    public static string Panel(PanelPreflight preflight, ILocalizationService localization)
        => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Preflight.Panel"), preflight.CalendarLength,
            preflight.RequiredCalendarLength, preflight.ReadyCount, preflight.Symbols.Length);

    /// <summary>Segments found against the minimum for each partition of the training symbol group (always shown, feasible or not).</summary>
    public static IReadOnlyList<string> Cells(PanelPreflight preflight, ILocalizationService localization)
        => preflight.Cells.Select(c => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Preflight.Cell"),
            Partition(c.Partition, localization), c.BlockCount, c.RequiredBlocks)).ToArray();

    /// <summary>The shortfall: which partitions miss segments, or that the panel is too short at all.</summary>
    public static string Shortfall(PanelPreflight preflight, ILocalizationService localization)
    {
        var short_ = preflight.Cells.Where(c => !c.Evaluable).Select(c => string.Format(CultureInfo.CurrentCulture,
            localization.GetString("Prl.Preflight.Cell"), Partition(c.Partition, localization), c.BlockCount, c.RequiredBlocks)).ToArray();
        string detail = short_.Length > 0 ? string.Join("; ", short_) : Panel(preflight, localization);
        return string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Preflight.Short"), detail);
    }

    private static string Percent(decimal value) => value.ToString("P2", CultureInfo.CurrentCulture);

    /// <summary>One line per evaluated cell with the model's mean return, then one line per baseline with the mean paired difference and the share of segments the model led, then the point-in-time caveat (only while the report says the prices are unverified) and the report's own statement.</summary>
    public static IReadOnlyList<string> Result(RlPortfolioEvaluationReport report, ILocalizationService localization)
    {
        var lines = new List<string>();
        foreach (var cell in report.Cells)
        {
            string partition = Partition(cell.Partition, localization), group = localization.GetString("Prl.Group." + cell.Group);
            if (!cell.Evaluable || cell.Policy is null)
            {
                lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Result.NotEvaluable"), partition, group, cell.BlockCount, cell.RequiredBlocks));
                continue;
            }
            // The training-symbol validation cell is the one the trainer selects the checkpoint on (RlPortfolioSelectionCell).
            bool selectionCell = RlPortfolioSelectionCell.Is(cell.Partition, cell.Group);
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString(selectionCell ? "Prl.Result.CellSelection" : "Prl.Result.Cell"),
                partition, group, cell.Policy.Count, Percent(cell.Policy.Mean)));
            foreach (var baseline in cell.Baselines)
                lines.Add("  " + string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Result.Versus"),
                    localization.GetString("Prl.Baseline." + baseline.Id), Percent(baseline.PairedDelta.Mean), Percent(baseline.PairedDelta.PositiveRate)));
        }
        if (report.PricePit == RlSourceContract.Unverified) lines.Add(localization.GetString("Prl.Result.PricePit"));
        lines.Add(report.Statement);
        return lines;
    }

    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm";
    /// <summary>Leading hex digits of a model hash shown in the lineage and the base list (the full hash is in the evidence file).</summary>
    public const int HashPrefixLength = 12;
    /// <summary>Leading characters of a run id shown in the base list.</summary>
    public const int RunIdPrefixLength = 8;

    /// <summary>One line of the base-run list: when it was made, which run, and the period of its data (or why that period is unknown).</summary>
    public static string BaseRun(RlPortfolioBaseRunInfo info, ILocalizationService localization)
    {
        string period = info.Period.Status switch
        {
            RlBasePeriodStatus.Known => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Transfer.Period"),
                info.Period.PeriodStart!.Value.ToString(DateFormat, CultureInfo.InvariantCulture), info.Period.PeriodEndExclusive!.Value.ToString(DateFormat, CultureInfo.InvariantCulture)),
            RlBasePeriodStatus.Ambiguous => localization.GetString("Prl.Transfer.PeriodAmbiguous"),
            _ => localization.GetString("Prl.Transfer.PeriodUnknown"),
        };
        return string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Transfer.BaseRow"),
            info.CreatedUtc.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture), info.RunId.ToString("N")[..RunIdPrefixLength], period);
    }

    /// <summary>One entry of the panel choice: the panel's id, its symbols (and how many are held out) and the period of its data.</summary>
    public static string StoredPanel(RlPortfolioStoredPanelInfo panel, ILocalizationService localization)
        => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Panel.Row"), panel.PanelId.ToString("N")[..RunIdPrefixLength], panel.SymbolCount, panel.HoldoutCount,
            panel.PeriodStartInclusive.ToString(DateFormat, CultureInfo.InvariantCulture), panel.PeriodEndExclusive.ToString(DateFormat, CultureInfo.InvariantCulture));

    /// <summary>One entry of the comparison lists: when the run was made, how it was made, which run, and which panel (leading hash digits) it used.</summary>
    public static string ComparableRun(RlPortfolioComparableRun run, ILocalizationService localization)
    {
        string kind = localization.GetString(run.Kind switch
        {
            RlPortfolioTransferContract.FineTuneKind => "Prl.Compare.Kind.FineTune",
            RlPortfolioTransferContract.ZeroShotKind => "Prl.Compare.Kind.ZeroShot",
            _ => "Prl.Compare.Kind.Scratch",
        });
        return string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Compare.Row"), run.CreatedUtc.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            kind, run.RunId.ToString("N")[..RunIdPrefixLength], run.UniverseSha256[..RunIdPrefixLength]);
    }

    /// <summary>
    /// The lines of a comparison (A minus B): per cell the paired segments, the mean difference and how often A was ahead, tied or behind; then the sign test as a
    /// reference value only (and that no inference is possible with too few effective pairs); the cell that chose the checkpoint says so; the limitation statement ends.
    /// </summary>
    public static IReadOnlyList<string> Comparison(RlPortfolioTransferComparisonResult result, ILocalizationService localization)
    {
        var lines = new List<string>();
        foreach (var cell in result.Cells)
        {
            string partition = Partition(cell.Partition, localization), group = localization.GetString("Prl.Group." + cell.Group);
            if (!cell.Comparable || cell.PairedDelta is null)
            {
                lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Compare.NotComparable"), partition, group));
                continue;
            }
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString(cell.SelectionContaminated ? "Prl.Compare.CellSelection" : "Prl.Compare.Cell"),
                partition, group, cell.BlockCount, Percent(cell.PairedDelta.Mean), cell.Wins, cell.Ties, cell.Losses));
            if (cell.NEff == 0) lines.Add("  " + localization.GetString("Prl.Compare.NoPairs"));
            else
            {
                lines.Add("  " + string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Compare.Sign"),
                    cell.OneSidedP!.Value.ToString(PValueFormat, CultureInfo.CurrentCulture), cell.NEff, cell.MinAttainableP!.Value.ToString(PValueFormat, CultureInfo.CurrentCulture)));
                if (cell.Inference == RlPortfolioComparisonInference.Unavailable)
                    lines.Add("  " + string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Compare.Unavailable"), RlPortfolioSignTest.MinimumEffectivePairs));
            }
        }
        lines.Add(result.Statement);
        return lines;
    }

    /// <summary>Significant digits of a p-value shown as a reference value (not a threshold).</summary>
    private const string PValueFormat = "0.######";

    public static string Admitted(RlPortfolioBaseRun admitted, DateOnly firstValidation, ILocalizationService localization)
        => string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Transfer.Admitted"),
            admitted.PeriodEndExclusive.ToString(DateFormat, CultureInfo.InvariantCulture), firstValidation.ToString(DateFormat, CultureInfo.InvariantCulture));

    /// <summary>
    /// The lineage of a run that used a stored model, then the mandatory limitation statement of the transfer contract (shown verbatim: it is the text the
    /// evidence file carries). <paramref name="freeze"/> is null for the evaluation of a stored model.
    /// </summary>
    public static IReadOnlyList<string> Lineage(string kind, RlPortfolioBaseRun admitted, DateOnly firstValidation, string? freeze, ILocalizationService localization,
        RlPortfolioSymbolOverlap? overlap = null)
    {
        var lines = new List<string>
        {
            localization.GetString("Prl.Transfer.Lineage.Kind." + kind),
            string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Transfer.Lineage.Base"), admitted.RunId.ToString("N"), admitted.ModelSha256[..HashPrefixLength]),
        };
        if (freeze is not null) lines.Add(localization.GetString("Prl.Transfer.Lineage.Freeze." + freeze));
        lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Transfer.Lineage.Dates"),
            admitted.PeriodEndExclusive.ToString(DateFormat, CultureInfo.InvariantCulture), firstValidation.ToString(DateFormat, CultureInfo.InvariantCulture)));
        if (overlap is not null)
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Prl.Transfer.Overlap"), overlap.HeldOutSeenByBase.Length, overlap.TargetHeldOutSymbols.Length));
        lines.Add(localization.GetString("Prl.Limit.TransferTrainCell"));
        lines.Add(RlPortfolioTransferContract.Statement);
        return lines;
    }
}
