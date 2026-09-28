using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.Services
{
    public class PythonSettingsManager : IPythonSettingsManager
    {
        private static readonly string PythonSettingsFilePath = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath("user_python_settings.json");

        private bool _showUpdateConfirmationDialog = true;

        public bool ShowUpdateConfirmationDialog
        {
            get => _showUpdateConfirmationDialog;
            private set
            {
                if (_showUpdateConfirmationDialog != value)
                {
                    _showUpdateConfirmationDialog = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetShowUpdateConfirmationDialog(bool value)
        {
            ShowUpdateConfirmationDialog = value;
        }

        public async Task SaveAsync()
        {
            try
            {
                var data = new PythonPersistenceData
                {
                    ShowUpdateConfirmationDialog = ShowUpdateConfirmationDialog
                };

                await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(PythonSettingsFilePath, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save python settings: {ex.Message}");
            }
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(PythonSettingsFilePath))
                {
                    SetShowUpdateConfirmationDialog(true);
                    await SaveAsync();
                    return;
                }

                var data = await StockAnalyzer.Core.Common.AtomicJsonFile.LoadAsync<PythonPersistenceData?>(PythonSettingsFilePath);
                if (data.HasValue)
                {
                    SetShowUpdateConfirmationDialog(data.Value.ShowUpdateConfirmationDialog);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load python settings: {ex.Message}");
                SetShowUpdateConfirmationDialog(true);
            }
        }

        private readonly record struct PythonPersistenceData
        {
            public bool ShowUpdateConfirmationDialog { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
