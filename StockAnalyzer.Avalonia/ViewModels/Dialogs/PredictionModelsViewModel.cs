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
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class PredictionModelsViewModel : ViewModelBase, IDisposable
    {
        private readonly IPredictionSettingsManager _predictionSettingsManager;
        private readonly IModelGenerationRegistry? _modelRegistry;
        private readonly IMessenger _messenger = WeakReferenceMessenger.Default;
        private readonly IDialogService? _dialogService;

        private bool _sendingConfigurationChange;

        /// <summary>Tells the charts that the model, ensemble or window the predictions use changed, so they predict again at once.</summary>
        private void NotifyPredictionConfigurationChanged()
        {
            _sendingConfigurationChange = true;   // this window refreshes its own list after its own change
            try { _messenger.Send(new PredictionConfigurationChangedMessage()); }
            finally { _sendingConfigurationChange = false; }
        }

        /// <summary>Someone else changed the models (the Training Wizard registered one, the window size was saved): the list shows the registry again.</summary>
        private void OnPredictionConfigurationChangedByOthers()
        {
            if (_isDisposed || _sendingConfigurationChange) return;
            RefreshGenerations();
        }
        private readonly IEnsembleSettingsManager? _ensembleSettings;
        private readonly ILogger<PredictionModelsViewModel> _logger;

        public ObservableCollection<EnsembleWeightRow> EnsembleMembers { get; } = new();

        [ObservableProperty]
        private EnsembleWeightRow? _selectedEnsembleMember;

        [ObservableProperty]
        private string? _ensembleStatus;

        private readonly HashSet<EnsembleWeightRow> _observedEnsembleRows = new();
        private long _ensembleEditRevision;
        private bool _isDisposed;

        [ObservableProperty]
        private string? _generationStatusMessage;

        [ObservableProperty]
        private bool _isBusy;

        public ObservableCollection<ModelGenerationSummary> Generations { get; } = new();
        public string? LegacyModelStatus => _modelRegistry?.ActiveId is null
            ? LocalizationManager.Instance["Settings_ModelGenerations_LegacyUnverified"] : null;

        [ObservableProperty]
        private ModelGenerationSummary? _selectedGeneration;

        /// <param name="IsReplacement">The candidate cannot be compared with the active generation: the activation deactivates the active one and activates the candidate.</param>
        private sealed record PendingActivation(string ModelId, string? ExpectedActiveId, bool IsReplacement = false);
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
            SyncRowSelection();
            SelectAnalysis(value, _generationSelectionRevision);
            Explainability.Select(value?.ModelId);
        }

        public PredictionModelsViewModel(
            IPredictionSettingsManager predictionSettingsManager,
            IModelGenerationRegistry? modelRegistry = null,
            IEnsembleSettingsManager? ensembleSettings = null,
            ILogger<PredictionModelsViewModel>? logger = null,
            IModelAnalysisService? modelAnalysisService = null, IDispatcherService? analysisDispatcher = null,
            IPredictionLogService? predictionLog = null,
            ICurrentPredictionHealthService? currentHealth = null,
            IModelExplainabilityService? explainabilityService = null, IMessenger? messenger = null,
            IDialogService? dialogService = null,
            IPredictionRunTarget? predictionRunTarget = null, IStockAnalyzerSettings? settings = null)
        {
            _predictionRunTarget = predictionRunTarget;
            _settings = settings;
            _messenger = messenger ?? WeakReferenceMessenger.Default;
            _dialogService = dialogService;
            _predictionSettingsManager = predictionSettingsManager;
            _predictionSettingsManager.PropertyChanged += OnPredictionSettingsChanged;
            Explainability = new ModelExplainabilityViewModel(explainabilityService, analysisDispatcher, logger);
            _modelRegistry = modelRegistry;
            _ensembleSettings = ensembleSettings;
            _logger = logger ?? NullLogger<PredictionModelsViewModel>.Instance;
            _modelAnalysisService = modelAnalysisService;
            _analysisDispatcher = analysisDispatcher;
            _predictionLog = predictionLog;
            _currentHealth = currentHealth;

            ObserveEnsembleEdits();
            RefreshGenerations();
            _ = LoadEnsembleAsync();
            InitializeMonitoring();
            _messenger.Register<PredictionModelsViewModel, PredictionConfigurationChangedMessage>(this,
                static (recipient, _) => recipient.OnPredictionConfigurationChangedByOthers());
        }

        public PredictionModelsViewModel()
        {
            // Designer fallback
            Explainability = new ModelExplainabilityViewModel();
            _predictionSettingsManager = new DesignPredictionSettingsManager();
            _logger = NullLogger<PredictionModelsViewModel>.Instance;
            ObserveEnsembleEdits();
        }

        private void RefreshGenerations()
        {
            _generationSelectionRevision++;
            Generations.Clear();
            if (_modelRegistry is null) { RebuildGenerationRows(); return; }
            foreach (var item in _modelRegistry.List()) Generations.Add(item);
            SelectedGeneration = null;
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
                {
                    foreach (var member in spec.Members)
                        EnsembleMembers.Add(new EnsembleWeightRow(member.ModelId, (decimal)(member.Weight * 100)));
                    RequestMonitoringRefresh();   // a saved ensemble has a health line; none saved means nothing to show
                }
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
                RequestMonitoringRefresh();
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
                RequestMonitoringRefresh();
                if (_isDisposed || revision != _ensembleEditRevision) return;
                EnsembleMembers.Clear();
                EnsembleStatus = LocalizationManager.Instance["Settings_Ensemble_Cleared"];
            }
            catch (Exception ex) { ReportEnsembleFailure(ex, "clear", revision); }
        }

        /// <summary>
        /// "Add model from file": the owner picks an <c>.onnx</c> (its <c>.onnx.metrics.json</c> must sit beside it), the registry verifies and registers it
        /// as an immutable generation, and the same manual activation path as the Activate button then makes it the active model (a window that differs
        /// from Window Size is reported). Registering a file that is already known just selects it.
        /// </summary>
        [RelayCommand]
        private async Task AddGenerationFromFileAsync()
        {
            if (_modelRegistry is null || _dialogService is null || IsBusy) return;
            var path = await _dialogService.ShowOpenFileDialogAsync(
                LocalizationManager.Instance["Settings_ModelGenerations_AddFile_Title"],
                new[] { TrainingEnsembleArtifactLayout.ModelExtension },
                StockAnalyzer.Core.Common.PathDiscovery.ResolveTrainingWizardArtifactsDirectory());   // where the Training Wizard puts its models
            if (string.IsNullOrEmpty(path)) return;

            IsBusy = true;
            GenerationStatusMessage = null;
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
            var activeBefore = _modelRegistry.ActiveId;
            await ActivateGenerationAsync();

            // Registration and activation are separate steps: when the second one did not happen (a window that differs from Window Size, a refused
            // or still unconfirmed switch) the owner must still learn that the model itself is in the list.
            if (_modelRegistry.ActiveId != registeredId)
                GenerationStatusMessage = string.IsNullOrEmpty(GenerationStatusMessage)
                    ? LocalizationManager.Instance["Settings_ModelGenerations_AddedNotActivated"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_AddedNotActivated"] + " " + GenerationStatusMessage;
            if (_modelRegistry.ActiveId != activeBefore) await RunPredictionAsync();   // say in the footer what the chart shows with the new model
        }

        /// <summary>
        /// "Remove from list": the selected generation is removed from the registry (its registered copy is deleted; the file it was added from is never
        /// touched, so it can be added again). Any listed model can be removed without a question: the active one is deactivated first and a member of
        /// the saved ensemble is taken out of the ensemble first. Only a generation that a prediction is using at this very moment is refused.
        /// </summary>
        [RelayCommand]
        private async Task RemoveGenerationAsync()
        {
            if (_modelRegistry is null || IsBusy) return;
            GenerationStatusMessage = null;
            if (SelectedGeneration is null)
            {
                GenerationStatusMessage = LocalizationManager.Instance["Settings_ModelGenerations_Error_RemoveNoSelection"];
                return;
            }

            var modelId = SelectedGeneration.ModelId;
            var wasActive = _modelRegistry.ActiveId == modelId;
            var ensemble = _ensembleSettings?.Current;
            var inEnsemble = ensemble?.Members.Any(member => member.ModelId == modelId) == true;

            IsBusy = true;
            try
            {
                if (wasActive && await _modelRegistry.DeactivateAsync()) NotifyPredictionConfigurationChanged();
                if (inEnsemble) await TakeOutOfSavedEnsembleAsync(ensemble!, modelId);
                var removed = await RemoveFromRegistryAsync(modelId);
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
                RefreshGenerations();   // the deactivation / ensemble change that was already made shows in the list
            }
            catch (Exception ex)
            {
                ReportGenerationFailure(ex, "remove");
                RefreshGenerations();
            }
            finally { IsBusy = false; }
        }

        /// <summary>
        /// Saves the ensemble without <paramref name="modelId"/>: the other weights are scaled to sum to one again (equal weights when they were all zero),
        /// and an ensemble that would have no model left is cleared.
        /// </summary>
        private async Task TakeOutOfSavedEnsembleAsync(EnsembleSpec spec, string modelId)
        {
            var rest = spec.Members.Where(member => member.ModelId != modelId).ToArray();
            EnsembleSpec? reduced = null;
            if (rest.Length > 0)
            {
                double total = rest.Sum(member => member.Weight);
                reduced = total > 0
                    ? new EnsembleSpec(Array.AsReadOnly(rest.Select(member => member with { Weight = member.Weight / total }).ToArray()))
                    : EnsembleSpec.Equal(rest.Select(member => member.ModelId));
            }

            await _ensembleSettings!.SaveAsync(reduced);
            NotifyPredictionConfigurationChanged();
            RequestMonitoringRefresh();
            if (reduced is null) return;
            foreach (var row in EnsembleMembers)
                if (reduced.Members.FirstOrDefault(member => member.ModelId == row.ModelId) is { } kept) row.Percent = (decimal)(kept.Weight * 100);
        }

        /// <summary>
        /// Removes the generation from the registry. The main chart's predictor keeps the generation it used last loaded until it predicts again, which
        /// makes a model that was just deactivated look "in use": the chart is asked to predict once, which lets go of it, and the removal is tried once more.
        /// </summary>
        private async Task<bool> RemoveFromRegistryAsync(string modelId)
        {
            try { return await _modelRegistry!.RemoveAsync(modelId); }
            catch (ModelRemovalRefusedException ex) when (ex.Reason == ModelRemovalRefusal.InUse && _predictionRunTarget is not null)
            {
                await _predictionRunTarget.RunAsync(default);
                return await _modelRegistry!.RemoveAsync(modelId);
            }
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
            PendingActivation? confirmed = null;
            IsBusy = true;
            GenerationStatusMessage = null;
            try
            {
                var changed = await _modelRegistry.ActivateAsync(candidateId, manual: true);
                if (changed) NotifyPredictionConfigurationChanged();
                GenerationStatusMessage = changed
                    ? LocalizationManager.Instance["Settings_ModelGenerations_Activated"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_AlreadyActive"];
                RefreshGenerations();
            }
            // The owner asked for this model: a score that does not improve, or an active model it cannot be compared with, is no reason to ask again.
            catch (ModelActivationConfirmationRequiredException ex)
            {
                confirmed = new PendingActivation(ex.CandidateId ?? candidateId, ex.CandidateId is null ? expectedActiveId : ex.ExpectedActiveId);
            }
            catch (ModelActivationIncompatibleException)
            {
                confirmed = new PendingActivation(candidateId, expectedActiveId, IsReplacement: true);
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "activate"); }
            finally { IsBusy = false; }
            if (confirmed is not null) await ActivateConfirmedAsync(confirmed);
        }

        private async Task ActivateConfirmedAsync(PendingActivation pending)
        {
            if (_modelRegistry is null || IsBusy) return;
            IsBusy = true;
            GenerationStatusMessage = null;
            try
            {
                var changed = pending.IsReplacement
                    ? await _modelRegistry.ReplaceActiveAsync(pending.ModelId, pending.ExpectedActiveId)
                    : await _modelRegistry.ConfirmActivationAsync(pending.ModelId, pending.ExpectedActiveId);
                if (changed) NotifyPredictionConfigurationChanged();
                GenerationStatusMessage = LocalizationManager.Instance[changed
                    ? "Settings_ModelGenerations_Activated" : "Settings_ModelGenerations_AlreadyActive"];
                RefreshGenerations();
            }
            catch (Exception ex) { ReportGenerationFailure(ex, "activate"); }
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

        /// <summary>The prediction window size the models are checked against (read-only here; it is changed in Settings > AI Predictions).</summary>
        public int ConfiguredWindowSize => _predictionSettingsManager.WindowSize > 0
            ? _predictionSettingsManager.WindowSize
            : PredictionSettingsManager.DefaultWindowSize;

        public string ConfiguredWindowText => string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocalizationManager.Instance["PredictionModels_ConfiguredWindow"], ConfiguredWindowSize);

        private void OnPredictionSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed || e.PropertyName != nameof(IPredictionSettingsManager.WindowSize)) return;
            OnPropertyChanged(nameof(ConfiguredWindowSize));
            OnPropertyChanged(nameof(ConfiguredWindowText));
        }

        [RelayCommand]
        private async Task OpenSettingsAsync()
        {
            if (_isDisposed || _dialogService is null) return;
            await _dialogService.ShowSettingsDialogAsync(SettingsConstants.Keys.AIPredictions);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _predictionSettingsManager.PropertyChanged -= OnPredictionSettingsChanged;
            _messenger.UnregisterAll(this);
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
