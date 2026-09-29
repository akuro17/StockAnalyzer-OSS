using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        public ObservableCollection<EnsembleWeightRow> EnsembleMembers { get; } = new();

        [ObservableProperty]
        private EnsembleWeightRow? _selectedEnsembleMember;

        [ObservableProperty]
        private string? _ensembleStatus;

        private int _snapshotWindowSize;
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
        private bool _isBusy;

        public ObservableCollection<ModelGenerationSummary> Generations { get; } = new();
        public string? LegacyModelStatus => _modelRegistry?.ActiveId is null
            ? LocalizationManager.Instance["Settings_ModelGenerations_LegacyUnverified"] : null;

        [ObservableProperty]
        private ModelGenerationSummary? _selectedGeneration;

        [ObservableProperty]
        private string? _activationWarning;

        private string? _pendingActivation;

        public AIPredictionsSettingsViewModel(
            IPredictionSettingsManager predictionSettingsManager,
            IClipboardService clipboardService,
            IToastNotificationService toastNotificationService,
            IPythonService? pythonService = null,
            IModelGenerationRegistry? modelRegistry = null,
            IEnsembleSettingsManager? ensembleSettings = null)
        {
            _predictionSettingsManager = predictionSettingsManager;
            _clipboardService = clipboardService;
            _toastNotificationService = toastNotificationService;
            _pythonService = pythonService;
            _modelRegistry = modelRegistry;
            _ensembleSettings = ensembleSettings;

            TakeSnapshot();
            InitializeFromSnapshot();
            RefreshGenerations();
            _ = LoadEnsembleAsync();
            _ = CheckOnnxInstalledAsync();
        }

        public AIPredictionsSettingsViewModel()
        {
            // Designer fallback
            _predictionSettingsManager = new DesignPredictionSettingsManager();
            _clipboardService = new DesignClipboardService();
            _toastNotificationService = new DesignToastNotificationService();
            TakeSnapshot();
            InitializeFromSnapshot();
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
            try
            {
                await _ensembleSettings.LoadAsync();
                EnsembleMembers.Clear();
                if (_ensembleSettings.Current is { } spec)
                    foreach (var member in spec.Members)
                        EnsembleMembers.Add(new EnsembleWeightRow(member.ModelId, (decimal)(member.Weight * 100)));
            }
            catch (Exception ex) { EnsembleStatus = ex.Message; }
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
            try { Apply(EnsembleSpec.Equal(EnsembleMembers.Select(row => row.ModelId))); }
            catch (Exception ex) { EnsembleStatus = ex.Message; }
        }

        [RelayCommand]
        private async Task AccuracyEnsembleWeightsAsync()
        {
            if (_modelRegistry is null) return;
            try
            {
                var ids = EnsembleMembers.Select(row => row.ModelId).ToArray();
                var preset = await Task.Run(() =>
                {
                    var manifests = ids.Select(id => _modelRegistry.GetManifest(id)
                        ?? throw new InvalidOperationException("Generation is unavailable.")).ToArray();
                    var spec = EnsembleSpec.Equal(ids);
                    EnsembleSettingsManager.ValidateGenerations(spec, _modelRegistry);
                    return EnsembleSpec.AccuracyProportional(manifests,
                        manifest => EnsembleSettingsManager.ReadFinalOuterAccuracy(manifest, _modelRegistry));
                });
                Apply(preset);
            }
            catch (Exception ex) { EnsembleStatus = ex.Message; }
        }

        private void Apply(EnsembleSpec spec)
        {
            for (int i = 0; i < spec.Members.Count; i++)
                EnsembleMembers[i].Percent = (decimal)(spec.Members[i].Weight * 100);
            EnsembleStatus = null;
        }

        [RelayCommand]
        private async Task SaveEnsembleAsync()
        {
            if (_ensembleSettings is null) return;
            try
            {
                var spec = new EnsembleSpec(EnsembleMembers.Select(row =>
                    new EnsembleMember(row.ModelId, (double)(row.Percent / 100m))).ToArray());
                await _ensembleSettings.SaveAsync(spec);
                EnsembleStatus = LocalizationManager.Instance["Settings_Ensemble_Saved"];
            }
            catch (Exception ex) { EnsembleStatus = ex.Message; }
        }

        [RelayCommand]
        private async Task ClearEnsembleAsync()
        {
            if (_ensembleSettings is null) return;
            try
            {
                await _ensembleSettings.SaveAsync(null);
                EnsembleMembers.Clear();
                EnsembleStatus = LocalizationManager.Instance["Settings_Ensemble_Cleared"];
            }
            catch (Exception ex) { EnsembleStatus = ex.Message; }
        }

        [RelayCommand]
        private async Task ActivateGenerationAsync()
        {
            if (_modelRegistry is null || SelectedGeneration is null || IsBusy) return;
            IsBusy = true;
            try
            {
                var changed = await _modelRegistry.ActivateAsync(SelectedGeneration.ModelId, manual: true);
                StatusMessage = changed
                    ? LocalizationManager.Instance["Settings_ModelGenerations_Activated"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_AlreadyActive"];
                RefreshGenerations();
            }
            catch (ModelActivationConfirmationRequiredException)
            {
                _pendingActivation = SelectedGeneration.ModelId;
                ActivationWarning = LocalizationManager.Instance["Settings_ModelGenerations_DowngradeWarning"];
            }
            catch (Exception ex) { StatusMessage = ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task ConfirmActivationAsync()
        {
            if (_modelRegistry is null || _pendingActivation is null || IsBusy) return;
            IsBusy = true;
            try
            {
                await _modelRegistry.ActivateAsync(_pendingActivation, manual: true, confirmLowerScore: true);
                StatusMessage = LocalizationManager.Instance["Settings_ModelGenerations_Activated"];
                RefreshGenerations();
            }
            catch (Exception ex) { StatusMessage = ex.Message; }
            finally { IsBusy = false; }
        }

        [RelayCommand]
        private async Task RollbackGenerationAsync()
        {
            if (_modelRegistry is null || IsBusy) return;
            IsBusy = true;
            try
            {
                var changed = await _modelRegistry.RollbackAsync();
                StatusMessage = changed
                    ? LocalizationManager.Instance["Settings_ModelGenerations_Restored"]
                    : LocalizationManager.Instance["Settings_ModelGenerations_NoHistory"];
                RefreshGenerations();
            }
            catch (ModelActivationConfirmationRequiredException)
            {
                _pendingActivation = _modelRegistry.PreviousId;
                ActivationWarning = LocalizationManager.Instance["Settings_ModelGenerations_RestoreWarning"];
            }
            catch (Exception ex) { StatusMessage = ex.Message; }
            finally { IsBusy = false; }
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
