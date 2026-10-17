using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.StoredPeriod;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// The two warnings under the data period, shown before Start: (1) tickers with fewer stored bars in the period than the current settings require, and
/// (2) a selection whose sample estimate is above the training sample cap. They only inform: no picker is written and Start is never blocked. The figures come
/// from <see cref="TrainingDataSufficiency"/> over the bar counts the stored-period advisor reads; this file only schedules the read and words the result.
/// </summary>
public partial class TrainingWizardViewModel
{
    /// <summary>The cap used when the saved resource limits cannot be resolved: nothing is over it, so no sample warning is made.</summary>
    private const int NoSampleCap = int.MaxValue;

    private ITrainingResourceSettings? _resourceSettings;
    // The debounce runs on the system clock on purpose: the period's injected clock is a manual clock in tests, and a second armed timer on it would disturb the period's own schedule.
    private long _sufficiencyRevision;
    private CancellationTokenSource? _sufficiencyCts;
    private SufficiencySnapshot? _sufficiency;

    /// <summary>Tickers that have too few bars in the period; empty while the existing one-line period status speaks, during a run, and when nothing is short.</summary>
    public string RequirementWarningDisplay => BuildRequirementWarning();
    public bool HasRequirementWarning => RequirementWarningDisplay.Length > 0;

    /// <summary>The sample estimate against the cap; empty during a run and when the estimate fits.</summary>
    public string SampleLimitWarningDisplay => BuildSampleLimitWarning();
    public bool HasSampleLimitWarning => SampleLimitWarningDisplay.Length > 0;

    private void ConfigureSufficiency(ITrainingResourceSettings? resourceSettings) => _resourceSettings = resourceSettings;

    private void NotifySufficiency()
    {
        OnPropertyChanged(nameof(RequirementWarningDisplay)); OnPropertyChanged(nameof(HasRequirementWarning));
        OnPropertyChanged(nameof(SampleLimitWarningDisplay)); OnPropertyChanged(nameof(HasSampleLimitWarning));
    }

    private void SufficiencyInputChanged(string? name)
    {
        if (name is nameof(StartDate) or nameof(EndDate) or nameof(SelectedTimeframe) or nameof(WindowSize) or nameof(Horizon) or nameof(NSplits) or nameof(Gap)
            or nameof(LagsText) or nameof(UseFixedZScore) or nameof(UseCalibration) or nameof(UseComposedFeatures) or nameof(SelectedEnsembleMethod)
            or nameof(SelectedStackingWeightSource) or nameof(IsTraining) or nameof(IsDeploying))
            ScheduleSufficiency();
    }

    /// <summary>Starts one recompute (the previous one is cancelled and dropped). Without the advisor, after disposal, or during a run the warnings are empty.</summary>
    private void ScheduleSufficiency()
    {
        if (_periodAdvisor is null || _periodDisposed) return;
        CancelSufficiency();
        if (IsTraining || IsDeploying) { SetSufficiency(null); return; }
        var cancel = new CancellationTokenSource();
        _sufficiencyCts = cancel;
        long revision = Interlocked.Increment(ref _sufficiencyRevision);
        _ = RefreshSufficiencyAsync(revision, cancel);
    }

    private async Task RefreshSufficiencyAsync(long revision, CancellationTokenSource cancel)
    {
        var token = cancel.Token;
        try
        {
            await Task.Delay(StoredPeriodTiming.Debounce, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!TryMinimumRawBars(out int required)) { Publish(revision, null); return; }
            var symbols = ResolveSymbols();
            if (symbols.Length == 0) { Publish(revision, null); return; }

            DateOnly? start = StartDate is { } s ? DateOnly.FromDateTime(s.Date) : null;
            DateOnly? end = EndDate is { } e ? DateOnly.FromDateTime(e.Date) : null;
            int window = WindowSize, horizon = Horizon, splits = CurrentSplitCount(), maxLag = CurrentMaxLag();
            bool retained = UseComposedFeatures || UseFixedZScore;
            int gap = WalkForwardDataRequirement.ResolveGap(window, horizon, Gap is >= 0 ? Gap : null);

            var infos = await _periodAdvisor!.ReadSymbolBarsForTrainingAsync(symbols, SelectedTimeframe, required, start, end, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (infos.Count == 0) { Publish(revision, null); return; }

            int cap = ResolveSampleCap();
            var result = TrainingDataSufficiency.Evaluate(infos, required, window, horizon, maxLag, splits, retained, cap);
            Publish(revision, new SufficiencySnapshot(result, required, window, horizon, splits, gap, cap, end));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            // The warnings only inform: whatever fails (the bar read, the resource limits, the arithmetic) is logged and the warnings disappear, never an old figure.
            _logger.LogWarning(error, "TrainingWizardViewModel: the sufficiency warnings could not be computed.");
            Publish(revision, null);
        }
        finally { if (ReferenceEquals(_sufficiencyCts, cancel)) _sufficiencyCts = null; cancel.Dispose(); }
    }

    /// <summary>The cap the run will use (the call <c>TrainingOrchestrator</c> makes), or <see cref="NoSampleCap"/> when the saved limits cannot be resolved.</summary>
    private int ResolveSampleCap()
    {
        if (_resourceSettings?.LoadError is not null) return NoSampleCap;
        try { return TrainingResourceGuard.Resolve(_resourceSettings?.Snapshot ?? new TrainingResourceOverrides()).MaxSamples; }
        catch (InvalidOperationException) { return NoSampleCap; }
    }

    private void Publish(long revision, SufficiencySnapshot? snapshot)
    {
        if (_periodDisposed || revision != Interlocked.Read(ref _sufficiencyRevision)) return;
        SetSufficiency(snapshot);
    }

    private void SetSufficiency(SufficiencySnapshot? snapshot)
    {
        _sufficiency = snapshot;
        NotifySufficiency();
    }

    private void CancelSufficiency()
    {
        try { _sufficiencyCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private string BuildRequirementWarning() =>
        _sufficiency is not { } snapshot || snapshot.Result.ShortSymbols.Count == 0 || IsTraining || IsDeploying || PeriodStatusDisplay.Length > 0
            ? "" : TrainingSufficiencyDisplay.RequirementWarning(snapshot);

    private string BuildSampleLimitWarning() =>
        _sufficiency is not { } snapshot || !snapshot.Result.OverLimit || IsTraining || IsDeploying
            ? "" : TrainingSufficiencyDisplay.SampleLimitWarning(snapshot);
}
