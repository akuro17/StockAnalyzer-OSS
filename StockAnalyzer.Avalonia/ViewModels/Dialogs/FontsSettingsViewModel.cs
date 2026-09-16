using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Services;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class FontsSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly IFontSettingsManager _fontSettingsManager;
        
        private double _snapshotBaseFontSize;
        private double _snapshotTitleFontSize;
        private double _snapshotDetailFontSize;
        private double _snapshotHelperFontSize;
        
        private bool _isDisposed;

        public string TitleKey => "Settings_Fonts";
        public string IconKey => "SettingsFontsIcon";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedBaseFontSize = 16.0;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedTitleFontSize = 20.0;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedDetailFontSize = 12.0;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedHelperFontSize = 12.0;

        public ObservableCollection<double> AvailableFontSizes { get; } = new()
        {
            12.0, 13.0, 14.0, 15.0, 16.0, 17.0, 18.0, 20.0, 22.0, 24.0
        };

        public FontsSettingsViewModel(IFontSettingsManager fontSettingsManager)
        {
            _fontSettingsManager = fontSettingsManager;
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        public FontsSettingsViewModel()
        {
            // Designer fallback
            _fontSettingsManager = new DesignFontSettingsManager();
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        private class DesignFontSettingsManager : IFontSettingsManager
        {
            public double BaseFontSize => 16.0;
            public double TitleFontSize => 20.0;
            public double DetailFontSize => 12.0;
            public double HelperFontSize => 12.0;
            public void SetBaseFontSize(double size) {}
            public void SetTitleFontSize(double size) {}
            public void SetDetailFontSize(double size) {}
            public void SetHelperFontSize(double size) {}
            public Task SaveAsync() => Task.CompletedTask;
            public Task LoadAsync() => Task.CompletedTask;
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private void TakeSnapshot()
        {
            _snapshotBaseFontSize   = _fontSettingsManager.BaseFontSize;
            _snapshotTitleFontSize  = _fontSettingsManager.TitleFontSize;
            _snapshotDetailFontSize = _fontSettingsManager.DetailFontSize;
            _snapshotHelperFontSize = _fontSettingsManager.HelperFontSize;
        }

        private void InitializeFromSnapshot()
        {
            SelectedBaseFontSize   = _fontSettingsManager.BaseFontSize;
            SelectedTitleFontSize  = _fontSettingsManager.TitleFontSize;
            SelectedDetailFontSize = _fontSettingsManager.DetailFontSize;
            SelectedHelperFontSize = _fontSettingsManager.HelperFontSize;
            OnPropertyChanged(nameof(IsModified));
        }

        public bool IsModified => 
            Math.Abs(SelectedBaseFontSize - _snapshotBaseFontSize) > 0.001 ||
            Math.Abs(SelectedTitleFontSize - _snapshotTitleFontSize) > 0.001 ||
            Math.Abs(SelectedDetailFontSize - _snapshotDetailFontSize) > 0.001 ||
            Math.Abs(SelectedHelperFontSize - _snapshotHelperFontSize) > 0.001;

        partial void OnSelectedBaseFontSizeChanged(double value)
        {
            if (_isDisposed) return;
            _fontSettingsManager.SetBaseFontSize(value);
        }

        partial void OnSelectedTitleFontSizeChanged(double value)
        {
            if (_isDisposed) return;
            _fontSettingsManager.SetTitleFontSize(value);
        }

        partial void OnSelectedDetailFontSizeChanged(double value)
        {
            if (_isDisposed) return;
            _fontSettingsManager.SetDetailFontSize(value);
        }

        partial void OnSelectedHelperFontSizeChanged(double value)
        {
            if (_isDisposed) return;
            _fontSettingsManager.SetHelperFontSize(value);
        }

        public async Task SaveChangesAsync()
        {
            await _fontSettingsManager.SaveAsync();
            TakeSnapshot();
            OnPropertyChanged(nameof(IsModified));
        }

        public void RevertChanges()
        {
            if (_isDisposed) return;
            _fontSettingsManager.SetBaseFontSize(_snapshotBaseFontSize);
            _fontSettingsManager.SetTitleFontSize(_snapshotTitleFontSize);
            _fontSettingsManager.SetDetailFontSize(_snapshotDetailFontSize);
            _fontSettingsManager.SetHelperFontSize(_snapshotHelperFontSize);
            InitializeFromSnapshot();
        }

        public void ResetToDefault()
        {
            if (_isDisposed) return;
            SelectedBaseFontSize   = 16.0;
            SelectedTitleFontSize  = 20.0;
            SelectedDetailFontSize = 12.0;
            SelectedHelperFontSize = 12.0;
        }

        [RelayCommand]
        private void ResetFonts() => ResetToDefault();

        [RelayCommand]
        private async Task SaveFonts()
        {
            await SaveChangesAsync();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
