using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs
{
    public partial class NotesSettingsViewModel : ViewModelBase, ISettingsPageViewModel, IDisposable
    {
        private readonly INotesSettingsManager _notesSettingsManager;
        private bool _isDisposed;

        private double _snapshotThumbnailSizePixels;
        private NoteImageDisplayMode _snapshotImageDisplayMode;
        private int _snapshotMaxRenderedThumbnails;
        private int _snapshotReadMoreMaxCharacters;
        private int _snapshotReadMoreMaxLines;
        private int _snapshotThreadCollapseThreshold;
        private int _snapshotTailVisibleCount;
        private double _snapshotConnectorLineLength;
        private double _snapshotDashLength;
        private int _snapshotTimelinePageSize;
        private int _snapshotUpcomingCatalystsLimit;
        private int _snapshotUpcomingCatalystsPastDaysRetention;
        private double _snapshotBodyFontSize;

        private static readonly NotesColorKey[] ColorKeys = Enum.GetValues<NotesColorKey>();

        /// <summary>The saved-state baseline of every Appearance color: the picks of BOTH Light/Dark modes, so a pick made in the
        /// mode just left stays a pending edit (and Cancel undoes it) after the mode is switched while this page is open.</summary>
        private readonly Dictionary<NotesColorKey, ModeColorPicks> _snapshotColorPicks = new();

        public string TitleKey => "Settings_Notes";
        public string IconKey => "SettingsNotesIcon";

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedThumbnailSizePixels = NotesSettingsConstants.DefaultThumbnailSizePixels;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private NoteImageDisplayMode _selectedImageDisplayMode = NotesSettingsConstants.DefaultImageDisplayMode;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedMaxRenderedThumbnails = NotesSettingsConstants.DefaultMaxRenderedThumbnails;

        /// <summary>ItemsSource for the "Images" section's display-mode ComboBox (same
        /// Enum.GetValues-backed pattern as KagiSettingsViewModel.FallbackModeOptions).</summary>
        public static IReadOnlyList<NoteImageDisplayMode> ImageDisplayModeOptions { get; } = Enum.GetValues<NoteImageDisplayMode>();

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedReadMoreMaxCharacters = NotesSettingsConstants.DefaultReadMoreMaxCharacters;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedReadMoreMaxLines = NotesSettingsConstants.DefaultReadMoreMaxLines;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedThreadCollapseThreshold = NotesSettingsConstants.DefaultThreadCollapseThreshold;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedTailVisibleCount = NotesSettingsConstants.DefaultTailVisibleCount;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedConnectorLineLength = NotesSettingsConstants.DefaultConnectorLineLength;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedDashLength = NotesSettingsConstants.DefaultDashLength;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedTimelinePageSize = NotesSettingsConstants.DefaultTimelinePageSize;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedUpcomingCatalystsLimit = NotesSettingsConstants.DefaultUpcomingCatalystsLimit;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private int _selectedUpcomingCatalystsPastDaysRetention = NotesSettingsConstants.DefaultUpcomingCatalystsPastDaysRetention;

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedBodyFontSize = NotesSettingsConstants.DefaultBodyFontSize;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedBodyTextColor = NotesSettingsConstants.FallbackBodyTextColor;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedBodyBackgroundColor = NotesSettingsConstants.FallbackBodyBackgroundColor;

        // No on/off toggle (fix request: "チェックボックスのON/OFFが不要...トグル設定は不要") - the
        // ColorPicker is always active, defaulting to the same value as the body text color.
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedUrlColor = NotesSettingsConstants.FallbackBodyTextColor;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedHashtagColor = NotesSettingsConstants.FallbackBodyTextColor;

        // Background of text highlighted (mouse-selected) in a Note body; the default follows the Light/Dark theme (light blue / dark blue).
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedHighlightColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHighlightColorArgb);

        public NotesSettingsViewModel(INotesSettingsManager notesSettingsManager)
        {
            _notesSettingsManager = notesSettingsManager;
            TakeSnapshot();
            InitializeFromSnapshot();
            _notesSettingsManager.PropertyChanged += OnNotesSettingsChanged;
        }

        /// <summary>Every Appearance color follows the Light/Dark theme on its own when the mode is switched while this page is open.
        /// The page shows the new value; the baseline holds the picks of both modes, so it needs no adoption and the switch is not
        /// a pending edit. Otherwise the page would keep showing the color of the previous mode.</summary>
        private void OnNotesSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed) return;

            switch (e.PropertyName)
            {
                case nameof(INotesSettingsManager.BodyTextColor) when SelectedBodyTextColor != _notesSettingsManager.BodyTextColor:
                    SelectedBodyTextColor = _notesSettingsManager.BodyTextColor;
                    break;
                case nameof(INotesSettingsManager.BodyBackgroundColor) when SelectedBodyBackgroundColor != _notesSettingsManager.BodyBackgroundColor:
                    SelectedBodyBackgroundColor = _notesSettingsManager.BodyBackgroundColor;
                    break;
                case nameof(INotesSettingsManager.UrlColor) when SelectedUrlColor != _notesSettingsManager.UrlColor:
                    SelectedUrlColor = _notesSettingsManager.UrlColor;
                    break;
                case nameof(INotesSettingsManager.HashtagColor) when SelectedHashtagColor != _notesSettingsManager.HashtagColor:
                    SelectedHashtagColor = _notesSettingsManager.HashtagColor;
                    break;
                case nameof(INotesSettingsManager.HighlightColor) when SelectedHighlightColor != _notesSettingsManager.HighlightColor:
                    SelectedHighlightColor = _notesSettingsManager.HighlightColor;
                    break;
                default:
                    return;
            }

            OnPropertyChanged(nameof(IsModified));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _notesSettingsManager.PropertyChanged -= OnNotesSettingsChanged;
        }

        public NotesSettingsViewModel()
        {
            // Designer fallback
            _notesSettingsManager = new DesignNotesSettingsManager();
            TakeSnapshot();
            InitializeFromSnapshot();
        }

        private class DesignNotesSettingsManager : INotesSettingsManager
        {
            public double ThumbnailSizePixels => NotesSettingsConstants.DefaultThumbnailSizePixels;
            public NoteImageDisplayMode ImageDisplayMode => NotesSettingsConstants.DefaultImageDisplayMode;
            public int MaxRenderedThumbnails => NotesSettingsConstants.DefaultMaxRenderedThumbnails;
            public int ReadMoreMaxCharacters => NotesSettingsConstants.DefaultReadMoreMaxCharacters;
            public int ReadMoreMaxLines => NotesSettingsConstants.DefaultReadMoreMaxLines;
            public int ThreadCollapseThreshold => NotesSettingsConstants.DefaultThreadCollapseThreshold;
            public int TailVisibleCount => NotesSettingsConstants.DefaultTailVisibleCount;
            public double ConnectorLineLength => NotesSettingsConstants.DefaultConnectorLineLength;
            public double DashLength => NotesSettingsConstants.DefaultDashLength;
            public int TimelinePageSize => NotesSettingsConstants.DefaultTimelinePageSize;
            public int UpcomingCatalystsLimit => NotesSettingsConstants.DefaultUpcomingCatalystsLimit;
            public int UpcomingCatalystsPastDaysRetention => NotesSettingsConstants.DefaultUpcomingCatalystsPastDaysRetention;
            public double BodyFontSize => NotesSettingsConstants.DefaultBodyFontSize;
            public IndicatorColor BodyTextColor => NotesSettingsConstants.FallbackBodyTextColor;
            public IndicatorColor BodyBackgroundColor => NotesSettingsConstants.FallbackBodyBackgroundColor;
            public IndicatorColor UrlColor => NotesSettingsConstants.FallbackBodyTextColor;
            public IndicatorColor HashtagColor => NotesSettingsConstants.FallbackBodyTextColor;
            public void SetThumbnailSizePixels(double value) { }
            public void SetImageDisplayMode(NoteImageDisplayMode value) { }
            public void SetMaxRenderedThumbnails(int value) { }
            public void SetReadMoreMaxCharacters(int value) { }
            public void SetReadMoreMaxLines(int value) { }
            public void SetThreadCollapseThreshold(int value) { }
            public void SetTailVisibleCount(int value) { }
            public void SetConnectorLineLength(double value) { }
            public void SetDashLength(double value) { }
            public void SetTimelinePageSize(int value) { }
            public void SetUpcomingCatalystsLimit(int value) { }
            public void SetUpcomingCatalystsPastDaysRetention(int value) { }
            public void SetBodyFontSize(double value) { }
            public void SetBodyTextColor(IndicatorColor value) { }
            public void SetBodyBackgroundColor(IndicatorColor value) { }
            public void SetUrlColor(IndicatorColor value) { }
            public void SetHashtagColor(IndicatorColor value) { }
            public Task SaveAsync() => Task.CompletedTask;
            public Task LoadAsync() => Task.CompletedTask;
            public event PropertyChangedEventHandler? PropertyChanged;
        }

        private void TakeSnapshot()
        {
            _snapshotThumbnailSizePixels = _notesSettingsManager.ThumbnailSizePixels;
            _snapshotImageDisplayMode = _notesSettingsManager.ImageDisplayMode;
            _snapshotMaxRenderedThumbnails = _notesSettingsManager.MaxRenderedThumbnails;
            _snapshotReadMoreMaxCharacters = _notesSettingsManager.ReadMoreMaxCharacters;
            _snapshotReadMoreMaxLines = _notesSettingsManager.ReadMoreMaxLines;
            _snapshotThreadCollapseThreshold = _notesSettingsManager.ThreadCollapseThreshold;
            _snapshotTailVisibleCount = _notesSettingsManager.TailVisibleCount;
            _snapshotConnectorLineLength = _notesSettingsManager.ConnectorLineLength;
            _snapshotDashLength = _notesSettingsManager.DashLength;
            _snapshotTimelinePageSize = _notesSettingsManager.TimelinePageSize;
            _snapshotUpcomingCatalystsLimit = _notesSettingsManager.UpcomingCatalystsLimit;
            _snapshotUpcomingCatalystsPastDaysRetention = _notesSettingsManager.UpcomingCatalystsPastDaysRetention;
            _snapshotBodyFontSize = _notesSettingsManager.BodyFontSize;
            foreach (var key in ColorKeys)
            {
                _snapshotColorPicks[key] = _notesSettingsManager.GetColorPicks(key);
            }
        }

        private void InitializeFromSnapshot()
        {
            SelectedThumbnailSizePixels = _notesSettingsManager.ThumbnailSizePixels;
            SelectedImageDisplayMode = _notesSettingsManager.ImageDisplayMode;
            SelectedMaxRenderedThumbnails = _notesSettingsManager.MaxRenderedThumbnails;
            SelectedReadMoreMaxCharacters = _notesSettingsManager.ReadMoreMaxCharacters;
            SelectedReadMoreMaxLines = _notesSettingsManager.ReadMoreMaxLines;
            SelectedThreadCollapseThreshold = _notesSettingsManager.ThreadCollapseThreshold;
            SelectedTailVisibleCount = _notesSettingsManager.TailVisibleCount;
            SelectedConnectorLineLength = _notesSettingsManager.ConnectorLineLength;
            SelectedDashLength = _notesSettingsManager.DashLength;
            SelectedTimelinePageSize = _notesSettingsManager.TimelinePageSize;
            SelectedUpcomingCatalystsLimit = _notesSettingsManager.UpcomingCatalystsLimit;
            SelectedUpcomingCatalystsPastDaysRetention = _notesSettingsManager.UpcomingCatalystsPastDaysRetention;
            SelectedBodyFontSize = _notesSettingsManager.BodyFontSize;
            SelectedBodyTextColor = _notesSettingsManager.BodyTextColor;
            SelectedBodyBackgroundColor = _notesSettingsManager.BodyBackgroundColor;
            SelectedUrlColor = _notesSettingsManager.UrlColor;
            SelectedHashtagColor = _notesSettingsManager.HashtagColor;
            SelectedHighlightColor = _notesSettingsManager.HighlightColor;
            OnPropertyChanged(nameof(IsModified));
        }

        public bool IsModified =>
            SelectedThumbnailSizePixels != _snapshotThumbnailSizePixels ||
            SelectedImageDisplayMode != _snapshotImageDisplayMode ||
            SelectedMaxRenderedThumbnails != _snapshotMaxRenderedThumbnails ||
            SelectedReadMoreMaxCharacters != _snapshotReadMoreMaxCharacters ||
            SelectedReadMoreMaxLines != _snapshotReadMoreMaxLines ||
            SelectedThreadCollapseThreshold != _snapshotThreadCollapseThreshold ||
            SelectedTailVisibleCount != _snapshotTailVisibleCount ||
            SelectedConnectorLineLength != _snapshotConnectorLineLength ||
            SelectedDashLength != _snapshotDashLength ||
            SelectedTimelinePageSize != _snapshotTimelinePageSize ||
            SelectedUpcomingCatalystsLimit != _snapshotUpcomingCatalystsLimit ||
            SelectedUpcomingCatalystsPastDaysRetention != _snapshotUpcomingCatalystsPastDaysRetention ||
            SelectedBodyFontSize != _snapshotBodyFontSize ||
            AreColorPicksModified();

        /// <summary>True when the picks of any Appearance color differ from the saved state, in either mode.</summary>
        private bool AreColorPicksModified()
        {
            foreach (var key in ColorKeys)
            {
                if (_notesSettingsManager.GetColorPicks(key) != _snapshotColorPicks[key])
                {
                    return true;
                }
            }

            return false;
        }

        partial void OnSelectedThumbnailSizePixelsChanged(double value) => _notesSettingsManager.SetThumbnailSizePixels(value);

        partial void OnSelectedImageDisplayModeChanged(NoteImageDisplayMode value) => _notesSettingsManager.SetImageDisplayMode(value);

        partial void OnSelectedMaxRenderedThumbnailsChanged(int value) => _notesSettingsManager.SetMaxRenderedThumbnails(value);

        partial void OnSelectedReadMoreMaxCharactersChanged(int value) => _notesSettingsManager.SetReadMoreMaxCharacters(value);

        partial void OnSelectedReadMoreMaxLinesChanged(int value) => _notesSettingsManager.SetReadMoreMaxLines(value);

        partial void OnSelectedThreadCollapseThresholdChanged(int value) => _notesSettingsManager.SetThreadCollapseThreshold(value);

        partial void OnSelectedTailVisibleCountChanged(int value) => _notesSettingsManager.SetTailVisibleCount(value);

        partial void OnSelectedConnectorLineLengthChanged(double value) => _notesSettingsManager.SetConnectorLineLength(value);

        partial void OnSelectedDashLengthChanged(double value) => _notesSettingsManager.SetDashLength(value);

        partial void OnSelectedTimelinePageSizeChanged(int value) => _notesSettingsManager.SetTimelinePageSize(value);

        partial void OnSelectedUpcomingCatalystsLimitChanged(int value) => _notesSettingsManager.SetUpcomingCatalystsLimit(value);

        partial void OnSelectedUpcomingCatalystsPastDaysRetentionChanged(int value) => _notesSettingsManager.SetUpcomingCatalystsPastDaysRetention(value);

        partial void OnSelectedBodyFontSizeChanged(double value) => _notesSettingsManager.SetBodyFontSize(value);

        partial void OnSelectedBodyTextColorChanged(IndicatorColor value) => _notesSettingsManager.SetBodyTextColor(value);

        partial void OnSelectedBodyBackgroundColorChanged(IndicatorColor value) => _notesSettingsManager.SetBodyBackgroundColor(value);

        partial void OnSelectedUrlColorChanged(IndicatorColor value) => _notesSettingsManager.SetUrlColor(value);

        partial void OnSelectedHashtagColorChanged(IndicatorColor value) => _notesSettingsManager.SetHashtagColor(value);

        partial void OnSelectedHighlightColorChanged(IndicatorColor value) => _notesSettingsManager.SetHighlightColor(value);

        public async Task SaveChangesAsync()
        {
            await _notesSettingsManager.SaveAsync();
            TakeSnapshot();
            OnPropertyChanged(nameof(IsModified));
        }

        public void RevertChanges()
        {
            _notesSettingsManager.SetThumbnailSizePixels(_snapshotThumbnailSizePixels);
            _notesSettingsManager.SetImageDisplayMode(_snapshotImageDisplayMode);
            _notesSettingsManager.SetMaxRenderedThumbnails(_snapshotMaxRenderedThumbnails);
            _notesSettingsManager.SetReadMoreMaxCharacters(_snapshotReadMoreMaxCharacters);
            _notesSettingsManager.SetReadMoreMaxLines(_snapshotReadMoreMaxLines);
            _notesSettingsManager.SetThreadCollapseThreshold(_snapshotThreadCollapseThreshold);
            _notesSettingsManager.SetTailVisibleCount(_snapshotTailVisibleCount);
            _notesSettingsManager.SetConnectorLineLength(_snapshotConnectorLineLength);
            _notesSettingsManager.SetDashLength(_snapshotDashLength);
            _notesSettingsManager.SetTimelinePageSize(_snapshotTimelinePageSize);
            _notesSettingsManager.SetUpcomingCatalystsLimit(_snapshotUpcomingCatalystsLimit);
            _notesSettingsManager.SetUpcomingCatalystsPastDaysRetention(_snapshotUpcomingCatalystsPastDaysRetention);
            _notesSettingsManager.SetBodyFontSize(_snapshotBodyFontSize);
            foreach (var key in ColorKeys)
            {
                _notesSettingsManager.SetColorPicks(key, _snapshotColorPicks[key]);
            }
            InitializeFromSnapshot();
        }

        public void ResetToDefault()
        {
            SelectedThumbnailSizePixels = NotesSettingsConstants.DefaultThumbnailSizePixels;
            SelectedImageDisplayMode = NotesSettingsConstants.DefaultImageDisplayMode;
            SelectedMaxRenderedThumbnails = NotesSettingsConstants.DefaultMaxRenderedThumbnails;
            SelectedReadMoreMaxCharacters = NotesSettingsConstants.DefaultReadMoreMaxCharacters;
            SelectedReadMoreMaxLines = NotesSettingsConstants.DefaultReadMoreMaxLines;
            SelectedThreadCollapseThreshold = NotesSettingsConstants.DefaultThreadCollapseThreshold;
            SelectedTailVisibleCount = NotesSettingsConstants.DefaultTailVisibleCount;
            SelectedConnectorLineLength = NotesSettingsConstants.DefaultConnectorLineLength;
            SelectedDashLength = NotesSettingsConstants.DefaultDashLength;
            SelectedTimelinePageSize = NotesSettingsConstants.DefaultTimelinePageSize;
            SelectedUpcomingCatalystsLimit = NotesSettingsConstants.DefaultUpcomingCatalystsLimit;
            SelectedUpcomingCatalystsPastDaysRetention = NotesSettingsConstants.DefaultUpcomingCatalystsPastDaysRetention;
            SelectedBodyFontSize = NotesSettingsConstants.DefaultBodyFontSize;
            // Both modes of every Appearance color go back to the theme defaults, not only the mode shown.
            foreach (var key in ColorKeys)
            {
                _notesSettingsManager.SetColorPicks(key, default);
            }

            SelectedBodyTextColor = _notesSettingsManager.DefaultBodyTextColor;
            SelectedBodyBackgroundColor = _notesSettingsManager.DefaultBodyBackgroundColor;
            SelectedUrlColor = SelectedBodyTextColor;
            SelectedHashtagColor = SelectedBodyTextColor;
            SelectedHighlightColor = _notesSettingsManager.DefaultHighlightColor;
        }
    }
}
