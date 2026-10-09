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
/// The lineage of a run that used a stored model (<c>transfer.json</c>): which base run and which exact files, the period of the base data, the first
/// validation date of the target and, for a fine-tune, the freezing. <c>Freeze</c> is null for a zero-shot evaluation.
/// </summary>
public sealed record RlPortfolioTransferDocument(int Schema, string SpecificationId, string Kind, Guid BaseRunId, string BaseModelSha256,
    string BaseNativeSha256, string BaseScalerSha256, string BaseUniverseSha256, string BaseCalendarSha256, DateOnly BasePeriodEndExclusive,
    DateOnly FirstValidationDate, string? Freeze, string ContractHash, string Statement);

public static class RlPortfolioTransferEvidence
{
    /// <summary>The lineage a run writes for its admitted base. <paramref name="freeze"/> must be null for the zero-shot kind and a freeze name for a fine-tune.</summary>
    public static RlPortfolioTransferDocument Create(string kind, RlPortfolioBaseRun baseRun, DateOnly firstValidationDate, string? freeze = null)
    {
        ArgumentNullException.ThrowIfNull(baseRun);
        var document = new RlPortfolioTransferDocument(RlPortfolioTransferContract.SchemaVersion, RlPortfolioTransferContract.SpecificationId, kind,
            baseRun.RunId, baseRun.ModelSha256, baseRun.NativeSha256, baseRun.ScalerSha256, baseRun.UniverseSha256, baseRun.CalendarSha256,
            baseRun.PeriodEndExclusive, firstValidationDate, freeze, RlPortfolioTransferContract.ContractHash, RlPortfolioTransferContract.Statement);
        Validate(document, baseRun, firstValidationDate);
        return document;
    }

    public static Task SaveAsync(string path, RlPortfolioTransferDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        return AtomicJsonFile.SaveAsync(path, document, RlFiles.Json);
    }

    /// <summary>Reads a stored lineage: linked or oversized files and malformed JSON are rejected; the content is still unvalidated.</summary>
    public static RlPortfolioTransferDocument Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > RlPortfolioEvaluationContract.MaximumReportBytes)
            throw new InvalidDataException("A linked or oversized transfer lineage is prohibited.");
        try
        {
            return JsonSerializer.Deserialize<RlPortfolioTransferDocument>(File.ReadAllText(path), RlFiles.Json)
                ?? throw new InvalidDataException("The transfer lineage is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("The transfer lineage is malformed.", error); }
    }

    /// <summary>
    /// Rejects a lineage that is not exactly what the transfer protocol writes for this base run and target: wrong schema, specification, contract
    /// hash, statement or kind, a freeze that does not fit the kind, or any identity that differs from the admitted base run.
    /// </summary>
    public static void Validate(RlPortfolioTransferDocument document, RlPortfolioBaseRun baseRun, DateOnly firstValidationDate)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(baseRun);
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidDataException($"{RlPortfolioTransferContract.ErrorPrefix} the lineage {detail}.");
        }
        Require(document.Schema == RlPortfolioTransferContract.SchemaVersion, "has an unsupported schema version");
        Require(document.SpecificationId == RlPortfolioTransferContract.SpecificationId, "belongs to a different specification");
        Require(document.ContractHash == RlPortfolioTransferContract.ContractHash, "was written under a different transfer contract");
        Require(document.Statement == RlPortfolioTransferContract.Statement, "does not carry the mandatory limitation statement");
        bool fineTune = document.Kind == RlPortfolioTransferContract.FineTuneKind;
        Require(fineTune || document.Kind == RlPortfolioTransferContract.ZeroShotKind, "names an unknown kind");
        Require(fineTune ? (document.Freeze is RlPortfolioTransferContract.EncoderFrozen or RlPortfolioTransferContract.NoFreeze) : (document.Freeze is null),
            "states a freeze that does not fit its kind");
        Require(document.BaseRunId == baseRun.RunId, "names a different base run");
        Require(document.BaseModelSha256 == baseRun.ModelSha256 && document.BaseNativeSha256 == baseRun.NativeSha256 && document.BaseScalerSha256 == baseRun.ScalerSha256,
            "names different base files");
        Require(document.BaseUniverseSha256 == baseRun.UniverseSha256 && document.BaseCalendarSha256 == baseRun.CalendarSha256, "names a different base panel");
        Require(document.BasePeriodEndExclusive == baseRun.PeriodEndExclusive, "states a different base period end");
        Require(document.FirstValidationDate == firstValidationDate, "states a different first validation date");
        Require(document.BasePeriodEndExclusive <= document.FirstValidationDate, "breaks the time rule (the base data reaches the target validation)");
    }
}
