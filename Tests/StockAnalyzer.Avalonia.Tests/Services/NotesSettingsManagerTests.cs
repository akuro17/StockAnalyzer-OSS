using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>sa_implement (Notes専用Settings & スレッド折りたたみ Task 4, Y:\Temp\sa_implementation_plan.md):
/// covers NotesSettingsManager's in-memory defaults and setter validation guards - the manager's
/// SaveAsync/LoadAsync themselves are deliberately not exercised here since NotesSettingsFilePath
/// resolves via PathDiscovery.ResolveConfigPath to the real user_notes_settings.json under the
/// user's Data/Config directory (a static readonly field, not injectable in tests), and no existing
/// settings-manager in this codebase (e.g. FontSettingsManager) has a dedicated file-round-trip
/// test either. The in-memory round trip covered here (Set -&gt; Get, including rejection of
/// invalid values) is the property-level contract JSON serialization simply mirrors.</summary>
public class NotesSettingsManagerTests
{
    [Fact]
    public void DefaultValues_MatchSpecifiedDefaults()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        Assert.Equal(48.0, manager.ThumbnailSizePixels);
        Assert.Equal(NoteImageDisplayMode.AttachmentList, manager.ImageDisplayMode);
        Assert.Equal(3, manager.MaxRenderedThumbnails);
        Assert.Equal(150, manager.ReadMoreMaxCharacters);
        Assert.Equal(5, manager.ReadMoreMaxLines);
        Assert.Equal(3, manager.ThreadCollapseThreshold);
        Assert.Equal(2, manager.TailVisibleCount);
        Assert.Equal(60.0, manager.ConnectorLineLength);
        Assert.Equal(8.0, manager.DashLength);
        Assert.Equal(10, manager.TimelinePageSize);
        Assert.Equal(16.0, manager.BodyFontSize);
        Assert.Equal(IndicatorColor.FromUInt(0xFFE0E0E0), manager.BodyTextColor);
        Assert.Equal(IndicatorColor.FromUInt(0xFF181A20), manager.BodyBackgroundColor);
        Assert.Equal(IndicatorColor.FromUInt(0xFFE0E0E0), manager.UrlColor);
        Assert.Equal(IndicatorColor.FromUInt(0xFFE0E0E0), manager.HashtagColor);
    }

    /// <summary>Task C (SAで実装, Note Tab Enhancements).</summary>
    [Fact]
    public void SetThumbnailSizePixels_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetThumbnailSizePixels(96.0);

        Assert.Equal(96.0, manager.ThumbnailSizePixels);
    }

    [Theory]
    [InlineData(23.999)]
    [InlineData(200.001)]
    public void SetThumbnailSizePixels_OutOfRange_IsIgnored(double value)
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetThumbnailSizePixels(value);

        Assert.Equal(48.0, manager.ThumbnailSizePixels);
    }

    [Fact]
    public void SetImageDisplayMode_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetImageDisplayMode(NoteImageDisplayMode.Inline);

        Assert.Equal(NoteImageDisplayMode.Inline, manager.ImageDisplayMode);
    }

    /// <summary>sa_minimal_fix (SAで制約確認 remediation, Finding 2): unlike
    /// SetThumbnailSizePixels/SetMaxRenderedThumbnails above, SetImageDisplayMode had no
    /// Enum.IsDefined guard - an undefined value (e.g. a corrupted or hand-edited persisted settings
    /// file, an external input boundary per CODE_REVIEW_GUIDELINES.md's Enum Input Validation rule)
    /// would be accepted as-is, leaving ImageDisplayMode in an undefined enum state.</summary>
    [Fact]
    public void SetImageDisplayMode_UndefinedValue_IsIgnored()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetImageDisplayMode((NoteImageDisplayMode)99);

        Assert.Equal(NoteImageDisplayMode.AttachmentList, manager.ImageDisplayMode);
    }

    /// <summary>sa_implement (Note Thumbnail Limit).</summary>
    [Fact]
    public void SetMaxRenderedThumbnails_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetMaxRenderedThumbnails(7);

        Assert.Equal(7, manager.MaxRenderedThumbnails);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void SetMaxRenderedThumbnails_OutOfRange_IsIgnored(int value)
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetMaxRenderedThumbnails(value);

        Assert.Equal(3, manager.MaxRenderedThumbnails);
    }

    [Fact]
    public void SetThreadCollapseThreshold_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetThreadCollapseThreshold(5);

        Assert.Equal(5, manager.ThreadCollapseThreshold);
    }

    [Fact]
    public void SetThreadCollapseThreshold_ZeroOrNegative_IsIgnored()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetThreadCollapseThreshold(0);
        manager.SetThreadCollapseThreshold(-1);

        Assert.Equal(3, manager.ThreadCollapseThreshold);
    }

    [Fact]
    public void SetTailVisibleCount_Zero_IsAccepted()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetTailVisibleCount(0);

        Assert.Equal(0, manager.TailVisibleCount);
    }

    [Fact]
    public void SetTailVisibleCount_Negative_IsIgnored()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetTailVisibleCount(-1);

        Assert.Equal(2, manager.TailVisibleCount);
    }

    [Fact]
    public void SetConnectorLineLengthAndDashLength_ValidValues_ArePersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetConnectorLineLength(80.0);
        manager.SetDashLength(6.0);

        Assert.Equal(80.0, manager.ConnectorLineLength);
        Assert.Equal(6.0, manager.DashLength);
    }

    [Fact]
    public void SetTimelinePageSize_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetTimelinePageSize(25);

        Assert.Equal(25, manager.TimelinePageSize);
    }

    [Fact]
    public void SetTimelinePageSize_ZeroOrNegative_IsIgnored()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetTimelinePageSize(0);
        manager.SetTimelinePageSize(-1);

        Assert.Equal(10, manager.TimelinePageSize);
    }

    [Fact]
    public void SetBodyFontSize_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetBodyFontSize(20.0);

        Assert.Equal(20.0, manager.BodyFontSize);
    }

    [Theory]
    [InlineData(11.999)]
    [InlineData(24.001)]
    public void SetBodyFontSize_OutOfRange_IsIgnored(double value)
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        manager.SetBodyFontSize(value);

        Assert.Equal(16.0, manager.BodyFontSize);
    }

    [Fact]
    public void SetBodyTextColor_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        var color = IndicatorColor.FromRgb(0x11, 0x22, 0x33);

        manager.SetBodyTextColor(color);

        Assert.Equal(color, manager.BodyTextColor);
    }

    [Fact]
    public void SetBodyBackgroundColor_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        var color = IndicatorColor.FromRgb(0x44, 0x55, 0x66);

        manager.SetBodyBackgroundColor(color);

        Assert.Equal(color, manager.BodyBackgroundColor);
    }

    [Fact]
    public void SetUrlColorAndHashtagColor_ValidValues_ArePersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        var url = IndicatorColor.FromRgb(0x11, 0x22, 0x33);
        var hashtag = IndicatorColor.FromRgb(0x44, 0x55, 0x66);

        manager.SetUrlColor(url);
        manager.SetHashtagColor(hashtag);

        Assert.Equal(url, manager.UrlColor);
        Assert.Equal(hashtag, manager.HashtagColor);
    }
}
