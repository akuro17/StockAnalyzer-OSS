using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>A stored run that passed admission as the base of a transfer: its files, identities and the period of its data.</summary>
public sealed record RlPortfolioBaseRun(RlPortfolioArtifactEntry Entry, string ModelPath, string ScalerPath, string NativePath,
    string ModelSha256, string ScalerSha256, string NativeSha256, int MaxSymbols, DateOnly PeriodStartInclusive, DateOnly PeriodEndExclusive)
{
    public Guid RunId => Entry.RunId;
    public string UniverseSha256 => Entry.Manifest!.UniverseSha256;
    public string CalendarSha256 => Entry.Manifest!.CalendarSha256;
}

/// <summary>
/// Admission of a stored run as the base of a transfer. The checks run in a fixed order and the first failure wins; every rejection is an
/// <see cref="InvalidDataException"/> that starts with <see cref="RlPortfolioTransferContract.ErrorPrefix"/>. The time rule (the base data must end
/// no later than the first validation date of the target) is strict and has no override: a base that has seen the target's validation or test period
/// would leak the future into the evidence.
/// </summary>
public static class RlPortfolioTransferAdmission
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The first validation date of a target panel: the calendar date at the first index of its validation partition (train cut plus one observation
    /// window), the earliest date any validation block can begin, in both partition modes.
    /// </summary>
    public static DateOnly FirstValidationDate(RlPanelSnapshot panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return FirstValidationDate(panel.Catalog, panel.Calendar) ?? throw Reject("the target panel has no validation partition.");
    }

    /// <summary>The same rule on a catalog and its calendar (the dialog's preflight has both before any panel is captured); null when the calendar holds no such index.</summary>
    public static DateOnly? FirstValidationDate(RlPanelCatalog catalog, IReadOnlyList<DateTime> calendar)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(calendar);
        int index = catalog.TrainCut + RlContract.WindowLength;
        return index >= 0 && index < calendar.Count ? DateOnly.FromDateTime(calendar[index]) : null;
    }

    /// <param name="layout">The observation variant of the target run (default v1): a fine-tune keeps the encoder input of its base, so a base of another variant is rejected.</param>
    public static RlPortfolioBaseRun Admit(RlPortfolioArtifactStore store, Guid baseRunId, DateOnly targetFirstValidationDate,
        RlPortfolioObservationLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        // 1. A verified run of the store.
        var entry = store.Find(baseRunId);
        if (entry is null || entry.Status != RlPortfolioArtifactStatus.Verified || entry.Manifest is null) throw Reject("the base run is not a verified run.");
        // 2. The three model files, each already hash-verified by the listing.
        foreach (string name in RlPortfolioBaseRuns.RequiredFiles)
            if (!entry.Manifest.Files.ContainsKey(name)) throw Reject($"the base run lacks {name}.");
        string InRun(string name) => Path.Combine(entry.Directory, name);
        string modelPath = InRun(RlFiles.Model), scalerPath = InRun(RlFiles.Model + RlFiles.ScalerSuffix), nativePath = InRun(RlFiles.Native);
        // 3. The ONNX metadata (contract hash, target type) through the strict actor boundary.
        int maxSymbols;
        try
        {
            using var model = new RlPortfolioActorModel(modelPath);
            maxSymbols = model.MaxSymbols;
            if (model.Layout != (layout ?? RlPortfolioObservationLayout.V1)) throw Reject("the base run belongs to a different portfolio contract.");
        }
        catch (InvalidDataException) { throw Reject("the base run belongs to a different portfolio contract."); }
        catch (OnnxRuntimeException) { throw Reject("the base model cannot be loaded."); }
        // 4. The period of the base data, from the stored panel manifests that carry the run's hashes.
        var period = RlPortfolioBaseRuns.Period(store, entry.Manifest.UniverseSha256, entry.Manifest.CalendarSha256);
        if (period.Status == RlBasePeriodStatus.Unknown) throw Reject("the base panel is unknown, its period cannot be proven.");
        if (period.Status == RlBasePeriodStatus.Ambiguous) throw Reject("the base panel period is ambiguous.");
        // 5. The time rule: bars below the exclusive end only, so an end equal to the first validation date passes.
        if (period.PeriodEndExclusive!.Value > targetFirstValidationDate)
            throw Reject($"the base data reaches {period.PeriodEndExclusive.Value.ToString(DateFormat, CultureInfo.InvariantCulture)}, "
                + $"the target validation starts {targetFirstValidationDate.ToString(DateFormat, CultureInfo.InvariantCulture)}.");
        // 6. The slot count of the target is no constraint here (the actor weights do not depend on it); the transfer service checks the slots it needs.
        return new RlPortfolioBaseRun(entry, modelPath, scalerPath, nativePath, entry.Manifest.Files[RlFiles.Model],
            entry.Manifest.Files[RlFiles.Model + RlFiles.ScalerSuffix], entry.Manifest.Files[RlFiles.Native], maxSymbols,
            period.PeriodStart!.Value, period.PeriodEndExclusive.Value);
    }

    private static InvalidDataException Reject(string detail) => RlPortfolioTransferRejection.Create($"{RlPortfolioTransferContract.ErrorPrefix} {detail}");
}

/// <summary>
/// Marks an admission or slot-count rejection of a transfer. Such a rejection is raised before any run folder exists, so the caller that captured the target
/// panel for this attempt may remove it (<see cref="RlPortfolioArtifactStore.TryRemoveCapturedPanel"/>); any other <see cref="InvalidDataException"/> may come after
/// a run folder was begun and does not qualify. <see cref="InvalidDataException"/> is sealed, so the mark is an entry of <see cref="Exception.Data"/>: the type and
/// the message stay those of a plain rejection.
/// </summary>
internal static class RlPortfolioTransferRejection
{
    private const string MarkKey = "PortfolioTransfer.RejectedBeforeAnyRun";

    public static InvalidDataException Create(string message)
    {
        var error = new InvalidDataException(message);
        error.Data[MarkKey] = true;
        return error;
    }

    public static bool Is(Exception error) => error is InvalidDataException && error.Data[MarkKey] is true;
}
