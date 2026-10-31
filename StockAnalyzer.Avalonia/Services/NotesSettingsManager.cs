using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
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

        private double _thumbnailSizePixels = NotesSettingsConstants.DefaultThumbnailSizePixels;
        private NoteImageDisplayMode _imageDisplayMode = NotesSettingsConstants.DefaultImageDisplayMode;
        private int _maxRenderedThumbnails = NotesSettingsConstants.DefaultMaxRenderedThumbnails;
        private int _readMoreMaxCharacters = NotesSettingsConstants.DefaultReadMoreMaxCharacters;
        private int _readMoreMaxLines = NotesSettingsConstants.DefaultReadMoreMaxLines;
        private int _threadCollapseThreshold = NotesSettingsConstants.DefaultThreadCollapseThreshold;
        private int _tailVisibleCount = NotesSettingsConstants.DefaultTailVisibleCount;
        private double _connectorLineLength = NotesSettingsConstants.DefaultConnectorLineLength;
        private double _dashLength = NotesSettingsConstants.DefaultDashLength;
        private int _timelinePageSize = NotesSettingsConstants.DefaultTimelinePageSize;
        private int _upcomingCatalystsLimit = NotesSettingsConstants.DefaultUpcomingCatalystsLimit;
        private int _upcomingCatalystsPastDaysRetention = NotesSettingsConstants.DefaultUpcomingCatalystsPastDaysRetention;
        private double _bodyFontSize = NotesSettingsConstants.DefaultBodyFontSize;
        private static readonly NotesColorKey[] ColorKeys = Enum.GetValues<NotesColorKey>();

        /// <summary>The picks of every Appearance color, one entry per <see cref="NotesColorKey"/>: a color the user picked for a
        /// Light/Dark mode belongs to that mode only; an unpicked mode shows the default of its theme.</summary>
        private readonly Dictionary<NotesColorKey, ModeColorPicks> _colorPicks = new();

        /// <summary>The effective color last announced through <see cref="PropertyChanged"/>, per key (a mode switch or a Theme
        /// edit moves the effective color without any pick being assigned).</summary>
        private readonly Dictionary<NotesColorKey, IndicatorColor> _announcedColors = new();

        public NotesSettingsManager(IThemeManager themeManager)
        {
            _themeManager = themeManager;
            _themeManager.PropertyChanged += OnThemeManagerPropertyChanged;
            foreach (var key in ColorKeys)
            {
                _colorPicks[key] = default;
                _announcedColors[key] = EffectiveColorOf(key);
            }
        }

        private void OnThemeManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IThemeManager.CurrentTheme))
            {
                SyncColorsToTheme();
            }
        }

        /// <summary>The colour of Settings &gt; Theme &gt; "App Background" (<see cref="ThemeColors.ShellBackground"/>, owner decision
        /// D-1) - the single place that decides which theme colour the Notes background follows.</summary>
        private static IndicatorColor ThemeBackgroundOf(ThemeColors theme) => theme.ShellBackground;

        /// <summary>The Theme "App Background" of the theme in effect, including a value the user changed in Settings &gt; Theme.</summary>
        public IndicatorColor DefaultBodyBackgroundColor => ThemeBackgroundOf(_themeManager.CurrentTheme);

        private bool IsDarkTheme => _themeManager.CurrentTheme.IsDark;

        /// <summary>The default of <paramref name="key"/> for the theme in effect: the Theme "App Background" for the background,
        /// the Theme text color for the three text colors, the built-in blue for the highlight.</summary>
        private IndicatorColor DefaultColorOf(NotesColorKey key) => key switch
        {
            NotesColorKey.BodyBackground => DefaultBodyBackgroundColor,
            NotesColorKey.Highlight => DefaultHighlightColor,
            NotesColorKey.BodyText or NotesColorKey.Url or NotesColorKey.Hashtag => DefaultBodyTextColor,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, $"Unhandled {nameof(NotesColorKey)} value."),
        };

        /// <summary>What a color shows now: the pick made for the mode in effect, otherwise that theme's default. A pick made in the
        /// other mode is never used, so a color chosen for one mode can never leave the other mode unreadable.</summary>
        private IndicatorColor EffectiveColorOf(NotesColorKey key)
        {
            var picks = _colorPicks[key];
            return (IsDarkTheme ? picks.Dark : picks.Light) ?? DefaultColorOf(key);
        }

        private static string PropertyNameOf(NotesColorKey key) => key switch
        {
            NotesColorKey.BodyText => nameof(BodyTextColor),
            NotesColorKey.BodyBackground => nameof(BodyBackgroundColor),
            NotesColorKey.Url => nameof(UrlColor),
            NotesColorKey.Hashtag => nameof(HashtagColor),
            NotesColorKey.Highlight => nameof(HighlightColor),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, $"Unhandled {nameof(NotesColorKey)} value."),
        };

        /// <summary>Picks <paramref name="value"/> for the mode in effect. Picking the theme's own default clears the pick, so the color
        /// keeps following the theme (including later edits in Settings &gt; Theme).</summary>
        private void SetColor(NotesColorKey key, IndicatorColor value)
        {
            IndicatorColor? pick = value == DefaultColorOf(key) ? null : value;
            var picks = _colorPicks[key];
            _colorPicks[key] = IsDarkTheme ? picks with { Dark = pick } : picks with { Light = pick };
            NotifyColorsIfChanged();
        }

        /// <summary>Raises <see cref="PropertyChanged"/> for every color whose effective value moved since it was last announced.</summary>
        private void NotifyColorsIfChanged()
        {
            foreach (var key in ColorKeys)
            {
                var current = EffectiveColorOf(key);
                if (current == _announcedColors[key]) continue;

                _announcedColors[key] = current;
                OnPropertyChanged(PropertyNameOf(key));
            }
        }

        public ModeColorPicks GetColorPicks(NotesColorKey key) => _colorPicks[key];

        public void SetColorPicks(NotesColorKey key, ModeColorPicks picks)
        {
            _colorPicks[key] = picks;
            NotifyColorsIfChanged();
        }

        /// <summary>A Light/Dark switch or a Settings &gt; Theme edit changes the defaults, hence every color that has no pick for the
        /// mode in effect; announce them.</summary>
        private void SyncColorsToTheme() => NotifyColorsIfChanged();

        public double ThumbnailSizePixels
        {
            get => _thumbnailSizePixels;
            private set
            {
                if (Math.Abs(_thumbnailSizePixels - value) > 0.001)
                {
                    _thumbnailSizePixels = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetThumbnailSizePixels(double value)
        {
            if (value < NotesSettingsConstants.MinThumbnailSizePixels || value > NotesSettingsConstants.MaxThumbnailSizePixels)
                return;

            ThumbnailSizePixels = value;
        }

        public NoteImageDisplayMode ImageDisplayMode
        {
            get => _imageDisplayMode;
            private set
            {
                if (_imageDisplayMode != value)
                {
                    _imageDisplayMode = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetImageDisplayMode(NoteImageDisplayMode value)
        {
            if (!Enum.IsDefined(value))
                return;

            ImageDisplayMode = value;
        }

        public int MaxRenderedThumbnails
        {
            get => _maxRenderedThumbnails;
            private set
            {
                if (_maxRenderedThumbnails != value)
                {
                    _maxRenderedThumbnails = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetMaxRenderedThumbnails(int value)
        {
            if (value < NotesSettingsConstants.MinRenderedThumbnailsLimit || value > NotesSettingsConstants.MaxRenderedThumbnailsLimit)
                return;

            MaxRenderedThumbnails = value;
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

        public int UpcomingCatalystsLimit
        {
            get => _upcomingCatalystsLimit;
            private set
            {
                if (_upcomingCatalystsLimit != value)
                {
                    _upcomingCatalystsLimit = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetUpcomingCatalystsLimit(int value)
        {
            if (value < NotesSettingsConstants.MinUpcomingCatalystsLimit || value > NotesSettingsConstants.MaxUpcomingCatalystsLimit)
                return;

            UpcomingCatalystsLimit = value;
        }

        public int UpcomingCatalystsPastDaysRetention
        {
            get => _upcomingCatalystsPastDaysRetention;
            private set
            {
                if (_upcomingCatalystsPastDaysRetention != value)
                {
                    _upcomingCatalystsPastDaysRetention = value;
                    OnPropertyChanged();
                }
            }
        }

        public void SetUpcomingCatalystsPastDaysRetention(int value)
        {
            if (value < NotesSettingsConstants.MinUpcomingCatalystsPastDaysRetention || value > NotesSettingsConstants.MaxUpcomingCatalystsPastDaysRetention)
                return;

            UpcomingCatalystsPastDaysRetention = value;
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

        public IndicatorColor BodyTextColor => EffectiveColorOf(NotesColorKey.BodyText);

        public void SetBodyFontSize(double value)
        {
            if (value < NotesSettingsConstants.MinBodyFontSize || value > NotesSettingsConstants.MaxBodyFontSize)
                return;

            BodyFontSize = value;
        }

        public void SetBodyTextColor(IndicatorColor value) => SetColor(NotesColorKey.BodyText, value);

        public IndicatorColor BodyBackgroundColor => EffectiveColorOf(NotesColorKey.BodyBackground);

        public void SetBodyBackgroundColor(IndicatorColor value) => SetColor(NotesColorKey.BodyBackground, value);

        public IndicatorColor UrlColor => EffectiveColorOf(NotesColorKey.Url);

        public IndicatorColor HashtagColor => EffectiveColorOf(NotesColorKey.Hashtag);

        public IndicatorColor HighlightColor => EffectiveColorOf(NotesColorKey.Highlight);

        /// <summary>The built-in highlight color of the theme currently in effect (a light blue in the Light theme, a dark
        /// blue in the Dark theme) - what a color still equal to "the other theme's default" is switched to, and what Reset restores.</summary>
        public IndicatorColor DefaultHighlightColor =>
            IndicatorColor.FromUInt(NotesSettingsConstants.GetDefaultHighlightColorArgb(_themeManager.CurrentTheme.IsDark));

        /// <summary>The built-in body text color of the theme currently in effect (dark text in the Light theme, light text in
        /// the Dark theme); what Reset restores for the text, URL and hashtag colors.</summary>
        public IndicatorColor DefaultBodyTextColor =>
            NotesSettingsConstants.GetDefaultBodyTextColor(_themeManager.CurrentTheme.IsDark);

        public void SetHighlightColor(IndicatorColor value) => SetColor(NotesColorKey.Highlight, value);

        public void SetUrlColor(IndicatorColor value) => SetColor(NotesColorKey.Url, value);

        public void SetHashtagColor(IndicatorColor value) => SetColor(NotesColorKey.Hashtag, value);

        public async Task SaveAsync()
        {
            try
            {
                var data = new NotesPersistenceData
                {
                    ThumbnailSizePixels = ThumbnailSizePixels,
                    ImageDisplayMode = ImageDisplayMode,
                    MaxRenderedThumbnails = MaxRenderedThumbnails,
                    ReadMoreMaxCharacters = ReadMoreMaxCharacters,
                    ReadMoreMaxLines = ReadMoreMaxLines,
                    ThreadCollapseThreshold = ThreadCollapseThreshold,
                    TailVisibleCount = TailVisibleCount,
                    ConnectorLineLength = ConnectorLineLength,
                    DashLength = DashLength,
                    TimelinePageSize = TimelinePageSize,
                    UpcomingCatalystsLimit = UpcomingCatalystsLimit,
                    UpcomingCatalystsPastDaysRetention = UpcomingCatalystsPastDaysRetention,
                    BodyFontSize = BodyFontSize,
                    // Every Appearance color is stored as its per-mode picks. The older single-color keys (BodyTextColor,
                    // BodyBackgroundColor, UrlColor, HashtagColor, HighlightColor) are neither written nor read (owner decision
                    // D-2 / D-3: no migration), so such a file shows the theme defaults until a color is picked again.
                    BodyTextColorDark = _colorPicks[NotesColorKey.BodyText].Dark,
                    BodyTextColorLight = _colorPicks[NotesColorKey.BodyText].Light,
                    BodyBackgroundColorDark = _colorPicks[NotesColorKey.BodyBackground].Dark,
                    BodyBackgroundColorLight = _colorPicks[NotesColorKey.BodyBackground].Light,
                    UrlColorDark = _colorPicks[NotesColorKey.Url].Dark,
                    UrlColorLight = _colorPicks[NotesColorKey.Url].Light,
                    HashtagColorDark = _colorPicks[NotesColorKey.Hashtag].Dark,
                    HashtagColorLight = _colorPicks[NotesColorKey.Hashtag].Light,
                    HighlightColorDark = _colorPicks[NotesColorKey.Highlight].Dark,
                    HighlightColorLight = _colorPicks[NotesColorKey.Highlight].Light
                };

                using (await StockAnalyzer.Core.Common.SerializedPathGate.AcquireAsync(NotesSettingsFilePath).ConfigureAwait(false))
                {
                    await StockAnalyzer.Core.Common.AtomicJsonFile.SaveAsync(NotesSettingsFilePath, data, JsonOptions);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save notes settings: {ex.Message}");
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

                var data = await StockAnalyzer.Core.Common.AtomicJsonFile.LoadAsync<NotesPersistenceData?>(NotesSettingsFilePath, JsonOptions);
                if (data.HasValue)
                {
                    if (data.Value.ThumbnailSizePixels is { } thumbnailSizePixels)
                    {
                        SetThumbnailSizePixels(thumbnailSizePixels);
                    }
                    if (data.Value.ImageDisplayMode is { } imageDisplayMode)
                    {
                        SetImageDisplayMode(imageDisplayMode);
                    }
                    if (data.Value.MaxRenderedThumbnails is { } maxRenderedThumbnails)
                    {
                        SetMaxRenderedThumbnails(maxRenderedThumbnails);
                    }
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
                    if (data.Value.UpcomingCatalystsLimit is { } upcomingCatalystsLimit)
                    {
                        SetUpcomingCatalystsLimit(upcomingCatalystsLimit);
                    }
                    if (data.Value.UpcomingCatalystsPastDaysRetention is { } upcomingCatalystsPastDaysRetention)
                    {
                        SetUpcomingCatalystsPastDaysRetention(upcomingCatalystsPastDaysRetention);
                    }
                    if (data.Value.BodyFontSize is { } bodyFontSize)
                    {
                        SetBodyFontSize(bodyFontSize);
                    }
                    // Picks are stored per mode. A file written before that has only the single-color keys, which are ignored
                    // on purpose (owner decision D-2 / D-3): a missing pick means "the default of that theme".
                    _colorPicks[NotesColorKey.BodyText] = new ModeColorPicks(data.Value.BodyTextColorDark, data.Value.BodyTextColorLight);
                    _colorPicks[NotesColorKey.BodyBackground] = new ModeColorPicks(data.Value.BodyBackgroundColorDark, data.Value.BodyBackgroundColorLight);
                    _colorPicks[NotesColorKey.Url] = new ModeColorPicks(data.Value.UrlColorDark, data.Value.UrlColorLight);
                    _colorPicks[NotesColorKey.Hashtag] = new ModeColorPicks(data.Value.HashtagColorDark, data.Value.HashtagColorLight);
                    _colorPicks[NotesColorKey.Highlight] = new ModeColorPicks(data.Value.HighlightColorDark, data.Value.HighlightColorLight);
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
            public double? ThumbnailSizePixels { get; init; }
            public NoteImageDisplayMode? ImageDisplayMode { get; init; }
            public int? MaxRenderedThumbnails { get; init; }
            public int ReadMoreMaxCharacters { get; init; }
            public int ReadMoreMaxLines { get; init; }
            public int? ThreadCollapseThreshold { get; init; }
            public int? TailVisibleCount { get; init; }
            public double? ConnectorLineLength { get; init; }
            public double? DashLength { get; init; }
            public int? TimelinePageSize { get; init; }
            public int? UpcomingCatalystsLimit { get; init; }
            public int? UpcomingCatalystsPastDaysRetention { get; init; }
            public double? BodyFontSize { get; init; }
            public IndicatorColor? BodyTextColorDark { get; init; }
            public IndicatorColor? BodyTextColorLight { get; init; }
            public IndicatorColor? BodyBackgroundColorDark { get; init; }
            public IndicatorColor? BodyBackgroundColorLight { get; init; }
            public IndicatorColor? UrlColorDark { get; init; }
            public IndicatorColor? UrlColorLight { get; init; }
            public IndicatorColor? HashtagColorDark { get; init; }
            public IndicatorColor? HashtagColorLight { get; init; }
            public IndicatorColor? HighlightColorDark { get; init; }
            public IndicatorColor? HighlightColorLight { get; init; }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
