using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Theme;
using PredictionConfigurationChangedMessage = StockAnalyzer.Avalonia.Common.PredictionConfigurationChangedMessage;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    /// <summary>
    /// Settings > AI Predictions: the two values of the prediction that are saved with Apply/OK and undone with Cancel (the window size and the colour of
    /// the attention bands). The window size reaches <see cref="IPredictionSettingsManager"/> (and so activation checks and the charts) only in <see cref="SaveChangesAsync"/>.
    /// The ONNX Environment &amp; Dependencies card (package setup) is an action that takes effect when pressed: Apply and Cancel do not touch it. The model
    /// operations (activate, ensemble) are in the Tools > Prediction Models window (<see cref="PredictionModelsViewModel"/>).
    /// </summary>
    public partial class AIPredictionsSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly IPredictionSettingsManager _predictionSettingsManager;
        private readonly IMessenger _messenger;
        private readonly IDialogService? _dialogService;
        private readonly IThemeManager? _themeManager;
        private readonly IClipboardService _clipboardService;
        private readonly IToastNotificationService _toastNotificationService;
        private readonly IPythonService? _pythonService;
        private IndicatorColor _snapshotAttentionHeat;
        private bool _syncingAttentionHeat;
        private int _snapshotWindowSize;
        private bool _isDisposed;

        public string TitleKey => "Settings_AIPredictions";
        public string IconKey => "SettingsAdvIcon";

        /// <summary>Manual install command copied to the clipboard (using <see cref="StockAnalyzer.Core.Services.ManualPipCommands.ClipboardShell"/>, embedded interpreter).</summary>
        public static string OnnxPipManualInstallCommand => StockAnalyzer.Core.Services.ManualPipCommands.Onnx(StockAnalyzer.Core.Services.ManualPipCommands.ClipboardShell, upgrade: false);

        /// <summary>Manual upgrade command copied to the clipboard (using <see cref="StockAnalyzer.Core.Services.ManualPipCommands.ClipboardShell"/>, embedded interpreter).</summary>
        public static string OnnxPipManualUpgradeCommand => StockAnalyzer.Core.Services.ManualPipCommands.Onnx(StockAnalyzer.Core.Services.ManualPipCommands.ClipboardShell, upgrade: true);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AutoButtonText))]
        private bool _isOnnxInstalled;

        public string AutoButtonText => LocalizationManager.Instance[IsOnnxInstalled ? "Settings_AIPredictions_Btn_AutoUpdate" : "Settings_AIPredictions_Btn_AutoInstall"] 
            ?? (IsOnnxInstalled ? "Automatic Update" : "Automatic Setup");

        [ObservableProperty]
        private string? _statusMessage;

        [ObservableProperty]
        private bool _isBusy;

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

        public AIPredictionsSettingsViewModel(
            IPredictionSettingsManager predictionSettingsManager,
            IMessenger? messenger = null,
            IDialogService? dialogService = null,
            IThemeManager? themeManager = null,
            IClipboardService? clipboardService = null,
            IToastNotificationService? toastNotificationService = null,
            IPythonService? pythonService = null)
        {
            _clipboardService = clipboardService ?? new DesignClipboardService();
            _toastNotificationService = toastNotificationService ?? new DesignToastNotificationService();
            _pythonService = pythonService;
            _messenger = messenger ?? WeakReferenceMessenger.Default;
            _dialogService = dialogService;
            _themeManager = themeManager;
            if (_themeManager is not null) _themeManager.PropertyChanged += OnThemeManagerChanged;
            _predictionSettingsManager = predictionSettingsManager;
            TakeSnapshot();
            InitializeFromSnapshot();
            _ = CheckOnnxInstalledAsync();
        }

        public AIPredictionsSettingsViewModel()
        {
            // Designer fallback
            _messenger = WeakReferenceMessenger.Default;
            _predictionSettingsManager = new DesignPredictionSettingsManager();
            _clipboardService = new DesignClipboardService();
            _toastNotificationService = new DesignToastNotificationService();
            TakeSnapshot();
            InitializeFromSnapshot();
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

        /// <summary>Opens (or brings to the front) the Tools > Prediction Models window, where models are activated, mixed into an ensemble and monitored.</summary>
        [RelayCommand]
        private Task OpenPredictionModels() => _dialogService?.ShowPredictionModelsWindowAsync() ?? Task.CompletedTask;

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
            // The charts predict again at once when the window the predictions use changed.
            if (windowChanged) _messenger.Send(new PredictionConfigurationChangedMessage());
        }

        public void RevertChanges()
        {
            if (_isDisposed) return;
            InitializeFromSnapshot(); // puts the snapshot colour back on the charts; the window size was never handed to the manager before Save
        }

        public void ResetToDefault()
        {
            if (_isDisposed) return;
            SelectedWindowSize = PredictionSettingsManager.DefaultWindowSize;
            if (_themeManager is not null)
                AttentionHeatColor = (_themeManager.CurrentTheme.IsDark ? ThemeColors.Dark : ThemeColors.Light).AttentionHeat;
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
                    await _pythonService.InstallPackagesAsync(StockAnalyzer.Core.Services.PythonPackageSets.OnnxTraining, forceUpgrade: isUpgrade, progress: progress);
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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            if (_themeManager is not null) _themeManager.PropertyChanged -= OnThemeManagerChanged;
            GC.SuppressFinalize(this);
        }
    }
}
