using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Settings page that replays one evaluation block of a stored single-ticker RL agent on the main chart.
/// It holds no editable state: nothing is persisted and <see cref="IsModified"/> is always false.
/// </summary>
public sealed partial class RlPlaybackSettingsViewModel : ViewModelBase, ISettingsPageViewModel, ISettingsPageCloseRequest, IDisposable
{
    private const string DecisionField = nameof(Decision);

    private readonly IRlModelGenerationRegistry _registry;
    private readonly RlDialogDataService _data;
    private readonly IRlReplayTarget _target;
    private readonly ILocalizationService _localization;
    private readonly IDispatcherService _dispatcher;
    private readonly IDialogService? _dialogService;
    private readonly HashSet<string> _invalidEditors = new(StringComparer.Ordinal);
    private IReadOnlyList<RlGeneration> _generations = Array.Empty<RlGeneration>();
    /// <summary>Registered models whose work folder is gone: labelled in the list, removable with <see cref="RemoveSelectedModelCommand"/>.</summary>
    private IReadOnlySet<string> _modelsWithoutWorkFolder = new HashSet<string>();
    private ImmutableArray<RlEpisodeBlock> _blocks = ImmutableArray<RlEpisodeBlock>.Empty;
    private CancellationTokenSource? _blocksCts, _replayCts;
    private long _modelsRevision, _blocksRevision;
    private bool _rebuilding, _disposed;

    public event Action? CloseRequested;

    /// <summary>The "any ticker and date" card; null only when the page is built without it (tests of the segment replay).</summary>
    public RlFreeReplayViewModel? Free { get; }

    public ObservableCollection<RlModelOption> ModelOptions { get; } = new();
    public ObservableCollection<RlBlockOption> BlockOptions { get; } = new();
    public ObservableCollection<RlPolicyOption> PolicyOptions { get; } = new();

    [ObservableProperty] private RlModelOption? _selectedModelOption;
    [ObservableProperty] private RlBlockOption? _selectedBlockOption;
    [ObservableProperty] private RlPolicyOption? _selectedPolicyOption;
    [ObservableProperty] private int _decision;
    [ObservableProperty] private RlDialogLoadState _modelsState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private RlDialogLoadState _blocksState = RlDialogLoadState.Unloaded;
    [ObservableProperty] private RlDialogOperation _operation = RlDialogOperation.Idle;
    [ObservableProperty] private string _statusMessage = "";

    public string TitleKey => "Settings_RlPlayback";
    public string IconKey => "SettingsAdvIcon";
    public bool IsModified => false;
    public int MaximumDecision => RlContract.EpisodeHorizon - 1;
    public bool IsBusy => Operation == RlDialogOperation.Replaying;
    public bool HasModels => ModelsState == RlDialogLoadState.Loaded && ModelOptions.Count > 0;
    public bool HasSelectedModel => SelectedModelOption is not null;
    public bool SelectedModelHasNoWorkFolder => SelectedModelOption is { } option && _modelsWithoutWorkFolder.Contains(option.Generation.ModelId);
    public bool CanRemoveSelectedModel => Operation == RlDialogOperation.Idle && _dialogService is not null && SelectedModelHasNoWorkFolder;
    public bool ShowEmpty => ModelsState == RlDialogLoadState.Loaded && ModelOptions.Count == 0;
    public bool ShowModelsCaption => ModelsState is RlDialogLoadState.Loading or RlDialogLoadState.Failed;
    public bool ShowBlocksCaption => BlocksState != RlDialogLoadState.Loaded;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public string ModelsDisplay => RlUiDisplay.LoadCaption(ModelsState, ModelOptions.Count, _localization);
    public string BlocksDisplay => RlUiDisplay.LoadCaption(BlocksState, BlockOptions.Count, _localization);
    public string SelectedModelDisplay => SelectedModelOption is { } option ? RlUiDisplay.Generation(option.Generation, _localization) : "";
    public bool DecisionEditorValid => !_invalidEditors.Contains(DecisionField);

    public bool CanShowOnChart => Operation == RlDialogOperation.Idle && ModelsState == RlDialogLoadState.Loaded
        && SelectedModelOption is not null && BlocksState == RlDialogLoadState.Loaded && SelectedBlockOption is not null
        && SelectedPolicyOption is not null && PolicyOptions.Contains(SelectedPolicyOption)
        && Decision >= 0 && Decision <= MaximumDecision && DecisionEditorValid && Free?.IsBusy != true;

    public RlPlaybackSettingsViewModel(IRlModelGenerationRegistry registry, RlDialogDataService data, IRlReplayTarget target,
        ILocalizationService localization, IDispatcherService dispatcher, RlFreeReplayViewModel? free = null, IDialogService? dialogService = null)
    {
        _registry = registry; _data = data; _target = target; _localization = localization; _dispatcher = dispatcher; _dialogService = dialogService;
        Free = free;
        if (free is not null)
        {
            free.CloseRequested += OnFreeCloseRequested;
            free.PropertyChanged += OnFreePropertyChanged;
        }
        Services.LocalizationManager.Instance.LocaleChanged += LocaleChanged;
        _ = RefreshAsync();
    }

    public Task SaveChangesAsync() => Task.CompletedTask;

    public void RevertChanges() { }

    /// <summary>Selects the newest agent and the default replay inputs; it never touches the chart.</summary>
    public void ResetToDefault()
    {
        if (_disposed) return;
        _rebuilding = true;
        try { SelectedModelOption = ModelOptions.FirstOrDefault(); }
        finally { _rebuilding = false; }
        ApplyModelSelection(SelectedModelOption);
    }

    /// <summary>View-owned raw numeric editor validity prevents admission from a hidden last valid value.</summary>
    public void SetEditorValidity(string field, bool valid)
    {
        if (valid) _invalidEditors.Remove(field); else _invalidEditors.Add(field);
        NotifyState();
    }

    /// <summary>Removes the selected registered model after a confirmation; only offered while its work folder is gone (the registry refuses everything else).</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelectedModel))]
    private async Task RemoveSelectedModelAsync()
    {
        if (!CanRemoveSelectedModel || SelectedModelOption is not { } option || _dialogService is null || _disposed) return;
        string modelId = option.Generation.ModelId;
        bool isActive = await Task.Run(() => _registry.RlActiveId == modelId || _registry.RlContinuousActiveId == modelId);
        bool confirmed = await _dialogService.ShowConfirmationAsync(_localization.GetString("Rl.Ui.RemoveModel.Title"),
            RlUiDisplay.RemoveModelQuestion(modelId, isActive, _localization));
        if (!confirmed || _disposed) return;
        Operation = RlDialogOperation.RemovingModel;
        try
        {
            var result = await Task.Run(() => _registry.RemoveRlGenerationAsync(modelId));
            if (_disposed) return;
            StatusMessage = RlUiDisplay.RemoveModelResult(result, modelId, _localization);
        }
        catch (Exception error) { if (!_disposed) StatusMessage = RlErrorText.Describe(error, _localization); }
        finally { if (!_disposed) Operation = RlDialogOperation.Idle; }
        string message = StatusMessage;
        await RefreshAsync();
        if (!_disposed) StatusMessage = message;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_disposed) return;
        long revision = ++_modelsRevision;
        ModelsState = RlDialogLoadState.Loading;
        try
        {
            var (generations, withoutWorkFolder) = await Task.Run(() => (_registry.ListRl(), _registry.RlModelIdsWithoutWorkFolder()));
            await PublishAsync(() =>
            {
                if (_disposed || revision != _modelsRevision) return;
                _generations = generations.OrderByDescending(g => g.CreatedUtc).ThenBy(g => g.ModelId, StringComparer.Ordinal).ToArray();
                _modelsWithoutWorkFolder = withoutWorkFolder;
                string? selectedId = SelectedModelOption?.Generation.ModelId;
                bool blocksCurrent = BlocksState == RlDialogLoadState.Loaded;
                _rebuilding = true;
                try
                {
                    RebuildModelOptions();
                    SelectedModelOption = ModelOptions.FirstOrDefault(o => o.Generation.ModelId == selectedId) ?? ModelOptions.FirstOrDefault();
                }
                finally { _rebuilding = false; }
                ModelsState = RlDialogLoadState.Loaded;
                // The same agent stays selected with its chosen replay inputs; any other outcome starts from the defaults.
                bool unchanged = selectedId is not null && SelectedModelOption?.Generation.ModelId == selectedId && blocksCurrent;
                if (!unchanged) ApplyModelSelection(SelectedModelOption);
            });
        }
        catch (Exception e)
        {
            await PublishAsync(() =>
            {
                if (_disposed || revision != _modelsRevision) return;
                _generations = Array.Empty<RlGeneration>();
                _rebuilding = true;
                try { ModelOptions.Clear(); SelectedModelOption = null; }
                finally { _rebuilding = false; }
                ModelsState = RlDialogLoadState.Failed;
                StatusMessage = RlErrorText.Describe(e, _localization);
                ApplyModelSelection(null);
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowOnChart))]
    private async Task ShowOnChartAsync()
    {
        if (!CanShowOnChart) return;
        string modelId = SelectedModelOption!.Generation.ModelId, blockId = SelectedBlockOption!.Block.Id, policyId = SelectedPolicyOption!.Id;
        int decision = Decision;
        Operation = RlDialogOperation.Replaying;
        StatusMessage = "";
        using var cancel = new CancellationTokenSource();
        _replayCts = cancel;
        try
        {
            var snapshot = await _data.ReplayAsync(modelId, blockId, decision, policyId, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            await _target.ApplyReplayAsync(snapshot, cancel.Token);
            await PublishAsync(() =>
            {
                if (_disposed) return;
                StatusMessage = _localization.GetString("Settings_RlPlayback_Status_Shown");
                CloseRequested?.Invoke();
            });
        }
        catch (OperationCanceledException)
        {
            await PublishAsync(() => { if (!_disposed) StatusMessage = _localization.GetString("Settings_RlPlayback_Status_Cancelled"); });
        }
        catch (Exception e)
        {
            await PublishAsync(() => { if (!_disposed) StatusMessage = RlErrorText.Describe(e, _localization); });
        }
        finally
        {
            if (ReferenceEquals(_replayCts, cancel)) _replayCts = null;
            await PublishAsync(() => { if (!_disposed) Operation = RlDialogOperation.Idle; });
        }
    }

    [RelayCommand]
    private void ClearChart()
    {
        if (_disposed) return;
        _target.Clear();
        StatusMessage = _localization.GetString("Settings_RlPlayback_Status_Cleared");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Operation = RlDialogOperation.Disposed;
        ++_modelsRevision; ++_blocksRevision;
        Cancel(_blocksCts); Cancel(_replayCts);
        if (Free is not null)
        {
            Free.CloseRequested -= OnFreeCloseRequested;
            Free.PropertyChanged -= OnFreePropertyChanged;
            Free.Dispose();
        }
        Services.LocalizationManager.Instance.LocaleChanged -= LocaleChanged;
        CloseRequested = null;
        // A replay already applied stays on the chart: the chart owns it.
    }

    partial void OnSelectedModelOptionChanged(RlModelOption? value)
    {
        if (_rebuilding || _disposed) return;
        ApplyModelSelection(value);
    }

    partial void OnSelectedBlockOptionChanged(RlBlockOption? value) => NotifyState();
    partial void OnSelectedPolicyOptionChanged(RlPolicyOption? value) => NotifyState();
    partial void OnDecisionChanged(int value) => NotifyState();
    partial void OnModelsStateChanged(RlDialogLoadState value) => NotifyState();
    partial void OnBlocksStateChanged(RlDialogLoadState value) => NotifyState();
    partial void OnOperationChanged(RlDialogOperation value)
    {
        if (Free is not null) Free.ParentBusy = value != RlDialogOperation.Idle;
        NotifyState();
    }
    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    /// <summary>Rule R2: a new agent resets blocks, policy and decision, then loads its evaluation blocks.</summary>
    private void ApplyModelSelection(RlModelOption? option)
    {
        if (_disposed) return;
        Free?.SetModel(option?.Generation);
        Cancel(_blocksCts);
        long revision = ++_blocksRevision;
        _blocks = ImmutableArray<RlEpisodeBlock>.Empty;
        _rebuilding = true;
        try
        {
            BlockOptions.Clear();
            SelectedBlockOption = null;
            RebuildPolicyOptions(option?.Generation);
            Decision = 0;
        }
        finally { _rebuilding = false; }
        if (option is null) { BlocksState = RlDialogLoadState.Unloaded; NotifyState(); return; }
        BlocksState = RlDialogLoadState.Loading;
        NotifyState();
        _ = LoadBlocksAsync(option.Generation.ModelId, revision);
    }

    private async Task LoadBlocksAsync(string modelId, long revision)
    {
        using var cancel = new CancellationTokenSource();
        _blocksCts = cancel;
        try
        {
            var result = await _data.LoadBlocksAsync(modelId, cancel.Token);
            if (result.Blocks.IsDefault) throw new InvalidDataException("Uninitialized replay blocks.");
            await PublishAsync(() =>
            {
                if (_disposed || revision != _blocksRevision || cancel.IsCancellationRequested || SelectedModelOption?.Generation.ModelId != modelId) return;
                _blocks = result.Blocks;
                _rebuilding = true;
                try
                {
                    RebuildBlockOptions();
                    var preferred = RlReplayDefaults.DefaultBlock(_blocks);
                    SelectedBlockOption = preferred is null ? null : BlockOptions.FirstOrDefault(o => o.Block == preferred);
                }
                finally { _rebuilding = false; }
                BlocksState = RlDialogLoadState.Loaded;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            await PublishAsync(() =>
            {
                if (_disposed || revision != _blocksRevision) return;
                BlocksState = RlDialogLoadState.Failed;
                StatusMessage = RlErrorText.Describe(e, _localization);
            });
        }
        finally { if (ReferenceEquals(_blocksCts, cancel)) _blocksCts = null; }
    }

    private void RebuildModelOptions()
    {
        ModelOptions.Clear();
        foreach (var generation in _generations)
            ModelOptions.Add(new RlModelOption(generation, RlUiDisplay.ModelLabel(generation, _modelsWithoutWorkFolder.Contains(generation.ModelId), _localization)));
    }

    private void RebuildBlockOptions()
    {
        BlockOptions.Clear();
        foreach (var block in _blocks)
            BlockOptions.Add(new RlBlockOption(block, RlUiDisplay.Partition(block.Partition, _localization) + " · " + block.Id));
    }

    private void RebuildPolicyOptions(RlGeneration? generation)
    {
        PolicyOptions.Clear();
        if (generation is null) { SelectedPolicyOption = null; return; }
        foreach (string id in RlReplayPolicies.For(generation)) PolicyOptions.Add(new RlPolicyOption(id, RlUiDisplay.Policy(id, _localization)));
        SelectedPolicyOption = PolicyOptions.FirstOrDefault(p => p.Id == generation.Target);
    }

    /// <summary>Rule R4: option texts follow the language; selections are kept by identifier and nothing is reloaded.</summary>
    private void LocaleChanged(object? sender, EventArgs args)
    {
        _ = PublishAsync(() =>
        {
            if (_disposed) return;
            string? modelId = SelectedModelOption?.Generation.ModelId, blockId = SelectedBlockOption?.Block.Id, policyId = SelectedPolicyOption?.Id;
            _rebuilding = true;
            try
            {
                RebuildModelOptions();
                SelectedModelOption = ModelOptions.FirstOrDefault(o => o.Generation.ModelId == modelId);
                RebuildBlockOptions();
                SelectedBlockOption = BlockOptions.FirstOrDefault(o => o.Block.Id == blockId);
                RebuildPolicyOptions(SelectedModelOption?.Generation);
                SelectedPolicyOption = PolicyOptions.FirstOrDefault(p => p.Id == policyId) ?? SelectedPolicyOption;
            }
            finally { _rebuilding = false; }
            NotifyState();
        });
    }

    private void OnFreeCloseRequested() => CloseRequested?.Invoke();

    private void OnFreePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RlFreeReplayViewModel.IsBusy)) NotifyState();
    }

    private void NotifyState()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(CanShowOnChart));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(HasSelectedModel));
        OnPropertyChanged(nameof(SelectedModelHasNoWorkFolder));
        OnPropertyChanged(nameof(CanRemoveSelectedModel));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowModelsCaption));
        OnPropertyChanged(nameof(ShowBlocksCaption));
        OnPropertyChanged(nameof(ModelsDisplay));
        OnPropertyChanged(nameof(BlocksDisplay));
        OnPropertyChanged(nameof(SelectedModelDisplay));
        OnPropertyChanged(nameof(DecisionEditorValid));
        ShowOnChartCommand.NotifyCanExecuteChanged();
        RemoveSelectedModelCommand.NotifyCanExecuteChanged();
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
