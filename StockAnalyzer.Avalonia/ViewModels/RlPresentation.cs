using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>Cached UI text and dimensionless Q-axis primitives. No platform objects, inference or formatting in render.</summary>
public sealed record RlPresentation(string ModelId, string RequestKey, string Legend, string HudText,
    string Units, string AxisMinimumText, string AxisMaximumText, double AxisMinimum, double AxisMaximum,
    float Hold, float Buy, float Sell, string HoldText, string BuyText, string SellText, RlAction SelectedAction)
{
    public string PolicyId { get; init; } = "";
    public string ContinuousReadout { get; init; } = "";
    public bool HasContinuousReadout => ContinuousReadout.Length > 0;
    public string PolicyControlReadout { get; init; } = "";
    public bool HasPolicyControlReadout => PolicyControlReadout.Length > 0;
    public string EvaluationReadout { get; init; } = "";
    public bool HasEvaluationReadout => EvaluationReadout.Length > 0;
    /// <summary>How the window relates to the agent's training data and which inputs leave the trained range; empty for a replay of the agent's own training source.</summary>
    public string ScopeReadout { get; init; } = "";
    public bool HasScopeReadout => ScopeReadout.Length > 0;
    public DateTime? AnchorDate { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal? EntryPrice { get; init; }
    public decimal? StopPrice { get; init; }
    public decimal PositionQuantity { get; init; }
    public string ActionLabel { get; init; } = "";
    public bool HasDecisionOverlay => AnchorDate.HasValue;
    public static RlPresentation From(RlQSnapshot snapshot, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        if (snapshot.QValues.Length != RlContract.ActionCount || snapshot.QValues.Any(v => !float.IsFinite(v)))
            throw new ArgumentException("Three finite Q values are required.", nameof(snapshot));
        double min = Math.Min(0d, snapshot.QValues.Min()); double max = Math.Max(0d, snapshot.QValues.Max());
        if (min == max) { min = -(double)RlContract.FlatAxisHalfRange; max = (double)RlContract.FlatAxisHalfRange; }
        else { double padding = (max - min) * (double)RlContract.AxisPaddingRatio; min -= padding; max += padding; }
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max) throw new ArgumentException("Q axis is not representable as finite display values.", nameof(snapshot));
        string Number(double value) => value.ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture);
        string Action(RlAction action) => localization.GetString("Rl." + action);
        string legend = localization.GetString("Rl.Maximum") + " " + Action(snapshot.SelectedAction) + " · " + snapshot.Account.MarketAnchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string hud = string.Join("  ", Action(RlAction.Hold) + " " + Number(snapshot.QValues[0]), Action(RlAction.Buy) + " " + Number(snapshot.QValues[1]),
            Action(RlAction.Sell) + " " + Number(snapshot.QValues[2]), localization.GetString("Rl.Maximum") + " " + Action(snapshot.SelectedAction));
        DateTime? anchorDate = snapshot.Account.MarketAnchor != default ? snapshot.Account.MarketAnchor : (snapshot.Anchor != default ? snapshot.Anchor : null);
        decimal? currentPrice = snapshot.Account.MarketWindow.Length > 0 ? snapshot.Account.MarketWindow[^1].Close : null;
        decimal? entryPrice = snapshot.Account.Quantity > 0 && snapshot.Account.EntryPrice > 0 ? snapshot.Account.EntryPrice : null;
        decimal? stopPrice = snapshot.Continuous?.ActualStopPrice ?? snapshot.Continuous?.EstimatedStopPrice ?? snapshot.Account.ActiveStopPrice;
        return new(snapshot.ModelId, snapshot.RequestKey, legend, hud, string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.QUnit"), RlContract.Gamma),
            Number(min), Number(max), min, max, snapshot.QValues[0], snapshot.QValues[1], snapshot.QValues[2],
            Action(RlAction.Hold) + " " + Number(snapshot.QValues[0]), Action(RlAction.Buy) + " " + Number(snapshot.QValues[1]),
            Action(RlAction.Sell) + " " + Number(snapshot.QValues[2]), snapshot.SelectedAction)
            { PolicyId = snapshot.PolicyId, ContinuousReadout = FormatContinuous(snapshot, localization),
                PolicyControlReadout = FormatPolicyControl(snapshot, localization),
                ScopeReadout = FormatScope(snapshot, localization),
                EvaluationReadout = (snapshot.AllBlockEvaluation is { } expanded ? FormatAllBlocks(expanded, localization) + Environment.NewLine : "")
                    + (snapshot.EvaluationConstraints is { } report ? FormatEvaluation(report, localization) : ""),
                AnchorDate = anchorDate, CurrentPrice = currentPrice, EntryPrice = entryPrice, StopPrice = stopPrice,
                PositionQuantity = snapshot.Account.Quantity, ActionLabel = Action(snapshot.SelectedAction) };
    }
    /// <summary>The scope line is omitted for <see cref="RlReplayScope.TrainingSource"/> so every existing display stays unchanged.</summary>
    private static string FormatScope(RlQSnapshot snapshot, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        var lines = new System.Collections.Generic.List<string>();
        string? scopeKey = snapshot.ReplayScope switch
        {
            RlReplayScope.SameInstrumentOverlapsTraining => "Rl.Scope.SameOverlaps",
            RlReplayScope.SameInstrumentAfterTraining => "Rl.Scope.SameAfter",
            RlReplayScope.SameInstrumentBeforeTraining => "Rl.Scope.SameBefore",
            RlReplayScope.OtherInstrument => "Rl.Scope.Other",
            _ => null,
        };
        if (snapshot.ReplayScope == RlReplayScope.SameInstrumentOverlapsTraining && snapshot.OverlappingParts != RlSourceParts.None)
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.Scope.SameOverlapsParts"), string.Join(", ", PartLabels(snapshot.OverlappingParts, localization))));
        else if (scopeKey is not null) lines.Add(localization.GetString(scopeKey));
        if (!snapshot.ScalerFindings.IsDefaultOrEmpty)
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.Scope.OutOfRange"),
                string.Join(", ", snapshot.ScalerFindings.Select(f => ChannelLabel(f.Channel)))));
        foreach (var finding in snapshot.ContinuityFindings.IsDefault ? ImmutableArray<RlContinuityFinding>.Empty : snapshot.ContinuityFindings)
            lines.Add(finding.Kind == RlContinuityKind.CalendarGap
                ? string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.Scope.ContinuityGap"),
                    finding.Observed.ToString("0", CultureInfo.CurrentCulture), Day(finding.Date), finding.TrainedMaximum.ToString("0", CultureInfo.CurrentCulture))
                : string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.Scope.ContinuityChange"),
                    finding.Observed.ToString("P1", CultureInfo.CurrentCulture), Day(finding.Date), finding.TrainedMaximum.ToString("P1", CultureInfo.CurrentCulture)));
        return string.Join(Environment.NewLine, lines);
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The names of the source parts a window overlaps, in chronological order (training, validation, final evaluation).</summary>
    private static System.Collections.Generic.IEnumerable<string> PartLabels(RlSourceParts parts, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        if (parts.HasFlag(RlSourceParts.Train)) yield return localization.GetString("Rl.Scope.Part.Train");
        if (parts.HasFlag(RlSourceParts.Validation)) yield return localization.GetString("Rl.Scope.Part.Validation");
        if (parts.HasFlag(RlSourceParts.FinalEvaluation)) yield return localization.GetString("Rl.Scope.Part.Final");
    }

    /// <summary>Scaler channel identities look like "lag0:0:price:Open"; the user sees the last part ("Open").</summary>
    private static string ChannelLabel(string identity)
    {
        int separator = identity.LastIndexOf(':');
        return separator >= 0 && separator < identity.Length - 1 ? identity[(separator + 1)..] : identity;
    }

    private static string FormatContinuous(RlQSnapshot snapshot, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        if (snapshot.Continuous is not { } readout) return "";
        string Optional(decimal? value) => value?.ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture) ?? localization.GetString("Rl.State.Unavailable");
        return string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.ContinuousReadout"),
            localization.GetString("Rl." + snapshot.SelectedAction),
            (readout.Action.SizeRatio * 100m).ToString("F" + RlContract.SizeDisplayDecimals, CultureInfo.CurrentCulture),
            (readout.Action.StopDistance * 100m).ToString("F" + RlContract.StopDisplayDecimals, CultureInfo.CurrentCulture),
            Optional(readout.EstimatedQuantity), Optional(readout.EstimatedStopPrice), Optional(readout.ActualStopPrice),
            localization.GetString("Rl.ControlState." + readout.State), readout.DirectionModelId) + " " + localization.GetString("Rl.ContinuousDisclosure")
            + " " + localization.GetString("Rl.Temporal." + (snapshot.TemporalSpecificationId is null ? "Legacy" : snapshot.TemporalStatus.ToString()));
    }

    private static string FormatPolicyControl(RlQSnapshot snapshot, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        if (snapshot.Continuous is not { } readout) return "";
        string Percentage(decimal? value, int decimals) => value.HasValue
            ? (value.Value * 100m).ToString("F" + decimals, CultureInfo.CurrentCulture) + "%" : localization.GetString("Rl.State.Unavailable");
        var control = readout.SelectedPolicyControl;
        return string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.PolicyControlReadout"), RlUiDisplay.Policy(snapshot.PolicyId, localization),
            localization.GetString("Rl." + (readout.PolicyAction ?? snapshot.SelectedAction)),
            Percentage(control?.SizeRatio, RlContract.SizeDisplayDecimals), Percentage(control?.StopDistance, RlContract.StopDisplayDecimals),
            localization.GetString(control is null ? "Rl.State.Unavailable"
                : control.RetainedFromActualEntry ? "Rl.Control.Retained" : "Rl.Control.Prospective"),
            readout.EstimatedQuantity?.ToString(CultureInfo.CurrentCulture) ?? localization.GetString("Rl.State.Unavailable"),
            readout.EstimatedStopPrice?.ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture) ?? localization.GetString("Rl.State.Unavailable"),
            readout.ActualStopPrice?.ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture) ?? localization.GetString("Rl.State.Unavailable"),
            localization.GetString("Rl.ControlState." + readout.State))
            + " " + localization.GetString("Rl.ControlMeaning");
    }

    private static string FormatDistribution(string label, RlReturnDistribution values, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
        => string.Format(CultureInfo.CurrentCulture,
            localization.GetString("Rl.AllBlockDistribution"), label, values.Count,
            values.Count == 0 ? "—" : (values.Mean * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture),
            values.Count == 0 ? "—" : (values.Median * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture),
            values.Count == 0 ? "—" : (values.Minimum * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture),
            values.Count == 0 ? "—" : (values.Maximum * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture),
            values.Count == 0 ? "—" : (values.PositiveRate * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture));

    public static string FormatShortHistory(RlShortHistoryEvaluationReport report, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        var lines = new System.Collections.Generic.List<string> { localization.GetString("Rl.ShortHistoryMode"),
            string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.ShortHistoryCounts"),
                report.BlockCount.Train, report.BlockCount.Validation, report.BlockCount.Oos),
            FormatDistribution(localization.GetString("Rl.ValidationActor"), report.Validation, localization) };
        if (report.Oos is { } oos) lines.Add(FormatDistribution(localization.GetString("Rl.OosActor"), oos, localization));
        lines.Add(localization.GetString("Rl.ShortHistoryDisclosure"));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatAllBlocks(RlAllBlockEvaluationReport report, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        string Distribution(string label, RlReturnDistribution values) => FormatDistribution(label, values, localization);
        var lines = new System.Collections.Generic.List<string> { localization.GetString("Rl.AllBlockMode"),
            Distribution(localization.GetString("Rl.ValidationActor"), report.Validation),
            Distribution(localization.GetString("Rl.PairedValidation"), report.PairedValidationDelta) };
        if (report.Oos is { } oos) lines.Add(Distribution(localization.GetString("Rl.OosActor"), oos));
        if (report.PairedOosDelta is { } delta) lines.Add(Distribution(localization.GetString("Rl.PairedOos"), delta));
        lines.Add(localization.GetString("Rl.AllBlockDisclosure"));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatEvaluation(RlConstraintReport report, StockAnalyzer.Core.Interfaces.ILocalizationService localization)
    {
        string Entries(string policy, RlEntryConstraintSummary s) => string.Format(CultureInfo.CurrentCulture,
            localization.GetString("Rl.ConstraintCounts"), policy, s.EligibleBuys, s.ZeroQuantity, s.QuantityCapExceeded,
            s.InsufficientFunds, s.ActualEntries, localization.GetString("Rl.ConstraintStatus." + s.Status));
        var lines = new System.Collections.Generic.List<string>
        {
            Entries(localization.GetString("Rl.ValidationActor"), report.Validation),
            Entries(localization.GetString("Rl.OosActor"), report.Oos),
        };
        foreach (var policy in report.BaselinePolicies)
            lines.Add(Entries(RlUiDisplay.Partition(policy.Partition, localization) + "/" + RlUiDisplay.Policy(policy.PolicyId, localization), policy.Entries));
        foreach (var partition in report.Coverage.Partitions)
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.CoveragePartition"), RlUiDisplay.Partition(partition.Partition, localization),
                partition.SelectedBlocks, partition.PotentialCompleteBlocks, partition.EvaluatedBlocks, partition.EvaluatedDecisions,
                partition.UnusedCompleteBlocks, partition.UnusedRemainderBars));
        lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.CoveragePeriod"),
            report.Coverage.SupportStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), report.Coverage.SupportEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            report.Coverage.PossibleDecisionStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), report.Coverage.PossibleDecisionEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        foreach (var partition in report.PairedDeltas.GroupBy(d => d.Partition))
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.PairedDelta"), RlUiDisplay.Partition(partition.Key, localization),
                (partition.Sum(d => d.Delta) / partition.Count() * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture)));
        foreach (var mark in report.TerminalMarks)
            lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.TerminalMark"), mark.BlockId, mark.ResidualQuantity,
                (mark.MarkReturn * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture),
                (mark.CloseMarkMaximumDrawdown * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture),
                (mark.AdjustedReturn * 100m).ToString("F" + RlContract.DisplayDecimals, CultureInfo.CurrentCulture)));
        lines.Add(string.Format(CultureInfo.CurrentCulture, localization.GetString("Rl.EvaluationEvidence"), RlUiDisplay.Metric(report.SelectionMetric, localization),
            report.LearningGamma, report.HorizonBars, localization.GetString("Rl.Temporal." + report.TemporalDependencyEvidence)));
        lines.Add(localization.GetString("Rl.ConstraintDisclosure"));
        return string.Join(Environment.NewLine, lines);
    }
}
