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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class AIPredictionsSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly IPredictionSettingsManager _predictionSettingsManager;
        private readonly IClipboardService _clipboardService;
        private readonly IToastNotificationService _toastNotificationService;
        private readonly IPythonService? _pythonService;
        private readonly IModelGenerationRegistry? _modelRegistry;
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

        private sealed record PendingActivation(string ModelId, string? ExpectedActiveId, bool IsRollback);
        private PendingActivation? _pendingActivation;
        private long _generationSelectionRevision;

        partial void OnSelectedGenerationChanged(ModelGenerationSummary? value)
        {
            _generationSelectionRevision++;
            _pendingActivation = null;
            ActivationWarning = null;
            SelectAnalysis(value, _generationSelectionRevision);
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
            ICurrentPredictionHealthService? currentHealth = null)
        {
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
            if (_modelRegistry is null) return;
            foreach (var item in _modelRegistry.List()) Generations.Add(item);
            SelectedGeneration = null;
            _pendingActivation = null;
            ActivationWarning = null;
            OnPropertyChanged(nameof(LegacyModelStatus));
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

        [RelayCommand]
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
                if (_isDisposed || revision != _ensembleEditRevision) return;
                EnsembleMembers.Clear();
                EnsembleStatus = LocalizationManager.Instance["Settings_Ensemble_Cleared"];
            }
            catch (Exception ex) { ReportEnsembleFailure(ex, "clear", revision); }
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
                var changed = await _modelRegistry.ConfirmActivationAsync(pending.ModelId, pending.ExpectedActiveId);
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
            string key = exception switch
            {
                ModelActivationContextChangedException => "Settings_ModelGenerations_Error_ContextChanged",
                KeyNotFoundException => "Settings_ModelGenerations_Error_Missing",
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
        }

        private void InitializeFromSnapshot()
        {
            SelectedWindowSize = _snapshotWindowSize;
            OnPropertyChanged(nameof(IsModified));
        }

        public bool IsModified => SelectedWindowSize != _snapshotWindowSize;

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
            await _predictionSettingsManager.SaveAsync();
            TakeSnapshot();
            OnPropertyChanged(nameof(IsModified));
        }

        public void RevertChanges()
        {
            if (_isDisposed) return;
            _predictionSettingsManager.SetWindowSize(_snapshotWindowSize);
            InitializeFromSnapshot();
        }

        public void ResetToDefault()
        {
            if (_isDisposed) return;
            SelectedWindowSize = PredictionSettingsManager.DefaultWindowSize;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
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
