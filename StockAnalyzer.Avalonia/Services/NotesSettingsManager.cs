using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Notes;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Avalonia.Services
{
    public class NotesSettingsManager : INotesSettingsManager
    {
        private static readonly string NotesSettingsFilePath = StockAnalyzer.Core.Common.PathDiscovery.ResolveConfigPath("user_notes_settings.json");

        // Matches ThemeManager's own JsonSerializerOptions: IndicatorColor round-trips as a
        // "#AARRGGBB" hex string via IndicatorColorJsonConverter.
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Converters = { new IndicatorColorJsonConverter() }
        };

        private readonly IThemeManager _themeManager;

        private int _readMoreMaxCharacters = NotesSettingsConstants.DefaultReadMoreMaxCharacters;
        private int _readMoreMaxLines = NotesSettingsConstants.DefaultReadMoreMaxLines;
        private int _threadCollapseThreshold = NotesSettingsConstants.DefaultThreadCollapseThreshold;
        private int _tailVisibleCount = NotesSettingsConstants.DefaultTailVisibleCount;
        private double _connectorLineLength = NotesSettingsConstants.DefaultConnectorLineLength;
        private double _dashLength = NotesSettingsConstants.DefaultDashLength;
        private int _timelinePageSize = NotesSettingsConstants.DefaultTimelinePageSize;
        private double _bodyFontSize = NotesSettingsConstants.DefaultBodyFontSize;
        private IndicatorColor _bodyTextColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyTextColorArgb);
        private IndicatorColor _bodyBackgroundColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyBackgroundColorArgb);
        private IndicatorColor _urlColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultUrlColorArgb);
        private IndicatorColor _hashtagColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHashtagColorArgb);

        public NotesSettingsManager(IThemeManager themeManager)
        {
            _themeManager = themeManager;
            _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;
        }

        private void OnThemeManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IThemeManager.CurrentTheme))
            {
                SyncColorsToTheme();
            }
        }

        /// <summary>
        /// Any of the four Notes colors still sitting at "the default for the other theme" is switched
        /// to the current theme's default; a color the user has explicitly customized away from both
        /// known defaults is left untouched.
        /// </summary>
        private void SyncColorsToTheme()
        {
            bool isDark = _themeManager.CurrentTheme.IsDark;
            uint targetTextArgb = NotesSettingsConstants.GetDefaultBodyTextColorArgb(isDark);
            uint otherTextArgb = NotesSettingsConstants.GetDefaultBodyTextColorArgb(!isDark);
            uint targetBackgroundArgb = NotesSettingsConstants.GetDefaultBodyBackgroundColorArgb(isDark);
            uint otherBackgroundArgb = NotesSettingsConstants.GetDefaultBodyBackgroundColorArgb(!isDark);

            if (BodyTextColor == IndicatorColor.FromUInt(otherTextArgb))
            {
                BodyTextColor = IndicatorColor.FromUInt(targetTextArgb);
            }
            if (BodyBackgroundColor == IndicatorColor.FromUInt(otherBackgroundArgb))
            {
                BodyBackgroundColor = IndicatorColor.FromUInt(targetBackgroundArgb);
            }
            if (UrlColor == IndicatorColor.FromUInt(otherTextArgb))
            {
                UrlColor = IndicatorColor.FromUInt(targetTextArgb);
            }
            if (HashtagColor == IndicatorColor.FromUInt(otherTextArgb))
            {
                HashtagColor = IndicatorColor.FromUInt(targetTextArgb);
            }
        }

        public int ReadMoreMaxCharacters
        {
            get => _readMoreMaxCharacters;
            private set
            {
                if (_readMoreMaxCharacters != value)
                {
                    _readMoreMaxCharacters = value;
                    OnPropertyChanged();
                }
            }
        }

        public int ReadMoreMaxLines
        {
            get => _readMoreMaxLines;
            private set
            {
                if (_readMoreMaxLines != value)
                {
                    _readMoreMaxLines = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetReadMoreMaxCharacters(int value)
        {
            if (value < 1)
                return;

            ReadMoreMaxCharacters = value;
        }

        public void SetReadMoreMaxLines(int value)
        {
            if (value < 1)
                return;

            ReadMoreMaxLines = value;
        }

        public int ThreadCollapseThreshold
        {
            get => _threadCollapseThreshold;
            private set
            {
                if (_threadCollapseThreshold != value)
                {
                    _threadCollapseThreshold = value;
                    OnPropertyChanged();
                }
            }
        }

        public int TailVisibleCount
        {
            get => _tailVisibleCount;
            private set
            {
                if (_tailVisibleCount != value)
                {
                    _tailVisibleCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public double ConnectorLineLength
        {
            get => _connectorLineLength;
            private set
            {
                if (Math.Abs(_connectorLineLength - value) > 0.001)
                {
                    _connectorLineLength = value;
                    OnPropertyChanged();
                }
            }
        }

        public double DashLength
        {
            get => _dashLength;
            private set
            {
                if (Math.Abs(_dashLength - value) > 0.001)
                {
                    _dashLength = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetThreadCollapseThreshold(int value)
        {
            if (value < 1)
                return;

            ThreadCollapseThreshold = value;
        }

        public void SetTailVisibleCount(int value)
        {
            if (value < 0)
                return;

            TailVisibleCount = value;
        }

        public void SetConnectorLineLength(double value)
        {
            if (value < 1.0)
                return;

            ConnectorLineLength = value;
        }

        public void SetDashLength(double value)
        {
            if (value < 1.0)
                return;

            DashLength = value;
        }

        public int TimelinePageSize
        {
            get => _timelinePageSize;
            private set
            {
                if (_timelinePageSize != value)
                {
                    _timelinePageSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetTimelinePageSize(int value)
        {
            if (value < 1)
                return;

            TimelinePageSize = value;
        }

        public double BodyFontSize
        {
            get => _bodyFontSize;
            private set
            {
                if (Math.Abs(_bodyFontSize - value) > 0.001)
                {
                    _bodyFontSize = value;
                    OnPropertyChanged();
                }
            }
        }

        public IndicatorColor BodyTextColor
        {
            get => _bodyTextColor;
            private set
            {
                if (_bodyTextColor != value)
                {
                    _bodyTextColor = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetBodyFontSize(double value)
        {
            if (value < NotesSettingsConstants.MinBodyFontSize || value > NotesSettingsConstants.MaxBodyFontSize)
                return;

            BodyFontSize = value;
        }

        public void SetBodyTextColor(IndicatorColor value)
        {
            BodyTextColor = value;
        }

        public IndicatorColor BodyBackgroundColor
        {
            get => _bodyBackgroundColor;
            private set
            {
                if (_bodyBackgroundColor != value)
                {
                    _bodyBackgroundColor = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetBodyBackgroundColor(IndicatorColor value)
        {
            BodyBackgroundColor = value;
        }

        public IndicatorColor UrlColor
        {
            get => _urlColor;
            private set
            {
                if (_urlColor != value)
                {
                    _urlColor = value;
                    OnPropertyChanged();
                }
            }
        }

        public IndicatorColor HashtagColor
        {
            get => _hashtagColor;
            private set
            {
                if (_hashtagColor != value)
                {
                    _hashtagColor = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetUrlColor(IndicatorColor value) => UrlColor = value;

        public void SetHashtagColor(IndicatorColor value) => HashtagColor = value;

        public async Task SaveAsync()
        {
            string tempPath = NotesSettingsFilePath + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(NotesSettingsFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var data = new NotesPersistenceData
                {
                    ReadMoreMaxCharacters = ReadMoreMaxCharacters,
                    ReadMoreMaxLines = ReadMoreMaxLines,
                    ThreadCollapseThreshold = ThreadCollapseThreshold,
                    TailVisibleCount = TailVisibleCount,
                    ConnectorLineLength = ConnectorLineLength,
                    DashLength = DashLength,
                    TimelinePageSize = TimelinePageSize,
                    BodyFontSize = BodyFontSize,
                    BodyTextColor = BodyTextColor,
                    BodyBackgroundColor = BodyBackgroundColor,
                    UrlColor = UrlColor,
                    HashtagColor = HashtagColor
                };

                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, data, JsonOptions);
                }

                if (File.Exists(NotesSettingsFilePath))
                {
                    File.Replace(tempPath, NotesSettingsFilePath, NotesSettingsFilePath + ".bak");
                }
                else
                {
                    File.Move(tempPath, NotesSettingsFilePath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save notes settings: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(NotesSettingsFilePath))
                {
                    SyncColorsToTheme();
                    await SaveAsync();
                    return;
                }

                using var stream = File.OpenRead(NotesSettingsFilePath);
                var data = await JsonSerializer.DeserializeAsync<NotesPersistenceData?>(stream, JsonOptions);
                if (data.HasValue)
                {
                    SetReadMoreMaxCharacters(data.Value.ReadMoreMaxCharacters);
                    SetReadMoreMaxLines(data.Value.ReadMoreMaxLines);

                    // Nullable so a file saved before these fields existed (missing key -> null,
                    // not 0) leaves the in-memory default (3/2/40/4) untouched rather than being
                    // misread as an explicit "0" - unlike ReadMoreMaxCharacters/Lines above, 0 is a
                    // legitimate TailVisibleCount value, so it can't reuse the same "value < 1 is
                    // ignored" guard to tell "absent" from "explicitly zero" apart.
                    if (data.Value.ThreadCollapseThreshold is { } threadCollapseThreshold)
                    {
                        SetThreadCollapseThreshold(threadCollapseThreshold);
                    }
                    if (data.Value.TailVisibleCount is { } tailVisibleCount)
                    {
                        SetTailVisibleCount(tailVisibleCount);
                    }
                    if (data.Value.ConnectorLineLength is { } connectorLineLength)
                    {
                        SetConnectorLineLength(connectorLineLength);
                    }
                    if (data.Value.DashLength is { } dashLength)
                    {
                        SetDashLength(dashLength);
                    }
                    if (data.Value.TimelinePageSize is { } timelinePageSize)
                    {
                        SetTimelinePageSize(timelinePageSize);
                    }
                    if (data.Value.BodyFontSize is { } bodyFontSize)
                    {
                        SetBodyFontSize(bodyFontSize);
                    }
                    if (data.Value.BodyTextColor is { } bodyTextColor)
                    {
                        SetBodyTextColor(bodyTextColor);
                    }
                    if (data.Value.BodyBackgroundColor is { } bodyBackgroundColor)
                    {
                        SetBodyBackgroundColor(bodyBackgroundColor);
                    }
                    if (data.Value.UrlColor is { } urlColor)
                    {
                        SetUrlColor(urlColor);
                    }
                    if (data.Value.HashtagColor is { } hashtagColor)
                    {
                        SetHashtagColor(hashtagColor);
                    }
                }

                SyncColorsToTheme();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load notes settings: {ex.Message}");
            }
        }

        private readonly record struct NotesPersistenceData
        {
            public int ReadMoreMaxCharacters { get; init; }
            public int ReadMoreMaxLines { get; init; }
            public int? ThreadCollapseThreshold { get; init; }
            public int? TailVisibleCount { get; init; }
            public double? ConnectorLineLength { get; init; }
            public double? DashLength { get; init; }
            public int? TimelinePageSize { get; init; }
            public double? BodyFontSize { get; init; }
            public IndicatorColor? BodyTextColor { get; init; }
            public IndicatorColor? BodyBackgroundColor { get; init; }
            public IndicatorColor? UrlColor { get; init; }
            public IndicatorColor? HashtagColor { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
