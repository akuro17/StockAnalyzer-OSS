using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Tickers;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    /// <summary>Settings &gt; Tickers page (Y:\Temp\sa_implementation_plan_TickersSettingsCategory.md). Mirrors
    /// <see cref="NotesSettingsViewModel"/>: edits are applied to the manager live (page live-preview contract),
    /// the snapshot taken at construction/save is what Discard restores.</summary>
    public partial class TickersSettingsViewModel : ViewModelBase, ISettingsPageViewModel
    {
        private readonly ITickersSettingsManager _manager;

        private bool _snapshotAutoPlayStopAtListEnd;
        private bool _snapshotAutoPlayStopOnListChange;
        private int _snapshotAutoPlayIntervalSeconds;
        private TickerColumnSelectionScope _snapshotColumnSelectionScope;

        // Set while a Selected* property is written back from the manager (rejected value / revert), so the
        // change handler does not push it to the manager again.
        private bool _isRestoringFromManager;

        public string TitleKey => "Settings_Tickers";
        public string IconKey => "SettingsTickersIcon";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private bool _selectedAutoPlayStopAtListEnd;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private bool _selectedAutoPlayStopOnListChange;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedAutoPlayIntervalSeconds;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private TickerColumnSelectionScope _selectedColumnSelectionScope;

        /// <summary>Bounds of the interval input; data-bound in XAML (no numeric literal in the view).</summary>
        public int AutoPlayMinIntervalSeconds => _manager.AutoPlayMinIntervalSeconds;
        public int AutoPlayMaxIntervalSeconds => _manager.AutoPlayMaxIntervalSeconds;

        /// <summary>ItemsSource for the column selection scope ComboBox.</summary>
        public static IReadOnlyList<TickerColumnSelectionScope> ColumnSelectionScopeOptions { get; } = Enum.GetValues<TickerColumnSelectionScope>();

        public TickersSettingsViewModel(ITickersSettingsManager manager)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        public TickersSettingsViewModel()
        {
            // Designer fallback
            _manager = new TickersSettingsManager(Microsoft.Extensions.Options.Options.Create(new TickersSettings()), null, System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"design_tickers_settings_{Guid.NewGuid():N}.json"));
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        private void TakeSnapshot()
        {
            _snapshotAutoPlayStopAtListEnd = _manager.AutoPlayStopAtListEnd;
            _snapshotAutoPlayStopOnListChange = _manager.AutoPlayStopOnListChange;
            _snapshotAutoPlayIntervalSeconds = _manager.AutoPlayIntervalSeconds;
            _snapshotColumnSelectionScope = _manager.ColumnSelectionScope;
        }

        private void InitializeFromSnapshot()
        {
            RestoreFromManager();
            OnPropertyChanged(nameof(IsModified));
        }

        private void RestoreFromManager()
        {
            _isRestoringFromManager = true;
            try
            {
                SelectedAutoPlayStopAtListEnd = _manager.AutoPlayStopAtListEnd;
                SelectedAutoPlayStopOnListChange = _manager.AutoPlayStopOnListChange;
                SelectedAutoPlayIntervalSeconds = _manager.AutoPlayIntervalSeconds;
                SelectedColumnSelectionScope = _manager.ColumnSelectionScope;
            }
            finally
            {
                _isRestoringFromManager = false;
            }
        }

        public bool IsModified =>
            SelectedAutoPlayStopAtListEnd != _snapshotAutoPlayStopAtListEnd ||
            SelectedAutoPlayStopOnListChange != _snapshotAutoPlayStopOnListChange ||
            SelectedAutoPlayIntervalSeconds != _snapshotAutoPlayIntervalSeconds ||
            SelectedColumnSelectionScope != _snapshotColumnSelectionScope;

        partial void OnSelectedAutoPlayStopAtListEndChanged(bool value)
        {
            if (_isRestoringFromManager) return;
            _manager.SetAutoPlayStopAtListEnd(value);
        }

        partial void OnSelectedAutoPlayStopOnListChangeChanged(bool value)
        {
            if (_isRestoringFromManager) return;
            _manager.SetAutoPlayStopOnListChange(value);
        }

        partial void OnSelectedAutoPlayIntervalSecondsChanged(int value)
        {
            if (_isRestoringFromManager) return;
            if (!_manager.SetAutoPlayIntervalSeconds(value))
            {
                // Rejected (outside the configured range): show the value that is actually applied.
                RestoreFromManager();
            }
        }

        partial void OnSelectedColumnSelectionScopeChanged(TickerColumnSelectionScope value)
        {
            if (_isRestoringFromManager) return;
            if (!_manager.SetColumnSelectionScope(value))
            {
                RestoreFromManager();
            }
        }

        public async Task SaveChangesAsync()
        {
            await _manager.SaveAsync();
            TakeSnapshot();
            OnPropertyChanged(nameof(IsModified));
        }

        public void RevertChanges()
        {
            _manager.SetAutoPlayStopAtListEnd(_snapshotAutoPlayStopAtListEnd);
            _manager.SetAutoPlayStopOnListChange(_snapshotAutoPlayStopOnListChange);
            _manager.SetAutoPlayIntervalSeconds(_snapshotAutoPlayIntervalSeconds);
            _manager.SetColumnSelectionScope(_snapshotColumnSelectionScope);
            InitializeFromSnapshot();
        }

        public void ResetToDefault()
        {
            SelectedAutoPlayStopAtListEnd = _manager.AutoPlayDefaultStopAtListEnd;
            SelectedAutoPlayStopOnListChange = _manager.AutoPlayDefaultStopOnListChange;
            SelectedAutoPlayIntervalSeconds = _manager.AutoPlayDefaultIntervalSeconds;
            SelectedColumnSelectionScope = default;
        }
    }
}
