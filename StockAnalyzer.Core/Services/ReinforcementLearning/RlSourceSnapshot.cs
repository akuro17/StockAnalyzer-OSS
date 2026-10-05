using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Validated immutable current-history snapshot. UTC midnight is a date label, never execution evidence.</summary>
public sealed class RlSourceSnapshot
{
    public string Directory { get; }
    public RlSourceManifest Manifest { get; }
    public ImmutableArray<CandleData> Bars { get; }
    public ImmutableArray<RlCorporateAction> Actions { get; }
    public string Revision { get; }
    public RlEpisodeCatalog Catalog { get; }
    public bool ExpandedEvaluation => RlAllBlockEvaluationContract.IsExpanded(Catalog.EvaluationSpecificationId);

    private RlSourceSnapshot(string directory, RlSourceManifest manifest,
        ImmutableArray<CandleData> bars, ImmutableArray<RlCorporateAction> actions, string revision, bool smoke, string? evaluationSpecification)
    {
        Directory = directory; Manifest = manifest; Bars = bars; Actions = actions; Revision = revision;
        Catalog = BuildCatalog(bars, actions, manifest.KnownImputedDates, smoke, evaluationSpecification);
    }

    public static RlSourceSnapshot Load(string directory, TrainingResourceLimits limits, bool smokeOnly = false, CancellationToken cancellationToken = default,
        string? evaluationSpecificationId = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        directory = Path.GetFullPath(directory);
        long byteBound = Math.Min(RlContract.BufferByteLimit, checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB));
        void RequireBoundedFile(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > byteBound)
                throw new InvalidDataException("Linked or oversized immutable evaluation artifact is prohibited.");
        }
        string? specification = evaluationSpecificationId;
        _ = RlAllBlockEvaluationContract.IsExpanded(specification);
        string jobPath = Path.Combine(directory, RlFiles.Job), catalogPath = Path.Combine(directory, RlFiles.Catalog);
        if (File.Exists(jobPath))
        {
            RequireBoundedFile(jobPath);
            using var job = JsonDocument.Parse(File.ReadAllText(jobPath));
            string? saved = RlAllBlockEvaluationContract.Read(job.RootElement);
            if (specification is not null && saved != specification) throw new InvalidDataException("Requested and saved evaluation specifications differ.");
            specification = saved;
        }
        if (File.Exists(catalogPath))
        {
            RequireBoundedFile(catalogPath);
            using var catalog = JsonDocument.Parse(File.ReadAllText(catalogPath));
            string? saved = RlAllBlockEvaluationContract.Read(catalog.RootElement);
            if (!File.Exists(jobPath) && specification is null) specification = saved;
            if (saved != specification || (saved is null && catalog.RootElement.TryGetProperty("schema_version", out _))
                || (saved is not null && catalog.RootElement.GetProperty("schema_version").GetInt32() != RlAllBlockEvaluationContract.SchemaVersion))
                throw new InvalidDataException("Evaluation catalog version was downgraded or changed.");
        }
        var manifestPath = Path.Combine(directory, RlFiles.SourceManifest);
        if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifestPath).Length > byteBound)
            throw new InvalidDataException("Linked or oversized immutable source artifact is prohibited.");
        var manifest = JsonSerializer.Deserialize<RlSourceManifest>(File.ReadAllText(manifestPath), RlFiles.Json)
            ?? throw new InvalidDataException("RL source manifest is absent.");
        if (manifest.SchemaVersion != RlSourceContract.SchemaVersion || manifest.Provider != RlFiles.LocalProvider
            || manifest.ProviderVersion != RlFiles.LocalProviderVersion || manifest.Adjustment != RlSourceContract.Adjustment
            || string.IsNullOrWhiteSpace(manifest.Symbol) || string.IsNullOrWhiteSpace(manifest.QuoteCurrency)
            || string.IsNullOrWhiteSpace(manifest.ExchangeTimezone)
            || manifest.InstrumentType != "EQUITY" || string.IsNullOrWhiteSpace(manifest.SourceStorageDtype)
            || string.IsNullOrWhiteSpace(manifest.MappedSymbol) || string.IsNullOrWhiteSpace(manifest.SourcePathIdentity)
            || !ContentHash.IsLowerHex64(manifest.SourceFileSha256) || !ContentHash.IsLowerHex64(manifest.MetadataFileSha256)
            || manifest.CurrentHistoryRevision || manifest.DateLabelSemantics != RlSourceContract.DateLabelSemantics
            || manifest.EvidencePolicy != RlSourceContract.EvidencePolicy
            || manifest.OriginalAcquisitionStatus != RlSourceContract.Unverified
            || manifest.CorporateActionCoverageStatus != RlSourceContract.Unverified
            || manifest.ActionsPeriodStart is not null || manifest.ActionsPeriodEnd is not null
            || manifest.RetrievalStartedUtc > manifest.RetrievalCompletedUtc
            || manifest.PeriodStartInclusive >= manifest.PeriodEndExclusive
            || manifest.FinalityPolicy != RlSourceContract.FinalityPolicy
            || manifest.AcquisitionProvenanceStatus != RlSourceContract.LocalProvenance
            || manifest.IngestionImputationStatus != RlSourceContract.Unverified
            || manifest.KnownImputedDates.IsDefault || !manifest.KnownImputedDates.IsEmpty)
            throw new InvalidDataException("RL source provenance/adjustment/availability contract is unsupported.");
        if (manifest.MappedSymbol.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || manifest.MappedSymbol.Contains('/') || manifest.MappedSymbol.Contains('\\')
            || manifest.SourcePathIdentity != Path.GetFileName(MarketDataSettings.DefaultDailyDataPath) + "/" + manifest.MappedSymbol + ".parquet"
            || manifest.MetadataPathIdentity != Path.GetFileName(MarketDataSettings.DefaultMetadataDataPath) + "/" + manifest.MappedSymbol + ".meta.parquet")
            throw new InvalidDataException("Source path identities must describe the approved local Daily and Metadata files.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(manifest.ExchangeTimezone);
        if (manifest.CapturedExchangeDate != DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(manifest.RetrievalStartedUtc, zone).DateTime))
            throw new InvalidDataException("Captured exchange date differs from immutable snapshot-start metadata.");
        RlSourceContract.Validate(manifest.AcquisitionParameters);
        var barsPath = Path.Combine(directory, RlFiles.Bars);
        var actionsPath = Path.Combine(directory, RlFiles.Actions);
        var rawPath = Path.Combine(directory, RlFiles.RawParquet);
        var metadataPath = Path.Combine(directory, RlFiles.MetadataParquet);
        foreach (string path in new[] { manifestPath, barsPath, actionsPath, rawPath, metadataPath })
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > byteBound)
                throw new InvalidDataException("Linked or oversized immutable source artifact is prohibited.");
        }
        if (ContentHash.OfFile(barsPath) != manifest.PreparedFileSha256
            || ContentHash.OfFile(actionsPath) != manifest.ActionsFileSha256
            || ContentHash.OfFile(rawPath) != manifest.SourceFileSha256 || ContentHash.OfFile(metadataPath) != manifest.MetadataFileSha256)
            throw new InvalidDataException("Source snapshot hash mismatch.");
        var bars = JsonSerializer.Deserialize<ImmutableArray<CandleData>>(File.ReadAllText(barsPath), RlFiles.Json);
        var actions = JsonSerializer.Deserialize<ImmutableArray<RlCorporateAction>>(File.ReadAllText(actionsPath), RlFiles.Json);
        if (bars.IsDefaultOrEmpty || bars.Length != manifest.RowCount || bars.Length > limits.MaxSamples || actions.IsDefault || !actions.IsEmpty)
            throw new InvalidDataException("Source arrays/count/resource bounds are invalid.");
        if (manifest.FirstDate != DateOnly.FromDateTime(bars[0].Timestamp) || manifest.LastDate != DateOnly.FromDateTime(bars[^1].Timestamp))
            throw new InvalidDataException("Source declared first/last dates differ from immutable prepared bars.");
        for (int i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            var date = DateOnly.FromDateTime(bar.Timestamp);
            if (bar.Timestamp.Kind != DateTimeKind.Utc || bar.Timestamp.TimeOfDay != TimeSpan.Zero
                || !bar.IsValid() || bar.Open <= 0 || bar.High <= 0 || bar.Low <= 0 || bar.Close <= 0
                || date >= manifest.CapturedExchangeDate || date < manifest.PeriodStartInclusive
                || date >= manifest.PeriodEndExclusive || (i > 0 && bar.Timestamp <= bars[i - 1].Timestamp))
                throw new InvalidDataException($"Invalid or unavailable source bar {i}; no repair is permitted.");
        }
        var storedMetadata = RlLocalParquetReader.ReadMetadata(metadataPath);
        if (storedMetadata["currency"] != manifest.QuoteCurrency || storedMetadata["quote_type"] != manifest.InstrumentType
            || storedMetadata["exchange_timezone_name"] != manifest.ExchangeTimezone)
            throw new InvalidDataException("Declared local metadata differs from the copied stored values.");
        var stored = RlLocalParquetReader.Read(rawPath, manifest.PeriodStartInclusive, manifest.PeriodEndExclusive, limits, cancellationToken);
        if (stored.SourceRows != manifest.SourceRows || stored.StorageDtype != manifest.SourceStorageDtype || !stored.Bars.SequenceEqual(bars))
            throw new InvalidDataException("Prepared decimal bars or precision differ from the copied local Parquet ingress.");
        return new RlSourceSnapshot(directory, manifest, bars, actions, ContentHash.OfFile(manifestPath), smokeOnly, specification);
    }

    public BacktestInput CreateInput(RlEpisodeBlock block)
    {
        if (block is null || !Catalog.Train.Concat(Catalog.Validation).Concat(Catalog.Oos).Contains(block))
            throw new ArgumentException("Episode must belong to the fixed source catalog.", nameof(block));
        var bars = Bars.AsSpan(block.StartIndex, RlContract.SupportLength).ToArray().ToImmutableArray();
        return new BacktestInput(bars, Manifest.Symbol, TimeFrame.D1, BacktestInput.CurrentDataVersion,
            bars[RlContract.WindowLength - 1].Timestamp, bars[^1].Timestamp, 0, RlContract.WindowLength - 1);
    }

    internal static RlEpisodeCatalog BuildCatalog(ImmutableArray<CandleData> bars,
        ImmutableArray<RlCorporateAction> actions, ImmutableArray<DateOnly> imputedDates, bool smoke, string? evaluationSpecification = null)
    {
        int cut1 = checked((int)((long)RlContract.TrainPartitionNumerator * bars.Length / RlContract.PartitionDenominator));
        int cut2 = checked((int)((long)RlContract.ValidationPartitionNumerator * bars.Length / RlContract.PartitionDenominator));
        var excluded = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<RlEpisodeBlock> Blocks(string name, int start, int end)
        {
            var blocks = ImmutableArray.CreateBuilder<RlEpisodeBlock>();
            for (int b = start; b + RlContract.SupportLength <= end; b += RlContract.SupportLength)
            {
                string id = $"{name}:{b}:{b + RlContract.SupportLength}";
                DateOnly first = DateOnly.FromDateTime(bars[b].Timestamp);
                DateOnly last = DateOnly.FromDateTime(bars[b + RlContract.SupportLength - 1].Timestamp);
                if (actions.Any(a => a.Kind == "split" && a.Date >= first && a.Date <= last))
                    excluded.Add(id + ":UnsupportedSplit");
                else if (imputedDates.Any(date => date >= first && date <= last))
                    excluded.Add(id + ":KnownImputedBar");
                else blocks.Add(new RlEpisodeBlock(id, name, b, b + RlContract.SupportLength));
            }
            return blocks.ToImmutable();
        }
        var train = Blocks("train", 0, cut1);
        var validation = Blocks("validation", cut1 + RlContract.WindowLength, cut2);
        var oos = Blocks("oos", cut2 + RlContract.WindowLength, bars.Length);
        bool expanded = RlAllBlockEvaluationContract.IsExpanded(evaluationSpecification);
        int needed = smoke || expanded ? 1 : RlContract.EvaluationEpisodeCount;
        if (train.IsEmpty || validation.Length < needed || (!smoke && oos.Length < needed))
            throw new InvalidDataException("InsufficientData: complete independent RL episode support is unavailable.");
        return new RlEpisodeCatalog(train, expanded ? validation : validation.Take(needed).ToImmutableArray(),
            smoke ? ImmutableArray<RlEpisodeBlock>.Empty : expanded ? oos : oos.Take(needed).ToImmutableArray(), excluded.ToImmutable(), cut1, cut2)
            { EvaluationSpecificationId = evaluationSpecification, SchemaVersion = expanded ? RlAllBlockEvaluationContract.SchemaVersion : null };
    }
}
