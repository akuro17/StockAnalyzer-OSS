using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.ViewModels
{
    /// <summary>
    /// ViewModel for an individual synchronization item in the progress grid.
    /// </summary>
    public partial class SyncItemViewModel : ObservableObject
    {
        private readonly SyncItemModel _model;

        public SyncItemViewModel(SyncItemModel model)
        {
            _model = model;
            _status = model.Status;
            _progress = model.Progress;
            _progressText = model.ProgressText;
            _errorMessage = model.ErrorMessage;
            _lastUpdatedText = model.LastUpdated?.ToString("HH:mm:ss") ?? "-";
            
            RetryCommand = new AsyncRelayCommand(OnRetry, CanRetry);
        }

        /// <summary>
        /// Reusable progress reporter instance to avoid allocations during retry.
        /// </summary>
        public IProgress<int>? ProgressReporter { get; set; }

        /// <summary>
        /// Stock symbol.
        /// </summary>
        public string Symbol => _model.Symbol;

        /// <summary>
        /// Data period.
        /// </summary>
        public string Period => _model.Period;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private SyncStatus _status;

        [ObservableProperty]
        private double _progress;

        [ObservableProperty]
        private string _progressText;

        [ObservableProperty]
        private string? _errorMessage;

        [ObservableProperty]
        private string _lastUpdatedText;

        /// <summary>
        /// Command to retry this specific item.
        /// </summary>
        public IAsyncRelayCommand RetryCommand { get; }

        /// <summary>
        /// Event triggered when retry is requested.
        /// </summary>
        public event Func<SyncItemViewModel, CancellationToken, Task>? RequestRetry;

        private async Task OnRetry(CancellationToken token)
        {
            if (RequestRetry != null)
            {
                await RequestRetry(this, token);
            }
        }

        private bool CanRetry() => Status == SyncStatus.Error || Status == SyncStatus.Canceled || Status == SyncStatus.Waiting;

        public void MarkWaiting()
        {
            _model.Status = SyncStatus.Waiting;
            _model.Progress = 0;
            _model.ProgressText = "Waiting";
            _model.ErrorMessage = null;
            _model.LastUpdated = DateTime.Now;
            UpdateFromModel();
        }

        /// <summary>
        /// Syncs the ViewModel state with the underlying model.
        /// </summary>
        public void UpdateFromModel()
        {
            Status = _model.Status;
            Progress = _model.Progress;
            ProgressText = _model.ProgressText;
            ErrorMessage = _model.ErrorMessage;
            LastUpdatedText = _model.LastUpdated?.ToString("HH:mm:ss") ?? "-";
        }
    }
}
