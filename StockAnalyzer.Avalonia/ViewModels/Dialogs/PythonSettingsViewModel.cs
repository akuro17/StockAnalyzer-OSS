using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class PythonSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly IPythonSettingsManager _pythonSettingsManager;
        private readonly IPythonService? _pythonService;
        private readonly IClipboardService? _clipboardService;
        private readonly IToastNotificationService? _toastNotificationService;

        private bool _snapshotShowUpdateConfirmationDialog;

        private bool _isDisposed;

        public string TitleKey => "Settings_Python";
        public string IconKey => "SettingsPythonIcon";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private bool _selectedShowUpdateConfirmationDialog = true;

        [ObservableProperty] private string? _statusMessage;

        [ObservableProperty] private bool _isBusy;

        public PythonSettingsViewModel(
            IPythonSettingsManager pythonSettingsManager,
            IPythonService? pythonService = null,
            IClipboardService? clipboardService = null,
            IToastNotificationService? toastNotificationService = null)
        {
            _pythonSettingsManager = pythonSettingsManager;
            _pythonService = pythonService;
            _clipboardService = clipboardService;
            _toastNotificationService = toastNotificationService;
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        public PythonSettingsViewModel()
        {
            // Designer fallback
            _pythonSettingsManager = new DesignPythonSettingsManager();
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        private class DesignPythonSettingsManager : IPythonSettingsManager
        {
            public bool ShowUpdateConfirmationDialog => true;
            public void SetShowUpdateConfirmationDialog(bool value) { }
            public Task SaveAsync() => Task.CompletedTask;
            public Task LoadAsync() => Task.CompletedTask;
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private void TakeSnapshot()
        {
            _snapshotShowUpdateConfirmationDialog = _pythonSettingsManager.ShowUpdateConfirmationDialog;
        }

        private void InitializeFromSnapshot()
        {
            SelectedShowUpdateConfirmationDialog = _pythonSettingsManager.ShowUpdateConfirmationDialog;
            OnPropertyChanged(nameof(IsModified));
        }

        public bool IsModified =>
            SelectedShowUpdateConfirmationDialog != _snapshotShowUpdateConfirmationDialog;

        partial void OnSelectedShowUpdateConfirmationDialogChanged(bool value)
        {
            if (_isDisposed) return;
            _pythonSettingsManager.SetShowUpdateConfirmationDialog(value);
        }

        public async Task SaveChangesAsync()
        {
            await _pythonSettingsManager.SaveAsync();
            TakeSnapshot();
            OnPropertyChanged(nameof(IsModified));
        }

        public void RevertChanges()
        {
            if (_isDisposed) return;
            _pythonSettingsManager.SetShowUpdateConfirmationDialog(_snapshotShowUpdateConfirmationDialog);
            InitializeFromSnapshot();
        }

        public void ResetToDefault()
        {
            if (_isDisposed) return;
            SelectedShowUpdateConfirmationDialog = true;
        }

        /// <summary>
        /// Python Library Update card (an action that takes effect when pressed; Apply, Cancel and Reset do not touch it): copies the update command
        /// of the startup dialog (<see cref="ManualPipCommands.Update"/>, using <see cref="ManualPipCommands.ClipboardShell"/>) to the clipboard.
        /// </summary>
        [RelayCommand]
        public async Task ManualUpdateLibrariesAsync()
        {
            if (_isDisposed || _clipboardService is null) return;
            await _clipboardService.SetTextAsync(ManualPipCommands.Update(ManualPipCommands.ClipboardShell));
            var msg = LocalizationManager.Instance["Settings_Python_Update_Copied"] ?? "Pip update command copied to clipboard.";
            StatusMessage = msg;
            _toastNotificationService?.ShowNotification(msg);
        }

        /// <summary>
        /// Upgrades the packages of <see cref="PythonPackageSets.Update"/> with <c>forceUpgrade: true</c>: the same package set and the same upgrade
        /// semantics as the manual command (<see cref="ManualPipCommands.UpdateArguments"/>).
        /// </summary>
        [RelayCommand]
        public async Task AutoUpdateLibrariesAsync()
        {
            if (_isDisposed || IsBusy || _pythonService is null) return;
            IsBusy = true;
            StatusMessage = LocalizationManager.Instance["Settings_Python_Update_Running"] ?? "Updating Python libraries...";
            // Progress<T> posts its callback asynchronously (thread pool when there is no synchronization context), so a late progress
            // message must never overwrite the final status. The final status is written under the same lock that guards the callback.
            // The assignment stays inside the lock on purpose: checking the flag under the lock and assigning after it would let a late
            // message land after the final status. PropertyChanged handlers only marshal to the UI thread and never take this private lock.
            var statusGate = new object();
            var finished = false;
            void SetFinalStatus(string? message)
            {
                lock (statusGate)
                {
                    finished = true;
                    StatusMessage = message;
                }
            }
            try
            {
                var progress = new Progress<string>(m =>
                {
                    lock (statusGate)
                    {
                        if (!finished) StatusMessage = m;
                    }
                });
                await _pythonService.InstallPackagesAsync(PythonPackageSets.Update, forceUpgrade: true, progress: progress);
                var done = LocalizationManager.Instance["Settings_Python_Update_Success"] ?? "Python library update completed successfully.";
                SetFinalStatus(done);
                _toastNotificationService?.ShowNotification(done);
            }
            catch (OperationCanceledException)
            {
                SetFinalStatus(null);
            }
            catch (Exception ex)
            {
                var error = string.Format(LocalizationManager.Instance["Settings_Python_Update_Error"] ?? "Update error: {0}", ex.Message);
                SetFinalStatus(error);
                _toastNotificationService?.ShowNotification(error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
