using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Prepares an immutable local stored-price snapshot with explicitly unknown historical evidence.</summary>
internal static class RlLocalSnapshotFactory
{
    public static async Task<RlSourceSnapshot> PrepareAsync(RlTrainingJob job, string directory,
        TrainingResourceLimits limits, CancellationToken ct)
    {
        await WriteSourceFilesAsync(job.Symbol, job.StartDate, job.EndDateExclusive, directory, limits, ct).ConfigureAwait(false);
        return RlSourceSnapshot.Load(directory, limits, job.SmokeOnly, ct, job.EvaluationSpecificationId, job.ShortHistorySpecificationId);
    }

    /// <summary>
    /// Captures the existing local files of one symbol and writes the immutable source files and manifest of the period, without
    /// building an episode catalog (a short history is a valid source here). Shared by the single-symbol and the portfolio snapshots.
    /// </summary>
    internal static async Task WriteSourceFilesAsync(string symbol, DateOnly startDate, DateOnly endDateExclusive, string directory,
        TrainingResourceLimits limits, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var bytes = await RlLocalSourceFiles.CaptureAsync(symbol, directory, limits, ct).ConfigureAwait(false);
        var metadata = RlLocalParquetReader.ReadMetadata(Path.Combine(directory, RlFiles.MetadataParquet));
        string Require(string name) => metadata.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"Unavailable: local metadata {name} is missing; no symbol inference is permitted.");
        string currency = Require("currency"), type = Require("quote_type"), timezone = Require("exchange_timezone_name");
        if (type != "EQUITY") throw new InvalidDataException("Unavailable: T15 requires an existing local equity series.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timezone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new InvalidDataException("Unavailable: stored exchange timezone is unsupported.", ex); }
        var capturedDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(started, zone).DateTime);
        var ingress = RlLocalParquetReader.Read(Path.Combine(directory, RlFiles.RawParquet), startDate, endDateExclusive, limits, ct);
        if (DateOnly.FromDateTime(ingress.Bars[^1].Timestamp) >= capturedDate)
            throw new InvalidDataException("Unavailable: selected stored bars include the current or a future exchange date.");
        string barsPath = Path.Combine(directory, RlFiles.Bars), actionsPath = Path.Combine(directory, RlFiles.Actions);
        await WriteAsync(barsPath, ingress.Bars, ct).ConfigureAwait(false);
        await WriteAsync(actionsPath, ImmutableArray<RlCorporateAction>.Empty, ct).ConfigureAwait(false);
        var manifest = new RlSourceManifest(RlSourceContract.SchemaVersion, RlSourceContract.Provider, RlSourceContract.ProviderVersion,
            symbol, currency, timezone, started, DateTimeOffset.UtcNow,
            ContentHash.OfFile(barsPath), ContentHash.OfFile(actionsPath), ingress.Bars.Length, RlSourceContract.Adjustment,
            RlSourceContract.LocalProvenance, RlSourceContract.Unverified, ingress.StorageDtype,
            RlSourceContract.FinalityPolicy, capturedDate, startDate, endDateExclusive,
            RlSourceContract.Acquisition.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)))
        {
            InstrumentType = type, MappedSymbol = bytes.MappedSymbol, SourcePathIdentity = bytes.SourceIdentity,
            SourceFileSha256 = bytes.SourceSha256, MetadataPathIdentity = bytes.MetadataIdentity,
            MetadataFileSha256 = bytes.MetadataSha256, SourceRows = ingress.SourceRows,
            EvidencePolicy = RlSourceContract.EvidencePolicy, OriginalAcquisitionStatus = RlSourceContract.Unverified,
            CorporateActionCoverageStatus = RlSourceContract.Unverified, CurrentHistoryRevision = false,
            DateLabelSemantics = RlSourceContract.DateLabelSemantics,
            FirstDate = DateOnly.FromDateTime(ingress.Bars[0].Timestamp), LastDate = DateOnly.FromDateTime(ingress.Bars[^1].Timestamp),
            ActionsPeriodStart = null, ActionsPeriodEnd = null, KnownImputedDates = ImmutableArray<DateOnly>.Empty,
        };
        await WriteAsync(Path.Combine(directory, RlFiles.SourceManifest), manifest, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private static Task WriteAsync<T>(string path, T value, CancellationToken ct) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, RlFiles.Json), TrainingConfigJson.Utf8NoBom, ct);
}
