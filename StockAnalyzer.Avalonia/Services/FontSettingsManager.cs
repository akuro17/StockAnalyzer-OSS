using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;

namespace StockAnalyzer.Avalonia.Services
{
    public class FontSettingsManager : IFontSettingsManager
    {
        private static readonly string FontSettingsFilePath = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath("user_font_settings.json");

        private double _baseFontSize = 16.0;
        private double _titleFontSize = 20.0;
        private double _detailFontSize = 12.0;
        private double _helperFontSize = 12.0;

        public double BaseFontSize
        {
            get => _baseFontSize;
            private set
            {
                if (Math.Abs(_baseFontSize - value) > 0.001)
                {
                    _baseFontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public double TitleFontSize
        {
            get => _titleFontSize;
            private set
            {
                if (Math.Abs(_titleFontSize - value) > 0.001)
                {
                    _titleFontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public double DetailFontSize
        {
            get => _detailFontSize;
            private set
            {
                if (Math.Abs(_detailFontSize - value) > 0.001)
                {
                    _detailFontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public double HelperFontSize
        {
            get => _helperFontSize;
            private set
            {
                if (Math.Abs(_helperFontSize - value) > 0.001)
                {
                    _helperFontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetBaseFontSize(double size)
        {
            if (size < 12.0 || size > 24.0)
                return;

            BaseFontSize = size;
            UpdateAppResource("BaseFontSize", size);
        }

        public void SetTitleFontSize(double size)
        {
            if (size < 12.0 || size > 24.0)
                return;

            TitleFontSize = size;
            UpdateAppResource("TitleFontSize", size);
        }

        public void SetDetailFontSize(double size)
        {
            if (size < 12.0 || size > 24.0)
                return;

            DetailFontSize = size;
            UpdateAppResource("DetailFontSize", size);
        }

        public void SetHelperFontSize(double size)
        {
            if (size < 12.0 || size > 24.0)
                return;

            HelperFontSize = size;
            UpdateAppResource("HelperFontSize", size);
        }

        private static void UpdateAppResource(string key, object value)
        {
            if (Application.Current == null) return;
            if (global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Application.Current.Resources[key] = value;
            }
            else
            {
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current != null)
                    {
                        Application.Current.Resources[key] = value;
                    }
                });
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var data = new FontPersistenceData
                {
                    BaseFontSize = BaseFontSize,
                    TitleFontSize = TitleFontSize,
                    DetailFontSize = DetailFontSize,
                    HelperFontSize = HelperFontSize
                };

                await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(FontSettingsFilePath, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save font settings: {ex.Message}");
            }
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(FontSettingsFilePath))
                {
                    SetBaseFontSize(16.0);
                    SetTitleFontSize(20.0);
                    SetDetailFontSize(12.0);
                    SetHelperFontSize(12.0);
                    await SaveAsync();
                    return;
                }

                var data = await StockAnalyzer.Core.Common.AtomicJsonFile.LoadAsync<FontPersistenceData?>(FontSettingsFilePath);
                if (data.HasValue)
                {
                    var val = data.Value;
                    SetBaseFontSize(val.BaseFontSize);
                    SetTitleFontSize(val.TitleFontSize);
                    SetDetailFontSize(val.DetailFontSize);
                    SetHelperFontSize(val.HelperFontSize);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load font settings: {ex.Message}");
                SetBaseFontSize(16.0);
                SetTitleFontSize(20.0);
                SetDetailFontSize(12.0);
                SetHelperFontSize(12.0);
            }
        }

        private readonly record struct FontPersistenceData
        {
            public double BaseFontSize { get; init; }
            public double TitleFontSize { get; init; }
            public double DetailFontSize { get; init; }
            public double HelperFontSize { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
