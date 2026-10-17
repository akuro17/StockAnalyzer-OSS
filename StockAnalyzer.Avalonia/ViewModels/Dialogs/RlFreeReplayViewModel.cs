using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Models;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>One entry of the list chooser; a null <see cref="Id"/> stands for all tickers.</summary>
public sealed record RlTickerListOption(Guid? Id, string Name);

/// <summary>
/// The "any ticker and date" card of the RL Playback page: replays the selected discrete agent on the stored daily bars of a ticker the user
/// picks, from an anchor date and a decision position. It holds no persisted state. The page view model feeds it the selected agent.
/// </summary>
public sealed partial class RlFreeReplayViewModel : ViewModelBase, IDisposable
{
    private const string DecisionField = nameof(Decision);
    private const string RejectKeyPrefix = "Settings_RlPlayback_Free_Reject_";
    private const string DateFormat = "yyyy-MM-dd";

    private readonly RlDialogDataService _data;
    private readonly IRlReplayTarget _target;
    private readonly IMarketDataProvider _market;
    private readonly IRlTickerListSource _lists;
    private readonly ILocalizationService _localization;
    private readonly IDispatcherService _dispatcher;
    private readonly HashSet<string> _invalidEditors = new(StringComparer.Ordinal);
    private Dictionary<string, string> _available = new(StringComparer.OrdinalIgnoreCase);
    private RlGeneration? _generation;
    private RlSourceInfo? _sourceInfo;
    private RlAnchorRange? _range;
    private RlExternalReplayException? _rangeRejection;
    private CancellationTokenSource? _rangeCts, _replayCts, _sourceCts;
    private long _modelRevision, _rangeRevision;
    private bool _settingDate, _rebuilding, _disposed, _tickersLoaded, _rangePending;


    public ObservableCollection<RlTickerListOption> ListOptions { get; } = new();
    public ObservableCollection<RlPolicyOption> PolicyOptions { get; } = new();

    [ObservableProperty] private IReadOnlyList<string> _suggestions = Array.Empty<string>();
    [ObservableProperty] private string _tickerText = "";
    [ObservableProperty] private RlTickerListOption? _selectedListOption;
    [ObservableProperty] private RlPolicyOption? _selectedPolicyOption;
    [ObservableProperty] private DateTime? _anchorDate;
    [ObservableProperty] private DateTime? _displayDateStart;
    [ObservableProperty] private DateTime? _displayDateEnd;
    [ObservableProperty] private int _decision;
    [ObservableProperty] private bool _dateIsAuto = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    /// <summary>Set by the page: true while its own replay runs, so the two buttons never replay at the same time.</summary>
    public bool ParentBusy
    {
        get => _parentBusy;
        set { if (SetProperty(ref _parentBusy, value)) NotifyState(); }
    }
    private bool _parentBusy;

    public int MaximumDecision => RlContract.EpisodeHorizon - 1;
    public bool HasModel => _generation is not null;
    public bool IsContinuous => _generation?.Target == RlContinuousContract.Target;
    /// <summary>The inputs are shown for a discrete agent only.</summary>
    public bool ShowInputs => HasModel && !IsContinuous;
    public bool ShowContinuousNotice => IsContinuous;
    public bool ShowNote => HasModel && SourceNote.Length > 0;
    public string SourceNote => _sourceInfo is { } info
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Settings_RlPlayback_Free_Note"), info.Symbol,
            info.FirstBar.ToString(DateFormat, CultureInfo.InvariantCulture), info.LastBar.ToString(DateFormat, CultureInfo.InvariantCulture)) : "";
    public string DateHelp => string.Format(CultureInfo.CurrentCulture, _localization.GetString("Settings_RlPlayback_Free_DateHelp"), RlContract.WindowLength - 1);
    public bool DecisionEditorValid => !_invalidEditors.Contains(DecisionField);
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>The ticker text names an available ticker (case-insensitive). False while the ticker list is still loading.</summary>
    public bool TickerIsKnown => _tickersLoaded && CanonicalTicker is not null;
    private string? CanonicalTicker => _available.TryGetValue(TickerText.Trim(), out var canonical) ? canonical : null;
    public bool ShowTickerUnknown => ShowInputs && _tickersLoaded && !TickerIsKnown;

    /// <summary>"Valid dates: first to last." or the reason the range cannot be read; empty while nothing is known.</summary>
    public string RangeText => _range is { } range
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Settings_RlPlayback_Free_Range"),
            range.EarliestAnchor.ToString(DateFormat, CultureInfo.InvariantCulture), range.LatestAnchor.ToString(DateFormat, CultureInfo.InvariantCulture))
        : _rangeRejection is { } rejection ? RejectText(rejection) : "";
    public bool HasRangeText => RangeText.Length > 0;

    public bool CanShowOnChart => ShowInputs && TickerIsKnown && AnchorDate.HasValue && Decision >= 0 && Decision <= MaximumDecision
        && DecisionEditorValid && SelectedPolicyOption is not null && PolicyOptions.Contains(SelectedPolicyOption) && !IsBusy && !ParentBusy
        && !_rangePending;

    public RlFreeReplayViewModel(RlDialogDataService data, IRlReplayTarget target, IMarketDataProvider market, IRlTickerListSource lists,
        ILocalizationService localization, IDispatcherService dispatcher)
    {
        _data = data; _target = target; _market = market; _lists = lists; _localization = localization; _dispatcher = dispatcher;
        Services.LocalizationManager.Instance.LocaleChanged += LocaleChanged;
        RebuildListOptions();
        _ = LoadTickersAsync();
    }

    /// <summary>View-owned raw numeric editor validity prevents admission from a hidden last valid value.</summary>
    public void SetEditorValidity(string field, bool valid)
    {
        if (valid) _invalidEditors.Remove(field); else _invalidEditors.Add(field);
        NotifyState();
    }

    /// <summary>The page selected another agent (or none): the card starts again from the agent's own ticker and the latest valid date.</summary>
    public void SetModel(RlGeneration? generation)
    {
        if (_disposed) return;
        long revision = ++_modelRevision;
        Cancel(_sourceCts); Cancel(_rangeCts);
        _generation = generation;
        _sourceInfo = null; _range = null; _rangeRejection = null;
        _rebuilding = true;
        try
        {
            RebuildListOptions();
            SelectedListOption = ListOptions[0];
            RebuildPolicyOptions(generation);
            Decision = 0;
            DateIsAuto = true;
            SetDate(null);
            DisplayDateStart = null; DisplayDateEnd = null;
            TickerText = generation?.Symbol ?? "";
            ApplySuggestions();
        }
        finally { _rebuilding = false; }
        StatusMessage = "";
        NotifyState();
        if (generation is null || IsContinuous) return;
        _ = LoadSourceAsync(generation.ModelId, revision);
        ScheduleRangeRead();
    }

    [RelayCommand(CanExecute = nameof(CanShowOnChart))]
    private async Task ShowOnChartAsync()
    {
        if (!CanShowOnChart || _generation is null) return;
        var request = new RlExternalReplayRequest(_generation.ModelId, CanonicalTicker!, DateOnly.FromDateTime(AnchorDate!.Value), Decision, SelectedPolicyOption!.Id);
        IsBusy = true;
        StatusMessage = "";
        using var cancel = new CancellationTokenSource();
        _replayCts = cancel;
        try
        {
            var result = await _data.ReplayExternalAsync(request, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            await _target.ApplyReplayAsync(result.Snapshot, cancel.Token);
            await PublishAsync(() =>
            {
                if (_disposed) return;
                StatusMessage = result.ResolvedAnchorDate != result.RequestedAnchorDate
                    ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Settings_RlPlayback_Free_Resolved"),
                        result.ResolvedAnchorDate.ToString(DateFormat, CultureInfo.InvariantCulture))
                    : _localization.GetString("Settings_RlPlayback_Status_Shown");
            });
        }
        catch (OperationCanceledException)
        {
            await PublishAsync(() => { if (!_disposed) StatusMessage = _localization.GetString("Settings_RlPlayback_Status_Cancelled"); });
        }
        catch (RlExternalReplayException rejection)
        {
            await PublishAsync(() => { if (!_disposed) StatusMessage = RejectText(rejection); });
        }
        catch (Exception e)
        {
            await PublishAsync(() => { if (!_disposed) StatusMessage = RlErrorText.Describe(e, _localization); });
        }
        finally
        {
            if (ReferenceEquals(_replayCts, cancel)) _replayCts = null;
            await PublishAsync(() => { if (!_disposed) IsBusy = false; });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_modelRevision; ++_rangeRevision;
        Cancel(_rangeCts); Cancel(_replayCts); Cancel(_sourceCts);
        Services.LocalizationManager.Instance.LocaleChanged -= LocaleChanged;
    }

    partial void OnTickerTextChanged(string value)
    {
        if (_rebuilding || _disposed) return;
        NotifyState();
        ScheduleRangeRead();
    }

    partial void OnSelectedListOptionChanged(RlTickerListOption? value)
    {
        if (_rebuilding || _disposed) return;
        ApplySuggestions();   // the chosen list never changes the ticker text by itself
    }

    partial void OnAnchorDateChanged(DateTime? value)
    {
        if (!_settingDate && !_rebuilding) DateIsAuto = false;
        NotifyState();
    }

    partial void OnSelectedPolicyOptionChanged(RlPolicyOption? value) => NotifyState();
    partial void OnDecisionChanged(int value) => NotifyState();
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    private async Task LoadTickersAsync()
    {
        try
        {
            var tickers = await _market.GetAvailableTickersAsync();
            await PublishAsync(() =>
            {
                if (_disposed) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string ticker in tickers) if (!string.IsNullOrWhiteSpace(ticker)) map.TryAdd(ticker.Trim(), ticker.Trim());
                _available = map;
                _tickersLoaded = true;
                ApplySuggestions();
                NotifyState();
                ScheduleRangeRead();
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await PublishAsync(() => { if (!_disposed) StatusMessage = RlErrorText.Describe(e, _localization); });
        }
    }

    private async Task LoadSourceAsync(string modelId, long revision)
    {
        using var cancel = new CancellationTokenSource();
        _sourceCts = cancel;
        try
        {
            var info = await _data.LoadSourceInfoAsync(modelId, cancel.Token);
            await PublishAsync(() =>
            {
                if (_disposed || revision != _modelRevision) return;
                _sourceInfo = info;
                OnPropertyChanged(nameof(SourceNote)); OnPropertyChanged(nameof(ShowNote));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The note is informational; the replay itself reports a real problem. */ }
        finally { if (ReferenceEquals(_sourceCts, cancel)) _sourceCts = null; }
    }

    /// <summary>Reads the valid anchor range of the typed ticker after the standard input throttle; a newer ticker cancels the older read.</summary>
    private void ScheduleRangeRead()
    {
        Cancel(_rangeCts);
        long revision = ++_rangeRevision;
        _range = null; _rangeRejection = null; _rangePending = false;
        DisplayDateStart = null; DisplayDateEnd = null;
        if (_generation is null || IsContinuous || !TickerIsKnown) { NotifyRange(); return; }
        // Until the range of THIS ticker is read, the date on screen still belongs to the previous ticker: no replay may start with it.
        _rangePending = true;
        NotifyRange();
        string modelId = _generation.ModelId, ticker = CanonicalTicker!;
        var cancel = new CancellationTokenSource();
        _rangeCts = cancel;
        _ = ReadRangeAsync(modelId, ticker, revision, cancel);
    }

    private async Task ReadRangeAsync(string modelId, string ticker, long revision, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Delay(SettingsConstants.UI_StandardInputThrottleMs, cancel.Token);
            var range = await _data.GetAnchorRangeAsync(modelId, ticker, cancel.Token);
            await PublishAsync(() =>
            {
                if (_disposed || revision != _rangeRevision || cancel.IsCancellationRequested) return;
                _range = range;
                DisplayDateStart = range.EarliestAnchor.ToDateTime(TimeOnly.MinValue);
                DisplayDateEnd = range.LatestAnchor.ToDateTime(TimeOnly.MinValue);
                if (DateIsAuto) SetDate(range.LatestAnchor.ToDateTime(TimeOnly.MinValue));
                _rangePending = false;
                NotifyRange();
            });
        }
        catch (OperationCanceledException) { }
        catch (RlExternalReplayException rejection)
        {
            await PublishAsync(() =>
            {
                if (_disposed || revision != _rangeRevision) return;
                _rangeRejection = rejection;
                _rangePending = false;
                NotifyRange();
            });
        }
        catch (Exception e)
        {
            await PublishAsync(() =>
            {
                if (_disposed || revision != _rangeRevision) return;
                StatusMessage = RlErrorText.Describe(e, _localization);
                _rangePending = false;
                NotifyState();
            });
        }
        finally
        {
            if (ReferenceEquals(_rangeCts, cancel)) _rangeCts = null;
            cancel.Dispose();
        }
    }

    private void SetDate(DateTime? value)
    {
        _settingDate = true;
        try { AnchorDate = value; }
        finally { _settingDate = false; }
    }

    private void ApplySuggestions()
    {
        if (SelectedListOption?.Id is { } id)
        {
            var wanted = new HashSet<string>(_lists.GetTickers(id).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
            Suggestions = _available.Values.Where(wanted.Contains).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        }
        else Suggestions = _available.Values.OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }

    private void RebuildListOptions()
    {
        Guid? selected = SelectedListOption?.Id;
        ListOptions.Clear();
        ListOptions.Add(new RlTickerListOption(null, _localization.GetString("Settings_RlPlayback_Free_AllTickers")));
        foreach (var list in _lists.GetLists()) ListOptions.Add(new RlTickerListOption(list.Id, list.Name));
        SelectedListOption = ListOptions.FirstOrDefault(o => o.Id == selected) ?? ListOptions[0];
    }

    private void RebuildPolicyOptions(RlGeneration? generation)
    {
        PolicyOptions.Clear();
        if (generation is null) { SelectedPolicyOption = null; return; }
        foreach (string id in RlReplayPolicies.For(generation)) PolicyOptions.Add(new RlPolicyOption(id, RlUiDisplay.Policy(id, _localization)));
        SelectedPolicyOption = PolicyOptions.FirstOrDefault(p => p.Id == generation.Target);
    }

    private string RejectText(RlExternalReplayException error)
    {
        string Date(DateOnly? value) => value?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "";
        string text = _localization.GetString(RejectKeyPrefix + error.Reason);
        return error.Reason switch
        {
            RlExternalReplayRejection.NotEnoughHistory => string.Format(CultureInfo.CurrentCulture, text, RlContract.SupportLength),
            RlExternalReplayRejection.NotEnoughBarsBefore => string.Format(CultureInfo.CurrentCulture, text, Date(error.EarliestAnchor)),
            RlExternalReplayRejection.NotEnoughBarsAfter => string.Format(CultureInfo.CurrentCulture, text, Date(error.LatestAnchor), RlContract.EpisodeHorizon),
            _ => text,
        };
    }

    private void LocaleChanged(object? sender, EventArgs args)
    {
        _ = PublishAsync(() =>
        {
            if (_disposed) return;
            _rebuilding = true;
            try
            {
                RebuildListOptions();
                string? policyId = SelectedPolicyOption?.Id;
                RebuildPolicyOptions(_generation);
                SelectedPolicyOption = PolicyOptions.FirstOrDefault(p => p.Id == policyId) ?? SelectedPolicyOption;
            }
            finally { _rebuilding = false; }
            OnPropertyChanged(nameof(SourceNote)); OnPropertyChanged(nameof(DateHelp));
            NotifyRange();
        });
    }

    private void NotifyRange()
    {
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(HasRangeText));
        NotifyState();
    }

    private void NotifyState()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(IsContinuous));
        OnPropertyChanged(nameof(ShowInputs));
        OnPropertyChanged(nameof(ShowContinuousNotice));
        OnPropertyChanged(nameof(ShowNote));
        OnPropertyChanged(nameof(TickerIsKnown));
        OnPropertyChanged(nameof(ShowTickerUnknown));
        OnPropertyChanged(nameof(DecisionEditorValid));
        OnPropertyChanged(nameof(CanShowOnChart));
        ShowOnChartCommand.NotifyCanExecuteChanged();
    }

    private Task PublishAsync(Action publish)
    {
        if (_dispatcher.CheckAccess()) { publish(); return Task.CompletedTask; }
        return _dispatcher.PostAsync(() => { publish(); return Task.CompletedTask; });
    }

    private static void Cancel(CancellationTokenSource? source)
    {
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
