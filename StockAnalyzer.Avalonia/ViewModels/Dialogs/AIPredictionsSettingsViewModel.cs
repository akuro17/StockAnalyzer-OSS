using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using PredictionConfigurationChangedMessage = StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class AIPredictionsSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly IPredictionSettingsManager _predictionSettingsManager;
        private readonly IClipboardService _clipboardService;
        private readonly IToastNotificationService _toastNotificationService;
        private readonly IPythonService? _pythonService;
        private readonly IModelGenerationRegistry? _modelRegistry;
        private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
        private readonly IDialogService? _dialogService;
        private readonly IThemeManager? _themeManager;
        private IndicatorColor _snapshotAttentionHeat;
        private bool _syncingAttentionHeat;

        /// <summary>Tells the charts that the model, ensemble or window the predictions use changed, so they predict again at once.</summary>
        private void NotifyPredictionConfigurationChanged() => _messenger.Send(new PredictionConfigurationChangedMessage());
        private readonly IEnsembleSettingsManager? _ensembleSettings;
        private readonly ILogger<AIPredictionsSettingsViewModel> _logger;

        public ObservableCollection<EnsembleWeightRow> EnsembleMembers { get; } = new();

        [ObservableProperty]
        private EnsembleWeightRow? _selectedEnsembleMember;

        [ObservableProperty]
        private string? _ensembleStatus;

        private int _snapshotWindowSize;
        private readonly HashSet<EnsembleWeightRow> _observedEnsembleRows = new();
        private long _ensembleEditRevision;
        private bool _isDisposed;

        public const string OnnxPipManualInstallCommand = "pip install torch --index-url https://download.pytorch.org/whl/cpu && pip install numpy onnx onnxruntime onnxscript tensorflow tf2onnx lightgbm scikit-learn skl2onnx onnxmltools";
        public const string OnnxPipManualUpgradeCommand = "pip install torch --index-url https://download.pytorch.org/whl/cpu --upgrade && pip install numpy onnx onnxruntime onnxscript tensorflow tf2onnx lightgbm scikit-learn skl2onnx onnxmltools --upgrade";

        public const string OnnxPipManualCommand = OnnxPipManualInstallCommand;

        public string TitleKey => "Settings_AIPredictions";
        public string IconKey => "SettingsAdvIcon";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsModified))]
        private int _selectedWindowSize = PredictionSettingsManager.DefaultWindowSize;

        /// <summary>
        /// The colour of the prediction bands drawn on the chart while a model is active (the theme's <see cref="ThemeColorKey.AttentionHeat"/>: the alpha is the opacity of
        /// the most attended bar). Changing it previews on the charts at once; it is saved with the page.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsModified))]
        private IndicatorColor _attentionHeatColor;

        /// <summary>False without a theme manager (design time): the colour row is then hidden.</summary>
        public bool HasAttentionHeatColor => _themeManager is not null;

        partial void OnAttentionHeatColorChanged(IndicatorColor value)
        {
            if (_isDisposed || _syncingAttentionHeat) return;
            _themeManager?.UpdateSingleColor(ThemeColorKey.AttentionHeat, value);
        }

        /// <summary>The Theme page (reset, preset change) can replace the colour behind this page's back: follow it.</summary>
        private void OnThemeManagerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed || _themeManager is null || e.PropertyName != nameof(IThemeManager.CurrentTheme)) return;
            var current = _themeManager.CurrentTheme.AttentionHeat;
            if (current == AttentionHeatColor) return;
            _syncingAttentionHeat = true;
            try { AttentionHeatColor = current; }
            finally { _syncingAttentionHeat = false; }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AutoButtonText))]
        private bool _isOnnxInstalled;

        public string AutoButtonText => LocalizationManager.Instance[IsOnnxInstalled ? "Settings_AIPredictions_Btn_AutoUpdate" : "Settings_AIPredictions_Btn_AutoInstall"] 
            ?? (IsOnnxInstalled ? "Automatic Update" : "Automatic Setup");

        [ObservableProperty]
        private string? _statusMessage;

        [ObservableProperty]
        private string? _generationStatusMessage;

        [ObservableProperty]
        private bool _isBusy;

        public ObservableCollection<ModelGenerationSummary> Generations { get; } = new();
        public string? LegacyModelStatus => _modelRegistry?.ActiveId is null
            ? LocalizationManager.Instance["Settings_ModelGenerations_LegacyUnverified"] : null;

        [ObservableProperty]
        private ModelGenerationSummary? _selectedGeneration;

        [ObservableProperty]
        private string? _activationWarning;

        /// <param name="IsReplacement">The candidate cannot be compared with the active generation: confirming deactivates the active one and activates the candidate.</param>
        private sealed record PendingActivation(string ModelId, string? ExpectedActiveId, bool IsRollback, bool IsReplacement = false);
        private PendingActivation? _pendingActivation;
        private long _generationSelectionRevision;

        /// <summary>True when the selected row is the active generation: the activation button then deactivates it.</summary>
        public bool IsSelectedGenerationActive => SelectedGeneration?.IsActive == true;

        public string GenerationActivationText => LocalizationManager.Instance[IsSelectedGenerationActive
            ? "Settings_ModelGenerations_Deactivate" : "Settings_ModelGenerations_Activate"];

        partial void OnSelectedGenerationChanged(ModelGenerationSummary? value)
        {
            OnPropertyChanged(nameof(IsSelectedGenerationActive));
            OnPropertyChanged(nameof(GenerationActivationText));
            _generationSelectionRevision++;
            _pendingActivation = null;
            ActivationWarning = null;
            SyncRowSelection();
            SelectAnalysis(value, _generationSelectionRevision);
            Explainability.Select(value?.ModelId);
            MonitoringRefreshTask = RefreshMonitoringAsync();
        }

        public AIPredictionsSettingsViewModel(
            IPredictionSettingsManager predictionSettingsManager,
            IClipboardService clipboardService,
            IToastNotificationService toastNotificationService,
            IPythonService? pythonService = null,
            IModelGenerationRegistry? modelRegistry = null,
            IEnsembleSettingsManager? ensembleSettings = null,
            ILogger<AIPredictionsSettingsViewModel>? logger = null,
            IModelAnalysisService? modelAnalysisService = null, IDispatcherService? analysisDispatcher = null,
            IPredictionLogService? predictionLog = null, IRetrainingScheduler? retrainingScheduler = null,
            ICurrentPredictionHealthService? currentHealth = null, IAttestedEvidenceDiagnostics? evidenceDiagnostics = null,
            IModelExplainabilityService? explainabilityService = null, IMessenger? messenger = null,
            IDialogService? dialogService = null, IThemeManager? themeManager = null)
        {
            _messenger = messenger ?? WeakReferenceMessenger.Default;
            _dialogService = dialogService;
            _themeManager = themeManager;
            if (_themeManager is not null) _themeManager.PropertyChanged += OnThemeManagerChanged;
            Explainability = new ModelExplainabilityViewModel(explainabilityService, analysisDispatcher, logger);
            _predictionSettingsManager = predictionSettingsManager;
            _clipboardService = clipboardService;
            _toastNotificationService = toastNotificationService;
            _pythonService = pythonService;
            _modelRegistry = modelRegistry;
            _ensembleSettings = ensembleSettings;
            _logger = logger ?? NullLogger<AIPredictionsSettingsViewModel>.Instance;
            _modelAnalysisService = modelAnalysisService;
            _analysisDispatcher = analysisDispatcher;
            _predictionLog = predictionLog;
            _retrainingScheduler = retrainingScheduler;
            _currentHealth = currentHealth;
            _evidenceDiagnostics = evidenceDiagnostics;

            TakeSnapshot();
            InitializeFromSnapshot();
            ObserveEnsembleEdits();
            RefreshGenerations();
            _ = LoadEnsembleAsync();
            _ = CheckOnnxInstalledAsync();
            InitializeMonitoring();
        }

        public AIPredictionsSettingsViewModel()
        {
            // Designer fallback
            Explainability = new ModelExplainabilityViewModel();
            _predictionSettingsManager = new DesignPredictionSettingsManager();
            _clipboardService = new DesignClipboardService();
            _toastNotificationService = new DesignToastNotificationService();
            _logger = NullLogger<AIPredictionsSettingsViewModel>.Instance;
            TakeSnapshot();
            InitializeFromSnapshot();
            ObserveEnsembleEdits();
        }

        public async Task CheckOnnxInstalledAsync()
        {
            if (_pythonService != null)
            {
                try
                {
                    IsOnnxInstalled = await _pythonService.IsPackageInstalledAsync("torch");
                }
                catch
                {
                    IsOnnxInstalled = false;
                }
            }
        }

        [RelayCommand]
        private void RefreshGenerations()
        {
            _generationSelectionRevision++;
            Generations.Clear();
            if (_modelRegistry is null) { RebuildGenerationRows(); return; }
            foreach (var item in _modelRegistry.List()) Generations.Add(item);
            SelectedGeneration = null;
            _pendingActivation = null;
            ActivationWarning = null;
            OnPropertyChanged(nameof(LegacyModelStatus));
            RebuildGenerationRows();
        }

        private async Task LoadEnsembleAsync()
        {
            if (_ensembleSettings is null) return;
            long revision = _ensembleEditRevision;
            try
            {
                await _ensembleSettings.LoadAsync();
                if (_isDisposed || revision != _ensembleEditRevision) return;
                EnsembleMembers.Clear();
                if (_ensembleSettings.Current is { } spec)
                    foreach (var member in spec.Members)
                        EnsembleMembers.Add(new EnsembleWeightRow(member.ModelId, (decimal)(member.Weight * 100)));
            }
            catch (Exception ex) { ReportEnsembleFailure(ex, "load", revision); }
        }

        private void ObserveEnsembleEdits() => EnsembleMembers.CollectionChanged += OnEnsembleMembersChanged;

        private void OnEnsembleMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            foreach (var row in _observedEnsembleRows.ToArray())
                if (!EnsembleMembers.Contains(row))
                {
                    row.PropertyChanged -= OnEnsembleRowChanged;
                    _observedEnsembleRows.Remove(row);
                }
            foreach (var row in EnsembleMembers)
                if (_observedEnsembleRows.Add(row)) row.PropertyChanged += OnEnsembleRowChanged;
            AdvanceEnsembleRevision();
            UpdateAddEnsembleMemberAvailability();
        }

        private void OnEnsembleRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EnsembleWeightRow.Percent)) AdvanceEnsembleRevision();
        }

        private long AdvanceEnsembleRevision()
        {
            EnsembleStatus = null;
            return ++_ensembleEditRevision;
        }

        [RelayCommand(CanExecute = nameof(CanAddEnsembleMember))]
        private void AddEnsembleMember()
        {
            if (SelectedGeneration is null || EnsembleMembers.Count >= EnsembleSpec.MaximumMembers
                || EnsembleMembers.Any(row => row.ModelId == SelectedGeneration.ModelId)) return;
            EnsembleMembers.Add(new EnsembleWeightRow(SelectedGeneration.ModelId, 0));
        }

        [RelayCommand]
        private void RemoveEnsembleMember()
        {
            if (SelectedEnsembleMember is not null) EnsembleMembers.Remove(SelectedEnsembleMember);
        }

        [RelayCommand]
        private void EqualEnsembleWeights()
        {
            AdvanceEnsembleRevision();
            try { Apply(EnsembleSpec.Equal(EnsembleMembers.Select(row => row.ModelId))); }
            catch (Exception ex) { ReportEnsembleFailure(ex, "assign equal weights"); }
        }

        [RelayCommand]
        private async Task AccuracyEnsembleWeightsAsync()
        {
            if (_modelRegistry is null) return;
            long revision = _ensembleEditRevision;
            try
            {
                var ids = EnsembleMembers.Select(row => row.ModelId).ToArray();
                var preset = await Task.Run(() =>
                {
                    var manifests = ids.Select(id => _modelRegistry.GetManifest(id)
                        ?? throw new KeyNotFoundException("Generation is unavailable.")).ToArray();
                    var spec = EnsembleSpec.Equal(ids);
                    EnsembleSettingsManager.ValidateGenerations(spec, _modelRegistry);
                    return EnsembleSpec.AccuracyProportional(manifests,
                        manifest => EnsembleSettingsManager.ReadFinalOuterAccuracy(manifest, _modelRegistry));
                });
                if (_isDisposed || revision != _ensembleEditRevision) return;
                Apply(preset);
            }
            catch (Exception ex) { ReportEnsembleFailure(ex, "assign accuracy weights", revision, accuracyPreset: true); }
        }

        private void Apply(EnsembleSpec spec)
        {
            spec.Validate();
            var weights = spec.Members.ToDictionary(member => member.ModelId, member => member.Weight,
                StringComparer.Ordinal);
            if (weights.Count != EnsembleMembers.Count
                || EnsembleMembers.Any(row => !weights.ContainsKey(row.ModelId)))
                throw new InvalidOperationException("Ensemble members changed before weights could be applied.");
            foreach (var row in EnsembleMembers)
                row.Percent = (decimal)(weights[row.ModelId] * 100);
            EnsembleStatus = null;
        }

        [RelayCommand]
        private async Task SaveEnsembleAsync()
        {
            if (_ensembleSettings is null) return;
            long revision = AdvanceEnsembleRevision();
            try
            {
                var spec = new EnsembleSpec(Array.AsReadOnly(EnsembleMembers.Select(row =>
                    new EnsembleMember(row.ModelId, (double)(row.Percent / 100m))).ToArray()));
                await _ensembleSettings.SaveAsync(spec);
                NotifyPredictionConfigurationChanged();
                if (!_isDisposed && revision == _ensembleEditRevision)
                    EnsembleStatus = LocalizationManager.Instance["Settings_Ensemble_Saved"];
            }
            catch (Exception ex) { ReportEnsembleFailure(ex, "save", revision); }
        }

        [RelayCommand]
        private async Task ClearEnsembleAsync()
        {
            if (_ensembleSettings is null) return;
            long revision = AdvanceEnsembleRevision();
            try
            {
                await _ensembleSettings.SaveAsync(null);
                NotifyPredictionConfigurationChanged();
                if (_isDisposed || revision != _ensembleEditRevision) return;
                EnsembleMembers.Clear();
                EnsembleStatus = LocalizationManager.Instance["Settings_Ensemble_Cleared"];
            }
            catch (Exception ex) { ReportEnsembleFailure(ex, "clear", revision); }
        }

        /// <summary>
        /// "Add model from file": the owner picks an <c>.onnx</c> (its <c>.onnx.metrics.json</c> must sit beside it), the registry verifies and registers it
        /// as an immutable generation, and the same manual activation path as the Activate button then makes it the active model (a lower score asks
        /// for the usual confirmation, a window that differs from Window Size is reported). Registering a file that is already known just selects it.
        /// </summary>
        [RelayCommand]
        private async Task AddGenerationFromFileAsync()
        {
            if (_modelRegistry is null || _dialogService is null || IsBusy) return;
            var path = await _dialogService.ShowOpenFileDialogAsync(
                LocalizationManager.Instance["Settings_ModelGenerations_AddFile_Title"],
                new[] { TrainingEnsembleArtifactLayout.ModelExtension });
            if (string.IsNullOrEmpty(path)) return;

            IsBusy = true;
            GenerationStatusMessage = null;
            ActivationWarning = null;
            _pendingActivation = null;
            string? registeredId = null;
            try
            {
                registeredId = (await _modelRegistry.RegisterAsync(path)).ModelId;
                RefreshGenerations();
                SelectedGeneration = Generations.FirstOrDefault(g => g.ModelId == registeredId);
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "register from file"); }
            finally { IsBusy = false; }

            if (registeredId is null || SelectedGeneration is null) return;
            await ActivateGenerationAsync();

            // Registration and activation are separate steps: when the second one did not happen (a window that differs from Window Size, a refused
            // or still unconfirmed switch) the owner must still learn that the model itself is in the list.
            if (_modelRegistry.ActiveId != registeredId)
                GenerationStatusMessage = string.IsNullOrEmpty(GenerationStatusMessage)
                    ? LocalizationManager.Instance["Settings_ModelGenerations_AddedNotActivated"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_AddedNotActivated"] + " " + GenerationStatusMessage;
        }

        /// <summary>
        /// "Remove from list": the selected generation is removed from the registry (its registered copy is deleted; the file it was added from is never
        /// touched, so it can be added again). Not the active generation, not a member of the saved ensemble, not one that is in use.
        /// </summary>
        [RelayCommand]
        private async Task RemoveGenerationAsync()
        {
            if (_modelRegistry is null || _dialogService is null || SelectedGeneration is null || IsBusy) return;
            var modelId = SelectedGeneration.ModelId;
            GenerationStatusMessage = null;
            ActivationWarning = null;
            _pendingActivation = null;
            if (SelectedGeneration.IsActive)
            {
                GenerationStatusMessage = LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveActive"];
                return;
            }

            if (_ensembleSettings?.Current?.Members.Any(member => member.ModelId == modelId) == true)
            {
                GenerationStatusMessage = LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveEnsemble"];
                return;
            }

            if (!await _dialogService.ShowConfirmationAsync(LocalizationManager.Instance["Settings_ModelGenerations_RemoveConfirm_Title"],
                    LocalizationManager.Instance["Settings_ModelGenerations_RemoveConfirm_Message"])) return;

            IsBusy = true;
            try
            {
                var removed = await _modelRegistry.RemoveAsync(modelId);
                // a row of the ensemble being edited (not saved yet) would name a model that no longer exists
                foreach (var row in EnsembleMembers.Where(r => r.ModelId == modelId).ToArray()) EnsembleMembers.Remove(row);
                GenerationStatusMessage = LocalizationManager.Instance[removed
                    ? "Settings_ModelGenerations_Removed" : "Settings_ModelGenerations_Error_Missing"];
                RefreshGenerations();
            }
            catch (ModelRemovalRefusedException ex)
            {
                GenerationStatusMessage = LocalizationManager.Instance[ex.Reason == ModelRemovalRefusal.Active
                    ? "Settings_ModelGenerations_Error_RemoveActive" : "Settings_ModelGenerations_Error_RemoveInUse"];
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "remove"); }
            finally { IsBusy = false; }
        }

        /// <summary>Used by the row check box (<c>ToggleGenerationRowActivationCommand</c>): deactivates the selected generation when it is the active one, otherwise activates it.</summary>
        [RelayCommand]
        private Task ToggleGenerationActivationAsync() => IsSelectedGenerationActive ? DeactivateGenerationAsync() : ActivateGenerationAsync();

        /// <summary>Clears the active generation; predictions then use the configured legacy model, and "restore previous" brings the generation back.</summary>
        [RelayCommand]
        private async Task DeactivateGenerationAsync()
        {
            if (_modelRegistry is null || IsBusy) return;
            IsBusy = true;
            GenerationStatusMessage = null;
            ActivationWarning = null;
            _pendingActivation = null;
            try
            {
                var changed = await _modelRegistry.DeactivateAsync();
                if (changed) NotifyPredictionConfigurationChanged();
                GenerationStatusMessage = LocalizationManager.Instance[changed
                    ? "Settings_ModelGenerations_Deactivated" : "Settings_ModelGenerations_AlreadyInactive"];
                RefreshGenerations();
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "deactivate"); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task ActivateGenerationAsync()
        {
            if (_modelRegistry is null || SelectedGeneration is null || IsBusy) return;
            var candidateId = SelectedGeneration.ModelId;
            var expectedActiveId = _modelRegistry.ActiveId;
            var selectionRevision = _generationSelectionRevision;
            IsBusy = true;
            GenerationStatusMessage = null;
            ActivationWarning = null;
            _pendingActivation = null;
            try
            {
                var changed = await _modelRegistry.ActivateAsync(candidateId, manual: true);
                if (changed) NotifyPredictionConfigurationChanged();
                GenerationStatusMessage = changed
                    ? LocalizationManager.Instance["Settings_ModelGenerations_Activated"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_AlreadyActive"];
                RefreshGenerations();
            }
            catch (ModelActivationConfirmationRequiredException ex)
            {
                if (selectionRevision != _generationSelectionRevision) return;
                _pendingActivation = new PendingActivation(ex.CandidateId ?? candidateId,
                    ex.CandidateId is null ? expectedActiveId : ex.ExpectedActiveId, false);
                ActivationWarning = LocalizationManager.Instance["Settings_ModelGenerations_DowngradeWarning"];
            }
            catch (ModelActivationIncompatibleException)
            {
                if (selectionRevision != _generationSelectionRevision) return;
                _pendingActivation = new PendingActivation(candidateId, expectedActiveId, false, IsReplacement: true);
                ActivationWarning = LocalizationManager.Instance["Settings_ModelGenerations_ReplaceWarning"];
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "activate"); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task ConfirmActivationAsync()
        {
            if (_modelRegistry is null || _pendingActivation is not { } pending || IsBusy) return;
            IsBusy = true;
            GenerationStatusMessage = null;
            try
            {
                var changed = pending.IsReplacement
                    ? await _modelRegistry.ReplaceActiveAsync(pending.ModelId, pending.ExpectedActiveId)
                    : await _modelRegistry.ConfirmActivationAsync(pending.ModelId, pending.ExpectedActiveId);
                if (changed) NotifyPredictionConfigurationChanged();
                GenerationStatusMessage = LocalizationManager.Instance[changed
                    ? pending.IsRollback ? "Settings_ModelGenerations_Restored" : "Settings_ModelGenerations_Activated"
                    : "Settings_ModelGenerations_AlreadyActive"];
                RefreshGenerations();
            }
            catch (ModelActivationContextChangedException ex)
            {
                _pendingActivation = null;
                ActivationWarning = null;
                ReportGenerationFailure(ex, "confirm activation");
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "confirm activation"); }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task RollbackGenerationAsync()
        {
            if (_modelRegistry is null || IsBusy) return;
            var fallbackCandidateId = _modelRegistry.PreviousId;
            var fallbackActiveId = _modelRegistry.ActiveId;
            var selectionRevision = _generationSelectionRevision;
            IsBusy = true;
            GenerationStatusMessage = null;
            ActivationWarning = null;
            _pendingActivation = null;
            try
            {
                var changed = await _modelRegistry.RollbackAsync();
                if (changed) NotifyPredictionConfigurationChanged();
                GenerationStatusMessage = changed
                    ? LocalizationManager.Instance["Settings_ModelGenerations_Restored"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_NoHistory"];
                RefreshGenerations();
            }
            catch (ModelActivationConfirmationRequiredException ex)
            {
                if (selectionRevision != _generationSelectionRevision) return;
                var candidateId = ex.CandidateId ?? fallbackCandidateId;
                if (candidateId is null) return;
                _pendingActivation = new PendingActivation(candidateId,
                    ex.CandidateId is null ? fallbackActiveId : ex.ExpectedActiveId, true);
                ActivationWarning = LocalizationManager.Instance["Settings_ModelGenerations_RestoreWarning"];
            }
            catch (ModelActivationIncompatibleException)
            {
                if (selectionRevision != _generationSelectionRevision || fallbackCandidateId is null) return;
                _pendingActivation = new PendingActivation(fallbackCandidateId, fallbackActiveId, true, IsReplacement: true);
                ActivationWarning = LocalizationManager.Instance["Settings_ModelGenerations_ReplaceWarning"];
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "restore previous"); }
            finally { IsBusy = false; }
        }

        private void ReportEnsembleFailure(Exception exception, string operation, long? revision = null,
            bool accuracyPreset = false)
        {
            _logger.LogError(exception, "Failed to {Operation} classification ensemble", operation);
            if (_isDisposed || (revision.HasValue && revision.Value != _ensembleEditRevision)) return;

            string key = EnsembleSettingsManager.GetValidationFailure(exception) switch
            {
                EnsembleValidationFailure.MissingGeneration => "Settings_Ensemble_Error_MissingGeneration",
                EnsembleValidationFailure.IncompatibleContract => "Settings_Ensemble_Error_IncompatibleContract",
                EnsembleValidationFailure.InvalidAccuracyEvidence => "Settings_Ensemble_Error_AccuracyEvidence",
                _ when exception is KeyNotFoundException => "Settings_Ensemble_Error_MissingGeneration",
                _ when accuracyPreset && (exception is FileNotFoundException or InvalidDataException
                    or System.Text.Json.JsonException) => "Settings_Ensemble_Error_AccuracyEvidence",
                _ when exception is UnauthorizedAccessException or System.IO.IOException =>
                    "Settings_Ensemble_Error_Storage",
                _ when exception is InvalidOperationException =>
                    "Settings_Ensemble_Error_InvalidSelection",
                _ when exception is InvalidDataException or System.Text.Json.JsonException =>
                    "Settings_Ensemble_Error_InvalidData",
                _ => "Settings_Ensemble_Error_General",
            };
            EnsembleStatus = LocalizationManager.Instance[key];
        }

        private void ReportGenerationFailure(Exception exception, string operation)
        {
            _logger.LogError(exception, "Failed to {Operation} model generation", operation);
            if (exception.Data[IModelGenerationRegistry.ReplaceRestoreFailureKey] is Exception restoreFailure)
                _logger.LogError(restoreFailure, "Could not put the previous model generation back");
            if (exception is StockAnalyzer.Core.Models.PredictionWindowMismatchException mismatch)
            {
                GenerationStatusMessage = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    LocalizationManager.Instance["Settings_ModelGenerations_Error_WindowMismatch"],
                    mismatch.ModelWindow, mismatch.ConfiguredWindow);
                return;
            }

            string key = exception switch
            {
                ModelActivationContextChangedException => "Settings_ModelGenerations_Error_ContextChanged",
                KeyNotFoundException => "Settings_ModelGenerations_Error_Missing",
                FileNotFoundException => "Settings_ModelGenerations_Error_FileMissing",
                InvalidDataException => "Settings_ModelGenerations_Error_Corrupt",
                UnauthorizedAccessException or System.IO.IOException => "Settings_ModelGenerations_Error_Storage",
                InvalidOperationException => "Settings_ModelGenerations_Error_Incompatible",
                _ => "Settings_ModelGenerations_Error_General",
            };
            GenerationStatusMessage = LocalizationManager.Instance[key];
        }

        private class DesignPredictionSettingsManager : IPredictionSettingsManager
        {
            public int WindowSize => PredictionSettingsManager.DefaultWindowSize;
            public void SetWindowSize(int value) { }
            public Task SaveAsync() => Task.CompletedTask;
            public Task LoadAsync() => Task.CompletedTask;
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        private class DesignClipboardService : IClipboardService
        {
            public Task SetTextAsync(string text) => Task.CompletedTask;
            public Task<string?> GetTextAsync() => Task.FromResult<string?>(null);
        }

        private class DesignToastNotificationService : IToastNotificationService
        {
            public string? NotificationMessage => null;
            public bool IsNotificationVisible => false;
            public void ShowNotification(string message) { }
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        private void TakeSnapshot()
        {
            _snapshotWindowSize = _predictionSettingsManager.WindowSize > 0 
                ? _predictionSettingsManager.WindowSize 
                : PredictionSettingsManager.DefaultWindowSize;
            if (_themeManager is not null) _snapshotAttentionHeat = _themeManager.CurrentTheme.AttentionHeat;
        }

        private void InitializeFromSnapshot()
        {
            SelectedWindowSize = _snapshotWindowSize;
            if (_themeManager is not null) AttentionHeatColor = _snapshotAttentionHeat;
            OnPropertyChanged(nameof(IsModified));
        }

        public bool IsModified => SelectedWindowSize != _snapshotWindowSize || (_themeManager is not null && AttentionHeatColor != _snapshotAttentionHeat);

        partial void OnSelectedWindowSizeChanged(int value)
        {
            if (_isDisposed) return;
            if (value > 0)
            {
                _predictionSettingsManager.SetWindowSize(value);
            }
        }

        [RelayCommand]
        public async Task ManualInstallOnnxAsync()
        {
            if (_isDisposed) return;
            bool isUpgrade = IsOnnxInstalled;
            var cmd = isUpgrade ? OnnxPipManualUpgradeCommand : OnnxPipManualInstallCommand;
            await _clipboardService.SetTextAsync(cmd);
            var msgKey = isUpgrade ? "Settings_AIPredictions_PipCopied_Upgrade" : "Settings_AIPredictions_PipCopied_Install";
            var msg = LocalizationManager.Instance[msgKey] 
                ?? LocalizationManager.Instance["Settings_AIPredictions_PipCopied"] 
                ?? (isUpgrade ? "Pip upgrade command copied to clipboard." : "Pip install command copied to clipboard.");
            StatusMessage = msg;
            _toastNotificationService.ShowNotification(msg);
        }

        public static readonly string[] OnnxTrainingPackages = new[]
        {
            "numpy",
            "torch",
            "onnx",
            "onnxruntime",
            "onnxscript",
            "tensorflow",
            "tf2onnx",
            "lightgbm",
            "scikit-learn",
            "skl2onnx",
            "onnxmltools"
        };

        [RelayCommand]
        public async Task AutoInstallOnnxAsync()
        {
            if (_isDisposed || IsBusy) return;
            IsBusy = true;
            bool isUpgrade = IsOnnxInstalled;
            var startKey = isUpgrade ? "Settings_AIPredictions_Upgrading" : "Settings_AIPredictions_Installing";
            var successKey = isUpgrade ? "Settings_AIPredictions_UpdateSuccess" : "Settings_AIPredictions_InstallSuccess";

            StatusMessage = LocalizationManager.Instance[startKey] ?? (isUpgrade ? "Upgrading ONNX dependencies..." : "Installing ONNX dependencies...");

            try
            {
                if (_pythonService != null)
                {
                    var progress = new Progress<string>(msg => StatusMessage = msg);
                    await _pythonService.InstallPackagesAsync(OnnxTrainingPackages, forceUpgrade: isUpgrade, progress: progress);
                    IsOnnxInstalled = true;
                }
                var successMsg = LocalizationManager.Instance[successKey] ?? (isUpgrade ? "ONNX update completed successfully." : "ONNX setup completed successfully.");
                StatusMessage = successMsg;
                _toastNotificationService.ShowNotification(successMsg);
            }
            catch (Exception ex)
            {
                var errorMsg = string.Format(LocalizationManager.Instance["Settings_AIPredictions_InstallError"] ?? "Installation error: {0}", ex.Message);
                StatusMessage = errorMsg;
                _toastNotificationService.ShowNotification(errorMsg);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task SaveChangesAsync()
        {
            if (_isDisposed) return;
            if (SelectedWindowSize > 0)
            {
                _predictionSettingsManager.SetWindowSize(SelectedWindowSize);
            }
            var windowChanged = SelectedWindowSize != _snapshotWindowSize;
            var colorChanged = _themeManager is not null && AttentionHeatColor != _snapshotAttentionHeat;
            await _predictionSettingsManager.SaveAsync();
            if (colorChanged) await _themeManager!.SaveAsync(); // the colour lives in the theme file; written only when it changed
            TakeSnapshot();
            OnPropertyChanged(nameof(IsModified));
            if (windowChanged) NotifyPredictionConfigurationChanged();
        }

        public void RevertChanges()
        {
            if (_isDisposed) return;
            _predictionSettingsManager.SetWindowSize(_snapshotWindowSize);
            InitializeFromSnapshot(); // also puts the snapshot colour back on the charts
        }

        public void ResetToDefault()
        {
            if (_isDisposed) return;
            SelectedWindowSize = PredictionSettingsManager.DefaultWindowSize;
            if (_themeManager is not null)
                AttentionHeatColor = (_themeManager.CurrentTheme.IsDark ? ThemeColors.Dark : ThemeColors.Light).AttentionHeat;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (_themeManager is not null) _themeManager.PropertyChanged -= OnThemeManagerChanged;
            Explainability.Dispose();
            DisposeMonitoring();
            EnsembleMembers.CollectionChanged -= OnEnsembleMembersChanged;
            foreach (var row in _observedEnsembleRows) row.PropertyChanged -= OnEnsembleRowChanged;
            _observedEnsembleRows.Clear();
            GC.SuppressFinalize(this);
        }
    }

    public partial class EnsembleWeightRow : ObservableObject
    {
        public EnsembleWeightRow(string modelId, decimal percent) { ModelId = modelId; Percent = percent; }
        public string ModelId { get; }
        [ObservableProperty] private decimal _percent;
    }
}
