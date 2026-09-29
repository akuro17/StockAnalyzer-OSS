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

        private const string BaseFontSizeKey = "BaseFontSize";
        private const string TitleFontSizeKey = "TitleFontSize";
        private const string DetailFontSizeKey = "DetailFontSize";
        private const string HelperFontSizeKey = "HelperFontSize";
        private const string TooltipFontSizeKey = "TooltipFontSize";

        private const double MinFontSize = 12.0;
        private const double MaxFontSize = 24.0;

        private double _baseFontSize = FontDefaults.Base;
        private double _titleFontSize = FontDefaults.Title;
        private double _detailFontSize = FontDefaults.Detail;
        private double _helperFontSize = FontDefaults.Helper;
        private double _tooltipFontSize = FontDefaults.Tooltip;

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

        public double TooltipFontSize
        {
            get => _tooltipFontSize;
            private set
            {
                if (Math.Abs(_tooltipFontSize - value) > 0.001)
                {
                    _tooltipFontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetBaseFontSize(double size)
        {
            if (size < MinFontSize || size > MaxFontSize)
                return;

            BaseFontSize = size;
            UpdateAppResource(BaseFontSizeKey, size);
        }

        public void SetTitleFontSize(double size)
        {
            if (size < MinFontSize || size > MaxFontSize)
                return;

            TitleFontSize = size;
            UpdateAppResource(TitleFontSizeKey, size);
        }

        public void SetDetailFontSize(double size)
        {
            if (size < MinFontSize || size > MaxFontSize)
                return;

            DetailFontSize = size;
            UpdateAppResource(DetailFontSizeKey, size);
        }

        public void SetHelperFontSize(double size)
        {
            if (size < MinFontSize || size > MaxFontSize)
                return;

            HelperFontSize = size;
            UpdateAppResource(HelperFontSizeKey, size);
        }

        public void SetTooltipFontSize(double size)
        {
            if (size < MinFontSize || size > MaxFontSize)
                return;

            TooltipFontSize = size;
            UpdateAppResource(TooltipFontSizeKey, size);
        }

        /// <summary>
        /// Seeds the application's semantic font-size resources from <see cref="FontDefaults"/> so the
        /// defaults live in one place (App.axaml no longer declares them). Called once from
        /// <c>App.Initialize</c>; <see cref="LoadAsync"/> later overwrites them with saved user values.
        /// </summary>
        public static void SeedDefaultResources(global::Avalonia.Controls.IResourceDictionary resources)
        {
            resources[BaseFontSizeKey] = FontDefaults.Base;
            resources[TitleFontSizeKey] = FontDefaults.Title;
            resources[DetailFontSizeKey] = FontDefaults.Detail;
            resources[HelperFontSizeKey] = FontDefaults.Helper;
            resources[TooltipFontSizeKey] = FontDefaults.Tooltip;
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
                    HelperFontSize = HelperFontSize,
                    TooltipFontSize = TooltipFontSize
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
                    SetBaseFontSize(FontDefaults.Base);
                    SetTitleFontSize(FontDefaults.Title);
                    SetDetailFontSize(FontDefaults.Detail);
                    SetHelperFontSize(FontDefaults.Helper);
                    SetTooltipFontSize(FontDefaults.Tooltip);
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
                    // Pre-existing settings files predate this field and deserialize it as 0.0, which
                    // SetTooltipFontSize's [12,24] clamp rejects, safely keeping the 16.0 field default.
                    SetTooltipFontSize(val.TooltipFontSize == 0.0 ? FontDefaults.Tooltip : val.TooltipFontSize);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load font settings: {ex.Message}");
                SetBaseFontSize(FontDefaults.Base);
                SetTitleFontSize(FontDefaults.Title);
                SetDetailFontSize(FontDefaults.Detail);
                SetHelperFontSize(FontDefaults.Helper);
                SetTooltipFontSize(FontDefaults.Tooltip);
            }
        }

        private readonly record struct FontPersistenceData
        {
            public double BaseFontSize { get; init; }
            public double TitleFontSize { get; init; }
            public double DetailFontSize { get; init; }
            public double HelperFontSize { get; init; }
            public double TooltipFontSize { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
