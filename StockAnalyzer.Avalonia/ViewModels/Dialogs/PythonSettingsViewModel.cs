using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class PythonSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly IPythonSettingsManager _pythonSettingsManager;

        private bool _snapshotShowUpdateConfirmationDialog;

        private bool _isDisposed;

        public string TitleKey => "Settings_Python";
        public string IconKey => "SettingsPythonIcon";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private bool _selectedShowUpdateConfirmationDialog = true;

        public PythonSettingsViewModel(IPythonSettingsManager pythonSettingsManager)
        {
            _pythonSettingsManager = pythonSettingsManager;
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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
