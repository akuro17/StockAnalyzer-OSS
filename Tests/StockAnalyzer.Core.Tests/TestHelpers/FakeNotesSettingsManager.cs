using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Services.Notes;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>Minimal INotesSettingsManager test double for Core.Tests, mirroring
/// StockAnalyzer.Avalonia.Tests.TestHelpers.FakeNotesSettingsManager (a separate project, not
/// referenceable from here). Defaults match NotesSettingsConstants (production defaults).</summary>
public sealed class FakeNotesSettingsManager : INotesSettingsManager
{
    public double ThumbnailSizePixels { get; private set; } = NotesSettingsConstants.DefaultThumbnailSizePixels;
    public NoteImageDisplayMode ImageDisplayMode { get; private set; } = NotesSettingsConstants.DefaultImageDisplayMode;
    public int MaxRenderedThumbnails { get; private set; } = NotesSettingsConstants.DefaultMaxRenderedThumbnails;
    public int ReadMoreMaxCharacters { get; private set; } = NotesSettingsConstants.DefaultReadMoreMaxCharacters;
    public int ReadMoreMaxLines { get; private set; } = NotesSettingsConstants.DefaultReadMoreMaxLines;
    public int ThreadCollapseThreshold { get; private set; } = NotesSettingsConstants.DefaultThreadCollapseThreshold;
    public int TailVisibleCount { get; private set; } = NotesSettingsConstants.DefaultTailVisibleCount;
    public double ConnectorLineLength { get; private set; } = NotesSettingsConstants.DefaultConnectorLineLength;
    public double DashLength { get; private set; } = NotesSettingsConstants.DefaultDashLength;
    public int TimelinePageSize { get; private set; } = NotesSettingsConstants.DefaultTimelinePageSize;
    public double BodyFontSize { get; private set; } = NotesSettingsConstants.DefaultBodyFontSize;
    public IndicatorColor BodyTextColor { get; private set; } = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyTextColorArgb);
    public IndicatorColor BodyBackgroundColor { get; private set; } = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultBodyBackgroundColorArgb);
    public IndicatorColor UrlColor { get; private set; } = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultUrlColorArgb);
    public IndicatorColor HashtagColor { get; private set; } = IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHashtagColorArgb);

    public void SetThumbnailSizePixels(double value) { ThumbnailSizePixels = value; RaisePropertyChanged(nameof(ThumbnailSizePixels)); }
    public void SetImageDisplayMode(NoteImageDisplayMode value) { ImageDisplayMode = value; RaisePropertyChanged(nameof(ImageDisplayMode)); }
    public void SetMaxRenderedThumbnails(int value) { MaxRenderedThumbnails = value; RaisePropertyChanged(nameof(MaxRenderedThumbnails)); }
    public void SetReadMoreMaxCharacters(int value) { ReadMoreMaxCharacters = value; RaisePropertyChanged(nameof(ReadMoreMaxCharacters)); }
    public void SetReadMoreMaxLines(int value) { ReadMoreMaxLines = value; RaisePropertyChanged(nameof(ReadMoreMaxLines)); }
    public void SetThreadCollapseThreshold(int value) { ThreadCollapseThreshold = value; RaisePropertyChanged(nameof(ThreadCollapseThreshold)); }
    public void SetTailVisibleCount(int value) { TailVisibleCount = value; RaisePropertyChanged(nameof(TailVisibleCount)); }
    public void SetConnectorLineLength(double value) { ConnectorLineLength = value; RaisePropertyChanged(nameof(ConnectorLineLength)); }
    public void SetDashLength(double value) { DashLength = value; RaisePropertyChanged(nameof(DashLength)); }
    public void SetTimelinePageSize(int value) { TimelinePageSize = value; RaisePropertyChanged(nameof(TimelinePageSize)); }
    public void SetBodyFontSize(double value) { BodyFontSize = value; RaisePropertyChanged(nameof(BodyFontSize)); }
    public void SetBodyTextColor(IndicatorColor value) { BodyTextColor = value; RaisePropertyChanged(nameof(BodyTextColor)); }
    public void SetBodyBackgroundColor(IndicatorColor value) { BodyBackgroundColor = value; RaisePropertyChanged(nameof(BodyBackgroundColor)); }
    public void SetUrlColor(IndicatorColor value) { UrlColor = value; RaisePropertyChanged(nameof(UrlColor)); }
    public void SetHashtagColor(IndicatorColor value) { HashtagColor = value; RaisePropertyChanged(nameof(HashtagColor)); }
    public Task SaveAsync() => Task.CompletedTask;
    public Task LoadAsync() => Task.CompletedTask;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaisePropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
