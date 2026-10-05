using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>One channel of a Shapley decomposition; the bar scale is shared by the rows of one output.</summary>
public sealed record ContributionDisplayRow(string Channel, string Display, double PositiveMagnitude,
    double NegativeMagnitude, double ScaleMaximum);

public sealed record ExplainOutputDisplay(string Title, IReadOnlyList<ContributionDisplayRow> Rows);

public sealed record ExplainCaseDisplay(string Header, IReadOnlyList<ExplainOutputDisplay> Outputs);

public sealed record PartialDependenceDisplayRow(string Text);

/// <summary>
/// Presents the generation-bound offline explanation (Shapley contributions, partial dependence) and the probability
/// calibration of the selected model. It only reads immutable sidecars through <see cref="IModelExplainabilityService"/>;
/// nothing is computed here, in prediction or in rendering.
/// </summary>
public sealed partial class ModelExplainabilityViewModel : ObservableObject, IDisposable
{
    private readonly IModelExplainabilityService? _service;
    private readonly IDispatcherService? _dispatcher;
    private readonly ILogger _logger;
    private long _revision;
    private volatile bool _disposed;

    public ModelExplainabilityViewModel(IModelExplainabilityService? service = null,
        IDispatcherService? dispatcher = null, ILogger? logger = null)
    {
        _service = service;
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger.Instance;
    }

    public ObservableCollection<ExplainCaseDisplay> Cases { get; } = new();
    public ObservableCollection<PartialDependenceDisplayRow> PartialDependenceRows { get; } = new();

    [ObservableProperty] private string? _explanationStatus;
    [ObservableProperty] private string? _representativeNote;
    [ObservableProperty] private string? _contributionNote;
    [ObservableProperty] private string? _stabilityNote;
    [ObservableProperty] private string? _partialDependenceNote;
    [ObservableProperty] private string? _provenance;
    [ObservableProperty] private string? _calibrationStatus;
    [ObservableProperty] private string? _calibrationDetail;
    [ObservableProperty] private string? _calibrationWarning;
    [ObservableProperty] private bool _isLoading;

    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    private static string Text(string key) => LocalizationManager.Instance.Get(key);

    /// <summary>Selects a generation (or none). A result that arrives for a superseded selection is dropped.</summary>
    public void Select(string? modelId)
    {
        long revision = Interlocked.Increment(ref _revision);
        Cases.Clear(); PartialDependenceRows.Clear();
        ExplanationStatus = RepresentativeNote = ContributionNote = StabilityNote = PartialDependenceNote = Provenance = null;
        CalibrationStatus = CalibrationDetail = CalibrationWarning = null;
        IsLoading = modelId is not null && _service is not null;
        LoadTask = modelId is null || _service is null ? Task.CompletedTask : LoadAsync(modelId, revision);
    }

    private async Task LoadAsync(string modelId, long revision)
    {
        ModelExplanationLoad? explanation = null;
        ModelCalibrationLoad? calibration = null;
        try { explanation = await _service!.LoadExplanationAsync(modelId).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // A load that threw is a broken sidecar or registry, not a model without an explanation.
            _logger.LogWarning(ex, "Selected model explanation could not be loaded.");
            explanation = new ModelExplanationLoad(ExplanationAvailability.Invalid, null);
        }
        try { calibration = await _service!.LoadCalibrationAsync(modelId).ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Not "no calibration": a declared calibration that cannot be read is a fault of the generation.
            _logger.LogWarning(ex, "Selected model calibration could not be loaded.");
            calibration = new ModelCalibrationLoad(CalibrationAvailability.LoadFailed);
        }
        void Publish()
        {
            if (_disposed || revision != Interlocked.Read(ref _revision)) return;
            IsLoading = false;
            PublishExplanation(modelId, explanation);
            PublishCalibration(calibration);
        }
        if (_dispatcher is null) Publish(); else _dispatcher.Post(Publish);
    }

    private void PublishExplanation(string modelId, ModelExplanationLoad? load)
    {
        if (load is null || load.Availability == ExplanationAvailability.NotGenerated)
        {
            ExplanationStatus = Text("Explainability_Unavailable_NotGenerated");
            return;
        }
        if (load.Availability == ExplanationAvailability.Failed)
        {
            ExplanationStatus = Text("Explainability_Unavailable_Failed");
            return;
        }
        if (load.Availability == ExplanationAvailability.Invalid || load.Explanation is null)
        {
            ExplanationStatus = Text("Explainability_Unavailable_Invalid");
            return;
        }
        var explanation = load.Explanation;
        if (!explanation.IsAvailable)
        {
            ExplanationStatus = Text("Explainability_Status_" + explanation.Status);
            return;
        }
        RepresentativeNote = Text("Explainability_RepresentativeNote");
        ContributionNote = Text("Explainability_ShapNote");
        PartialDependenceNote = Text("Explainability_PdpNote");
        if (explanation.Stability is { } stability)
            StabilityNote = string.Format(CultureInfo.CurrentCulture, Text("Explainability_StabilityNote"),
                stability.MaxStandardError, stability.MaxHalfDelta, stability.PermutationsUsed);
        Provenance = string.Format(CultureInfo.CurrentCulture, Text("Explainability_Provenance"),
            modelId, explanation.DataRevision, explanation.BackgroundSize, explanation.Permutations, explanation.ModelCalls);
        foreach (var record in explanation.Records)
        {
            var outputs = record.Outputs.Select(output =>
            {
                var top = output.Top();
                double scale = top.Length == 0 ? 1 : Math.Max(top.Max(c => Math.Abs(c.Value)), double.Epsilon);
                var rows = top.Select(c => new ContributionDisplayRow(c.Channel, ContributionText(c),
                    Math.Max(0, c.Value), Math.Max(0, -c.Value), scale)).ToArray();
                return new ExplainOutputDisplay(string.Format(CultureInfo.CurrentCulture, Text("Explainability_Output"),
                    output.OutputId, output.BaseValue, output.Prediction), rows);
            }).ToArray();
            Cases.Add(new ExplainCaseDisplay(string.Format(CultureInfo.CurrentCulture, Text("Explainability_Case"),
                record.Sample.Symbol, record.Sample.AnchorDate, string.Join(", ", record.Reasons.Select(ReasonText))), outputs));
        }
        foreach (var channel in explanation.Pdp!.Channels)
        {
            string ranges = string.Join(" · ", channel.Values.OrderBy(v => explanation.Classes.IndexOf(v.Key))
                .Select(v => string.Format(CultureInfo.CurrentCulture, Text("Explainability_PdpRange"),
                    v.Key, v.Value.Min(), v.Value.Max())));
            string row = string.Format(CultureInfo.CurrentCulture,
                Text("Explainability_PdpRow"), channel.Channel, channel.Offsets.Min(), channel.Offsets.Max(), ranges);
            if (!channel.OutOfSupportRate.IsDefaultOrEmpty)
                row += string.Format(CultureInfo.CurrentCulture, Text("Explainability_PdpOutOfSupport"),
                    channel.OutOfSupportRate.Max());
            PartialDependenceRows.Add(new PartialDependenceDisplayRow(row));
        }
    }

    private static string ContributionText(ExplanationContribution c) => c.StandardError is { } error
        ? string.Format(CultureInfo.CurrentCulture, Text("Explainability_ContributionWithError"), c.Value, error)
        : c.Value.ToString("+0.0000;-0.0000;0.0000", CultureInfo.CurrentCulture);

    private static string ReasonText(string reason) => reason == ModelExplanationContract.ReasonLatest
        ? Text("Explainability_Reason_latest")
        : reason.StartsWith(ModelExplanationContract.ReasonTopPrefix, StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, Text("Explainability_Reason_top"), reason[ModelExplanationContract.ReasonTopPrefix.Length..])
            : reason;

    private void PublishCalibration(ModelCalibrationLoad? load)
    {
        switch (load?.Availability)
        {
            case CalibrationAvailability.Active when load.Calibration is { } c:
                CalibrationStatus = string.Format(CultureInfo.CurrentCulture, Text("Calibration_Active"), c.Temperature);
                var test = c.Evaluation.OuterTest;
                CalibrationDetail = string.Format(CultureInfo.CurrentCulture, Text("Calibration_Detail"),
                    c.Fit.SampleCount, test.SampleCount, test.Before.Nll, test.After.Nll, test.Before.Brier,
                    test.After.Brier, test.Before.Ece, test.After.Ece);
                if (test.Before.EceEqualMass is { } before && test.After.EceEqualMass is { } after)
                    CalibrationDetail += string.Format(CultureInfo.CurrentCulture, Text("Calibration_DetailEqualMass"), before, after);
                if (!ModelCalibrationLimits.IsWithinStandardBand(c.Temperature))
                    CalibrationWarning = string.Format(CultureInfo.CurrentCulture, Text("Calibration_OutsideStandard"),
                        ModelCalibrationLimits.TemperatureStandardMin, ModelCalibrationLimits.TemperatureStandardMax);
                break;
            case CalibrationAvailability.Disabled when load.Calibration is { } boundary:
                CalibrationStatus = string.Format(CultureInfo.CurrentCulture, Text("Calibration_DisabledBoundary"), boundary.Temperature);
                break;
            case CalibrationAvailability.Unavailable:
                CalibrationStatus = string.Format(CultureInfo.CurrentCulture, Text("Calibration_RequestedUnavailable"), load!.Reason);
                break;
            case CalibrationAvailability.LoadFailed:
                CalibrationStatus = Text("Calibration_LoadFailed");
                break;
            default:
                CalibrationStatus = Text("Calibration_None");
                break;
        }
    }

    public void Dispose() => _disposed = true;
}
