using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// Prepares an immutable panel snapshot from existing local Data/Daily and Data/Metadata files: no network, no repair, no imputation.
/// Each symbol is captured and validated by the same shared functions as the single-symbol snapshot; the panel manifest is written last
/// and the finished directory is re-validated by <see cref="RlPanelSnapshot.Load"/>. A failed or cancelled preparation removes the directory it created.
/// </summary>
public static class RlPanelSnapshotFactory
{
    public static Task<RlPanelSnapshot> PrepareAsync(IReadOnlyList<string> symbols, DateOnly startDate, DateOnly endDateExclusive,
        string directory, TrainingResourceLimits limits, RlPortfolioSettings settings, bool holdoutEnabled,
        RlPanelEvaluationMode mode = RlPanelEvaluationMode.Standard, CancellationToken ct = default, RlLocalSourceFolders? folders = null)
        => PrepareAsync(symbols, startDate, endDateExclusive, directory, limits, settings, holdoutEnabled, mode, null, ct, folders);

    /// <summary>The preparation with a partition plan (short-history only); a null plan is the behaviour of the evaluation mode.</summary>
    public static async Task<RlPanelSnapshot> PrepareAsync(IReadOnlyList<string> symbols, DateOnly startDate, DateOnly endDateExclusive,
        string directory, TrainingResourceLimits limits, RlPortfolioSettings settings, bool holdoutEnabled,
        RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken ct = default, RlLocalSourceFolders? folders = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (folders is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(folders.Daily);
            ArgumentException.ThrowIfNullOrWhiteSpace(folders.Metadata);
        }
        limits.Validate();
        _ = RlPortfolioSettingsValidator.Validate(settings, limits);
        var canonical = RlPanelUniverse.Canonicalize(symbols, holdoutEnabled, settings.MaxSymbols);
        if (startDate >= endDateExclusive)
            throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} the source date interval must be nonempty.", nameof(endDateExclusive));
        directory = Path.GetFullPath(directory);
        if (System.IO.Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} the panel directory must not exist yet.", nameof(directory));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var sources = new List<RlSourceSnapshot.RlValidatedSource>(canonical.Length);
            foreach (string symbol in canonical)
            {
                ct.ThrowIfCancellationRequested();
                string folder = RlPanelSnapshot.SymbolDirectory(directory, symbol);
                try
                {
                    await RlLocalSnapshotFactory.WriteSourceFilesAsync(symbol, startDate, endDateExclusive, folder, limits, ct, folders).ConfigureAwait(false);
                    sources.Add(RlSourceSnapshot.ValidateSourceFiles(folder, limits, ct));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw RlPanelSnapshot.Reject($"{symbol}: {error.Message}", error);
                }
            }
            var calendar = RlPanelSnapshot.BuildCalendar(sources.Select(s => s.Bars));
            var entries = canonical.Select((symbol, i) => new RlPanelSymbolEntry(symbol, sources[i].Revision, sources[i].Bars.Length,
                sources[i].Manifest.FirstDate!.Value, sources[i].Manifest.LastDate!.Value)).ToImmutableArray();
            string membership = RlPanelUniverse.MembershipSha256(canonical, entries.Select(e => e.SourceManifestSha256).ToArray());
            uint seed = RlPanelUniverse.SplitSeed(RlPanelUniverse.SymbolSetSha256(canonical));
            var holdoutSymbols = holdoutEnabled
                ? canonical.Zip(RlPanelUniverse.AssignHoldout(canonical, seed, settings.HoldoutPercent), (symbol, held) => (symbol, held))
                    .Where(p => p.held).Select(p => p.symbol).ToImmutableArray()
                : ImmutableArray<string>.Empty;
            var first = sources[0].Manifest;
            var manifest = new RlPanelManifest(RlPortfolioContract.PanelSchemaVersion, RlPortfolioContract.SpecificationId, entries,
                first.QuoteCurrency, first.ExchangeTimezone, first.CapturedExchangeDate, startDate, endDateExclusive,
                RlPanelUniverse.CalendarSha256(calendar), calendar.Length, membership, seed, holdoutEnabled, settings.HoldoutPercent, holdoutSymbols,
                RlPanelUniverse.UniverseSha256(membership, seed, holdoutEnabled, settings.HoldoutPercent, holdoutSymbols));
            await AtomicJsonFile.SaveAsync(Path.Combine(directory, RlPortfolioContract.PanelManifest), manifest, RlFiles.Json).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return RlPanelSnapshot.Load(directory, limits, settings, mode, plan, ct);
        }
        catch
        {
            // Best-effort cleanup of the half-built panel folder: a folder that cannot be removed must not hide the original error rethrown below.
            try { System.IO.Directory.Delete(directory, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
