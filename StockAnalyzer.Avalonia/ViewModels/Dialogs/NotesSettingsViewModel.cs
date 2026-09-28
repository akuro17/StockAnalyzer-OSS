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
    public partial class NotesSettingsViewModel : ViewModelBase, ISettingsPageViewModel
    {
        private readonly INotesSettingsManager _notesSettingsManager;

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
        private double _snapshotBodyFontSize;
        private IndicatorColor _snapshotBodyTextColor;
        private IndicatorColor _snapshotBodyBackgroundColor;
        private IndicatorColor _snapshotUrlColor;
        private IndicatorColor _snapshotHashtagColor;

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

        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private double _selectedBodyFontSize = NotesSettingsConstants.DefaultBodyFontSize;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedBodyTextColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyTextColorArgb);
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedBodyBackgroundColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyBackgroundColorArgb);

        // No on/off toggle (fix request: "チェックボックスのON/OFFが不要...トグル設定は不要") - the
        // ColorPicker is always active, defaulting to the same value as the body text color.
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedUrlColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultUrlColorArgb);
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsModified))] private IndicatorColor _selectedHashtagColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHashtagColorArgb);

        public NotesSettingsViewModel(INotesSettingsManager notesSettingsManager)
        {
            _notesSettingsManager = notesSettingsManager;
            TakeSnapshot();
            InitializeFromSnapshot();
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
            public double BodyFontSize => NotesSettingsConstants.DefaultBodyFontSize;
            public IndicatorColor BodyTextColor => IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyTextColorArgb);
            public IndicatorColor BodyBackgroundColor => IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyBackgroundColorArgb);
            public IndicatorColor UrlColor => IndicatorColor.FromUInt(NotesSettingsConstants.DefaultUrlColorArgb);
            public IndicatorColor HashtagColor => IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHashtagColorArgb);
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
            _snapshotBodyFontSize = _notesSettingsManager.BodyFontSize;
            _snapshotBodyTextColor = _notesSettingsManager.BodyTextColor;
            _snapshotBodyBackgroundColor = _notesSettingsManager.BodyBackgroundColor;
            _snapshotUrlColor = _notesSettingsManager.UrlColor;
            _snapshotHashtagColor = _notesSettingsManager.HashtagColor;
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
            SelectedBodyFontSize = _notesSettingsManager.BodyFontSize;
            SelectedBodyTextColor = _notesSettingsManager.BodyTextColor;
            SelectedBodyBackgroundColor = _notesSettingsManager.BodyBackgroundColor;
            SelectedUrlColor = _notesSettingsManager.UrlColor;
            SelectedHashtagColor = _notesSettingsManager.HashtagColor;
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
            SelectedBodyFontSize != _snapshotBodyFontSize ||
            SelectedBodyTextColor != _snapshotBodyTextColor ||
            SelectedBodyBackgroundColor != _snapshotBodyBackgroundColor ||
            SelectedUrlColor != _snapshotUrlColor ||
            SelectedHashtagColor != _snapshotHashtagColor;

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

        partial void OnSelectedBodyFontSizeChanged(double value) => _notesSettingsManager.SetBodyFontSize(value);

        partial void OnSelectedBodyTextColorChanged(IndicatorColor value) => _notesSettingsManager.SetBodyTextColor(value);

        partial void OnSelectedBodyBackgroundColorChanged(IndicatorColor value) => _notesSettingsManager.SetBodyBackgroundColor(value);

        partial void OnSelectedUrlColorChanged(IndicatorColor value) => _notesSettingsManager.SetUrlColor(value);

        partial void OnSelectedHashtagColorChanged(IndicatorColor value) => _notesSettingsManager.SetHashtagColor(value);

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
            _notesSettingsManager.SetBodyFontSize(_snapshotBodyFontSize);
            _notesSettingsManager.SetBodyTextColor(_snapshotBodyTextColor);
            _notesSettingsManager.SetBodyBackgroundColor(_snapshotBodyBackgroundColor);
            _notesSettingsManager.SetUrlColor(_snapshotUrlColor);
            _notesSettingsManager.SetHashtagColor(_snapshotHashtagColor);
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
            SelectedBodyFontSize = NotesSettingsConstants.DefaultBodyFontSize;
            SelectedBodyTextColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyTextColorArgb);
            SelectedBodyBackgroundColor = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyBackgroundColorArgb);
            SelectedUrlColor = SelectedBodyTextColor;
            SelectedHashtagColor = SelectedBodyTextColor;
        }
    }
}
