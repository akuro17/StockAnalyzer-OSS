using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services
{
    public class PredictionSettingsManager : IPredictionSettingsManager
    {
        private static readonly string PredictionSettingsFilePath = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath("user_prediction_settings.json");
        public const int DefaultWindowSize = 75;

        private int _windowSize = DefaultWindowSize;

        public int WindowSize
        {
            get => _windowSize;
            private set
            {
                if (_windowSize != value)
                {
                    _windowSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetWindowSize(int value)
        {
            if (value > 0)
            {
                WindowSize = value;
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var data = new PredictionPersistenceData
                {
                    WindowSize = WindowSize
                };

                await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(PredictionSettingsFilePath, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save prediction settings: {ex.Message}");
            }
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(PredictionSettingsFilePath))
                {
                    SetWindowSize(DefaultWindowSize);
                    await SaveAsync();
                    return;
                }

                var data = await StockAnalyzer.Core.Common.AtomicJsonFile.LoadAsync<PredictionPersistenceData?>(PredictionSettingsFilePath);
                if (data.HasValue && data.Value.WindowSize > 0)
                {
                    SetWindowSize(data.Value.WindowSize);
                }
                else
                {
                    SetWindowSize(DefaultWindowSize);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load prediction settings: {ex.Message}");
                SetWindowSize(DefaultWindowSize);
            }
        }

        private readonly record struct PredictionPersistenceData
        {
            public int WindowSize { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
