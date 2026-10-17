using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Services.StoredPeriod;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// The pre-filled data period of the Training Wizard: when the window has resolved its ticked lists, and again when the lists, the timeframe or an input of the
/// required bar count changes, the pickers the user has not touched are written from the stored data (end = the latest stored date, start = the date from which
/// the bars the current settings require are available for every selected symbol). A touched picker is never overwritten. Without the advisor both stay empty
/// (empty means all stored data, as before).
/// </summary>
public partial class TrainingWizardViewModel
{
    private IStoredPeriodAdvisor? _periodAdvisor;
    private TimeProvider _periodClock = TimeProvider.System;
    private bool _startIsAuto = true, _endIsAuto = true, _applyingPeriod, _periodDisposed;
    private long _periodRevision;
    private CancellationTokenSource? _periodCts;
    private StoredPeriodProposal? _appliedPeriod;

    private void ConfigurePeriod(IStoredPeriodAdvisor? periodAdvisor, TimeProvider? clock)
    {
        _periodAdvisor = periodAdvisor;
        _periodClock = clock ?? TimeProvider.System;
    }

    /// <summary>One line under the data period, only while a selected symbol has fewer bars than the current settings require (the start is still automatic).</summary>
    public string PeriodStatusDisplay => _appliedPeriod is { Status: StoredPeriodStatus.InsufficientHistory } proposal && _startIsAuto && !IsTraining && !IsDeploying
        ? string.Format(CultureInfo.CurrentCulture, LocalizationManager.Instance["TrainingWizard_Period_Insufficient"], proposal.RequiredBars, proposal.AvailableBars,
            proposal.ShortSymbols.FirstOrDefault() ?? "")
        : "";
    public bool HasPeriodStatus => PeriodStatusDisplay.Length > 0;

    private void NotifyPeriodStatus() { OnPropertyChanged(nameof(PeriodStatusDisplay)); OnPropertyChanged(nameof(HasPeriodStatus)); NotifySufficiency(); }   // the requirement warning yields to this line

    private void PeriodInputChanged(string? name)
    {
        switch (name)
        {
            case nameof(StartDate):
                if (_applyingPeriod) return;
                _startIsAuto = false; NotifyPeriodStatus();               // a picked, typed or cleared start is never proposed over again
                return;
            case nameof(EndDate):
                if (_applyingPeriod) return;
                _endIsAuto = false; SchedulePeriod(immediate: false);     // the start follows the typed end
                return;
            case nameof(SelectedTimeframe) or nameof(WindowSize) or nameof(Horizon) or nameof(NSplits) or nameof(Gap) or nameof(LagsText)
                or nameof(UseFixedZScore) or nameof(UseCalibration) or nameof(SelectedEnsembleMethod) or nameof(SelectedStackingWeightSource):
                SchedulePeriod(immediate: false);
                return;
        }
    }

    /// <summary>Starts one recompute (the previous one is cancelled and dropped). Does nothing without the advisor, after disposal, or while a run or a deployment is active.</summary>
    private void SchedulePeriod(bool immediate)
    {
        if (_periodAdvisor is null || _periodDisposed || IsTraining || IsDeploying) return;
        if (!_startIsAuto && !_endIsAuto) return;                          // nothing could be written
        CancelPeriod();
        var cancel = new CancellationTokenSource();
        _periodCts = cancel;
        long revision = Interlocked.Increment(ref _periodRevision);
        _ = RefreshPeriodAsync(revision, cancel, immediate);
    }

    private async Task RefreshPeriodAsync(long revision, CancellationTokenSource cancel, bool immediate)
    {
        var token = cancel.Token;
        try
        {
            if (!immediate) await Task.Delay(StoredPeriodTiming.Debounce, _periodClock, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (!TryMinimumRawBars(out int required)) return;               // a transient invalid input (window = 0 while typing) changes nothing
            var symbols = ResolveSymbols();
            DateOnly? bound = !_endIsAuto && EndDate is { } typedEnd ? DateOnly.FromDateTime(typedEnd.Date) : null;
            StoredPeriodProposal proposal;
            try { proposal = await _periodAdvisor!.ProposeForTrainingAsync(symbols, SelectedTimeframe, required, bound, token).ConfigureAwait(true); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogWarning(error, "TrainingWizardViewModel: the stored period could not be proposed.");
                proposal = StoredPeriodProposal.None(StoredPeriodStatus.NoProposal, required);
            }
            token.ThrowIfCancellationRequested();
            ApplyProposal(revision, proposal);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_periodCts, cancel)) _periodCts = null; cancel.Dispose(); }
    }

    /// <summary>Writes the proposal into the pickers that are still automatic; a stale revision, a busy or a disposed window writes nothing.</summary>
    private void ApplyProposal(long revision, StoredPeriodProposal proposal)
    {
        if (_periodDisposed || revision != Interlocked.Read(ref _periodRevision) || IsTraining || IsDeploying) return;
        if (proposal.Status == StoredPeriodStatus.NoProposal) return;
        _applyingPeriod = true;
        try
        {
            if (proposal.Status is StoredPeriodStatus.Found or StoredPeriodStatus.InsufficientHistory)
            {
                if (_startIsAuto && proposal.Start is { } start) StartDate = start.ToDateTime(TimeOnly.MinValue);
                if (_endIsAuto && proposal.Latest is { } latest) EndDate = latest.ToDateTime(TimeOnly.MinValue);
            }
            else
            {
                if (_startIsAuto) StartDate = null;
                if (_endIsAuto) EndDate = null;
            }
        }
        finally { _applyingPeriod = false; }
        _appliedPeriod = proposal;
        NotifyPeriodStatus();
    }

    /// <summary>Cancels the running recompute; the source may already be disposed by the recompute that just finished.</summary>
    private void CancelPeriod()
    {
        try { _periodCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void DisposePeriod() { _periodDisposed = true; CancelPeriod(); CancelSufficiency(); }
}
