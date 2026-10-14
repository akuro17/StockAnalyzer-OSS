using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.StoredPeriod;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.Core.Services.StoredPeriod;

namespace StockAnalyzer.Avalonia.ViewModels;

/// <summary>
/// The pre-filled start and end date of the RL window: when the window opens, and again when the symbol or an option that changes the required bars changes,
/// the boxes the user has not typed are written from the stored daily data (end = the day after the latest final bar, because the end is exclusive;
/// start = the date from which the required bars are available). A typed box is never overwritten. The proposal never changes the start gate: it reads only the boxes.
/// </summary>
public sealed partial class RlTrainingDialogViewModel
{
    private const string PeriodDateFormat = "yyyy-MM-dd";
    private IStoredPeriodAdvisor? _periodAdvisor;
    private TimeProvider _periodClock = TimeProvider.System;
    private bool _startIsAuto = true, _endIsAuto = true, _applyingProposal;
    private long _periodRevision;
    private CancellationTokenSource? _periodCts;
    private StoredPeriodProposal? _appliedProposal;
    private readonly Lazy<int> _standardRequiredBars = new(() => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.Standard));
    private readonly Lazy<int> _shortHistoryRequiredBars = new(() => RlPanelCatalogBuilder.MinimumCalendarLength(RlPanelEvaluationMode.ShortHistory));

    private void ConfigurePeriod(IStoredPeriodAdvisor? periodAdvisor, TimeProvider? clock)
    {
        _periodAdvisor = periodAdvisor;
        _periodClock = clock ?? TimeProvider.System;
    }

    /// <summary>Bars the start date has to leave: the catalog minimum of the evaluation the options select (the trial run is checked conservatively like the short history).</summary>
    public int RequiredBars()
    {
        bool shortHistory = SmokeOnly || (UseContinuousPolicy ? UseAllCompleteBlocks : UseShortHistory);
        return shortHistory ? _shortHistoryRequiredBars.Value : _standardRequiredBars.Value;
    }

    /// <summary>One line under the start date, only while the stored history is shorter than the bars required (the start date is still automatic).</summary>
    public string PeriodStatusDisplay => _appliedProposal is { Status: StoredPeriodStatus.InsufficientHistory } proposal && _startIsAuto && Editable
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Rl.Ui.Period.Insufficient"), proposal.RequiredBars, proposal.AvailableBars)
        : "";
    public bool HasPeriodStatus => PeriodStatusDisplay.Length > 0;

    private void PeriodInputChanged(string? name)
    {
        switch (name)
        {
            case nameof(StartDate):
                if (_applyingProposal) return;
                _startIsAuto = false; NotifyPeriodStatus();               // a typed start (or a cleared box) is never proposed over again
                return;
            case nameof(EndDateExclusive):
                if (_applyingProposal) return;
                _endIsAuto = false; SchedulePeriod(immediate: false);     // the start follows the typed end
                return;
            case nameof(Symbol) or nameof(SmokeOnly) or nameof(UseShortHistory) or nameof(UseContinuousPolicy) or nameof(UseAllCompleteBlocks):
                SchedulePeriod(immediate: false);
                return;
        }
    }

    private void NotifyPeriodStatus() { OnPropertyChanged(nameof(PeriodStatusDisplay)); OnPropertyChanged(nameof(HasPeriodStatus)); }

    /// <summary>Starts one recompute (the previous one is cancelled and dropped). Does nothing without the advisor, after disposal, or while an operation runs.</summary>
    private void SchedulePeriod(bool immediate)
    {
        if (_periodAdvisor is null || _disposed || !Editable) return;
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
            string symbol = Symbol.Trim();
            DateOnly? bound = !_endIsAuto && DateOnly.TryParseExact(EndDateExclusive, PeriodDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var typedEnd)
                ? typedEnd.AddDays(-1) : null;
            StoredPeriodProposal proposal;
            try { proposal = await _periodAdvisor!.ProposeForRlAsync(symbol, RequiredBars(), bound, token).ConfigureAwait(true); }
            catch (Exception error) when (error is not OperationCanceledException) { proposal = StoredPeriodProposal.None(StoredPeriodStatus.NoProposal); }
            token.ThrowIfCancellationRequested();
            await PublishAsync(() => ApplyProposal(revision, proposal)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_periodCts, cancel)) _periodCts = null; cancel.Dispose(); }
    }

    /// <summary>Writes the proposal into the boxes that are still automatic; a stale revision, a disposed or busy window writes nothing.</summary>
    private void ApplyProposal(long revision, StoredPeriodProposal proposal)
    {
        if (_disposed || revision != Interlocked.Read(ref _periodRevision) || !Editable) return;
        if (proposal.Status == StoredPeriodStatus.NoProposal) return;     // a transient invalid input must not blank the boxes
        _applyingProposal = true;
        try
        {
            if (proposal.Status is StoredPeriodStatus.Found or StoredPeriodStatus.InsufficientHistory)
            {
                if (_startIsAuto && proposal.Start is { } start) StartDate = start.ToString(PeriodDateFormat, CultureInfo.InvariantCulture);
                if (_endIsAuto && proposal.Latest is { } latest) EndDateExclusive = latest.AddDays(1).ToString(PeriodDateFormat, CultureInfo.InvariantCulture);
            }
            else
            {
                if (_startIsAuto) StartDate = "";
                if (_endIsAuto) EndDateExclusive = "";
            }
        }
        finally { _applyingProposal = false; }
        _appliedProposal = proposal;
        NotifyPeriodStatus();
    }

    /// <summary>Cancels the running recompute; the source may already be disposed by the recompute that just finished.</summary>
    private void CancelPeriod()
    {
        try { _periodCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void DisposePeriod() => CancelPeriod();
}
