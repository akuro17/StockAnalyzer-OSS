using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>A panel an earlier run stored (the folder name is the panel id a new run may reuse): what the dialog shows to choose it.</summary>
public sealed record RlPortfolioStoredPanelInfo(Guid PanelId, DateOnly CapturedExchangeDate, DateOnly PeriodStartInclusive, DateOnly PeriodEndExclusive,
    int SymbolCount, int HoldoutCount, string UniverseSha256, string CalendarSha256);

/// <summary>
/// A verified run with an evaluation report, as the comparison entry lists it. <see cref="Kind"/> is
/// <see cref="RlPortfolioTransferContract.FineTuneKind"/> or <see cref="RlPortfolioTransferContract.ZeroShotKind"/> when the run published a lineage,
/// <see cref="ScratchKind"/> when it trained from scratch. Two runs can be paired only when their universe and calendar hashes agree.
/// </summary>
public sealed record RlPortfolioComparableRun(Guid RunId, DateTimeOffset CreatedUtc, string Kind, string UniverseSha256, string CalendarSha256)
{
    /// <summary>The kind of a run without a lineage: trained from scratch.</summary>
    public const string ScratchKind = "scratch";
}

/// <summary>Read-only listings of the artifact store for the dialog: stored panels and the runs whose evaluation reports can be compared. Nothing is repaired or deleted.</summary>
public static class RlPortfolioStoredRuns
{
    /// <summary>Every panel folder that holds a readable manifest, newest capture first (then by period end, then by id).</summary>
    public static ImmutableArray<RlPortfolioStoredPanelInfo> ListPanels(RlPortfolioArtifactStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!Directory.Exists(store.PanelsRoot)) return ImmutableArray<RlPortfolioStoredPanelInfo>.Empty;
        var panels = ImmutableArray.CreateBuilder<RlPortfolioStoredPanelInfo>();
        foreach (string directory in Directory.EnumerateDirectories(store.PanelsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out Guid id)) continue;
            if (RlPortfolioBaseRuns.ReadPanelManifest(directory) is not { } manifest || manifest.Symbols.IsDefault) continue;
            panels.Add(new RlPortfolioStoredPanelInfo(id, manifest.CapturedExchangeDate, manifest.PeriodStartInclusive, manifest.PeriodEndExclusive,
                manifest.Symbols.Length, manifest.HoldoutSymbols.IsDefault ? 0 : manifest.HoldoutSymbols.Length, manifest.UniverseSha256, manifest.CalendarSha256));
        }
        return panels.OrderByDescending(p => p.CapturedExchangeDate).ThenByDescending(p => p.PeriodEndExclusive).ThenBy(p => p.PanelId).ToImmutableArray();
    }

    /// <summary>
    /// Verified runs that published an evaluation report, as the store lists them. A run whose lineage cannot be read is left out: its kind would be a guess.
    /// </summary>
    public static ImmutableArray<RlPortfolioComparableRun> ListComparableRuns(RlPortfolioArtifactStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var runs = ImmutableArray.CreateBuilder<RlPortfolioComparableRun>();
        foreach (var entry in store.List(cancellationToken))
        {
            if (entry.Status != RlPortfolioArtifactStatus.Verified || entry.Manifest is not { } manifest
                || !manifest.Files.ContainsKey(RlPortfolioEvaluationContract.ReportFile)) continue;
            string kind = RlPortfolioComparableRun.ScratchKind;
            if (manifest.Files.ContainsKey(RlPortfolioTransferContract.LineageFile))
            {
                try { kind = RlPortfolioTransferEvidence.Read(Path.Combine(entry.Directory, RlPortfolioTransferContract.LineageFile)).Kind; }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException) { continue; }
            }
            runs.Add(new RlPortfolioComparableRun(entry.RunId, manifest.CreatedUtc, kind, manifest.UniverseSha256, manifest.CalendarSha256));
        }
        return runs.ToImmutable();
    }

    /// <summary>
    /// The comparison of the evaluation reports of two stored runs (A minus B). Both runs must be verified and hold a report; every other precondition is the one of
    /// <see cref="RlPortfolioTransferComparison.Compare"/>, whose <see cref="InvalidDataException"/> names the field that differs.
    /// </summary>
    public static RlPortfolioTransferComparisonResult Compare(RlPortfolioArtifactStore store, Guid runA, Guid runB)
    {
        ArgumentNullException.ThrowIfNull(store);
        RlPortfolioEvaluationReport Report(Guid id)
        {
            var entry = store.Find(id);
            if (entry is not { Status: RlPortfolioArtifactStatus.Verified, Manifest: { } manifest } || !manifest.Files.ContainsKey(RlPortfolioEvaluationContract.ReportFile))
                throw new InvalidDataException($"{RlPortfolioTransferContract.ErrorPrefix} run {id:N} is not a verified run with an evaluation report.");
            return RlPortfolioEvaluationReports.Read(Path.Combine(entry.Directory, RlPortfolioEvaluationContract.ReportFile));
        }
        return RlPortfolioTransferComparison.Compare(Report(runA), Report(runB));
    }
}
