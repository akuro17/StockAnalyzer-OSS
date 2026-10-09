using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>How the period of the data a stored run was trained on could be established.</summary>
public enum RlBasePeriodStatus
{
    /// <summary>Exactly one period is on record for the run's universe and calendar hashes.</summary>
    Known = 0,
    /// <summary>No panel manifest with the run's hashes is stored, so its data period cannot be proven.</summary>
    Unknown = 1,
    /// <summary>Panel manifests with the run's hashes disagree on the period.</summary>
    Ambiguous = 2,
}

/// <param name="PeriodStart">Null unless <see cref="RlBasePeriodStatus.Known"/>.</param>
public sealed record RlBasePeriod(RlBasePeriodStatus Status, DateOnly? PeriodStart, DateOnly? PeriodEndExclusive);

/// <summary>A verified run that can serve as a base model, with the period of its data when that is known (the dialog lists these).</summary>
public sealed record RlPortfolioBaseRunInfo(Guid RunId, DateTimeOffset CreatedUtc, string UniverseSha256, string CalendarSha256, RlBasePeriod Period);

/// <summary>
/// Verified runs of the artifact store that hold a complete base model, and the period of the data they were trained on. A run folder records only
/// the universe and calendar hashes of its panel; the period is read from the immutable panel manifests kept beside it (manifest only, no bars). Read-only.
/// </summary>
public static class RlPortfolioBaseRuns
{
    /// <summary>Files a run must hold to serve as a base model: the ONNX actor, its scaler sidecar and the native weights.</summary>
    public static readonly ImmutableArray<string> RequiredFiles = ImmutableArray.Create(
        RlFiles.Model, RlFiles.Model + RlFiles.ScalerSuffix, RlFiles.Native);

    /// <summary>True when the manifest of a verified run lists every file of <see cref="RequiredFiles"/>.</summary>
    public static bool HoldsModel(RlPortfolioArtifactEntry entry)
        => entry.Status == RlPortfolioArtifactStatus.Verified && entry.Manifest is not null && RequiredFiles.All(entry.Manifest.Files.ContainsKey);

    /// <summary>Verified runs with a complete model, as listed by the store (verified runs newest first).</summary>
    public static ImmutableArray<RlPortfolioBaseRunInfo> List(RlPortfolioArtifactStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var panels = ReadPanels(store, cancellationToken);
        return store.List(cancellationToken).Where(HoldsModel).Select(e => new RlPortfolioBaseRunInfo(e.RunId, e.Manifest!.CreatedUtc,
            e.Manifest.UniverseSha256, e.Manifest.CalendarSha256, PeriodOf(panels, e.Manifest.UniverseSha256, e.Manifest.CalendarSha256))).ToImmutableArray();
    }

    /// <summary>
    /// The period of the data behind the hashes: the stored panel manifests whose universe AND calendar hashes both equal them (both hashes bind the
    /// symbols and the dates). All matches must agree on the period, otherwise it is ambiguous.
    /// </summary>
    public static RlBasePeriod Period(RlPortfolioArtifactStore store, string universeSha256, string calendarSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return PeriodOf(ReadPanels(store, cancellationToken), universeSha256, calendarSha256);
    }

    /// <summary>
    /// The stored panel manifest behind the hashes when it is unambiguous: every stored manifest whose universe AND calendar hashes both equal them
    /// agrees on the period (equal hashes bind the symbols and the dates). Null when none is stored or the periods disagree.
    /// </summary>
    public static RlPanelManifest? PanelManifest(RlPortfolioArtifactStore store, string universeSha256, string calendarSha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var matches = ReadPanels(store, cancellationToken).Where(m => m.UniverseSha256 == universeSha256 && m.CalendarSha256 == calendarSha256).ToArray();
        return matches.Select(m => (m.PeriodStartInclusive, m.PeriodEndExclusive)).Distinct().Count() == 1 ? matches[0] : null;
    }

    /// <summary>The manifest of one panel folder, read like the listing reads them; null when the folder holds none that can be read (never repaired).</summary>
    public static RlPanelManifest? ReadPanelManifest(string panelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelDirectory);
        return TryReadManifest(panelDirectory);
    }

    private static RlBasePeriod PeriodOf(IReadOnlyList<RlPanelManifest> panels, string universeSha256, string calendarSha256)
    {
        var matches = panels.Where(m => m.UniverseSha256 == universeSha256 && m.CalendarSha256 == calendarSha256)
            .Select(m => (m.PeriodStartInclusive, m.PeriodEndExclusive)).Distinct().ToArray();
        return matches.Length switch
        {
            0 => new RlBasePeriod(RlBasePeriodStatus.Unknown, null, null),
            1 => new RlBasePeriod(RlBasePeriodStatus.Known, matches[0].PeriodStartInclusive, matches[0].PeriodEndExclusive),
            _ => new RlBasePeriod(RlBasePeriodStatus.Ambiguous, null, null),
        };
    }

    /// <summary>Every parsable panel manifest below the store's panel folder; a folder that cannot be read or parsed is skipped, never repaired.</summary>
    private static List<RlPanelManifest> ReadPanels(RlPortfolioArtifactStore store, CancellationToken cancellationToken)
    {
        var manifests = new List<RlPanelManifest>();
        if (!Directory.Exists(store.PanelsRoot)) return manifests;
        foreach (string directory in Directory.EnumerateDirectories(store.PanelsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadManifest(directory) is { } manifest) manifests.Add(manifest);
        }
        return manifests;
    }

    private static RlPanelManifest? TryReadManifest(string directory)
    {
        string path = Path.Combine(directory, RlPortfolioContract.PanelManifest);
        try
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > RlContract.IpcFrameByteLimit) return null;
            var manifest = JsonSerializer.Deserialize<RlPanelManifest>(File.ReadAllText(path), RlFiles.Json);
            return manifest is not null && !string.IsNullOrEmpty(manifest.UniverseSha256) && !string.IsNullOrEmpty(manifest.CalendarSha256) ? manifest : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}
