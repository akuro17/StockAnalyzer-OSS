using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>sa_implement (Notes専用Settings & スレッド折りたたみ Task 4, docs/sa_implementation_plan):
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
        Assert.Equal(7, manager.UpcomingCatalystsLimit);
        Assert.Equal(-1, manager.UpcomingCatalystsPastDaysRetention);
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

    [Fact]
    public void SetUpcomingCatalystsLimit_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        manager.SetUpcomingCatalystsLimit(14);
        Assert.Equal(14, manager.UpcomingCatalystsLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void SetUpcomingCatalystsLimit_OutOfRange_IsIgnored(int value)
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        manager.SetUpcomingCatalystsLimit(value);
        Assert.Equal(7, manager.UpcomingCatalystsLimit);
    }

    [Fact]
    public void SetUpcomingCatalystsPastDaysRetention_ValidValue_IsPersistedInMemory()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        manager.SetUpcomingCatalystsPastDaysRetention(-7);
        Assert.Equal(-7, manager.UpcomingCatalystsPastDaysRetention);
    }

    [Theory]
    [InlineData(-366)]
    [InlineData(366)]
    public void SetUpcomingCatalystsPastDaysRetention_OutOfRange_IsIgnored(int value)
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        manager.SetUpcomingCatalystsPastDaysRetention(value);
        Assert.Equal(-1, manager.UpcomingCatalystsPastDaysRetention);
    }

    // ---- HighlightColor (selection background of a Note body; default follows the Light/Dark theme) ----

    [Fact]
    public void HighlightColor_DefaultsToTheDarkBlue_WhenTheThemeIsDark()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());

        Assert.Equal(IndicatorColor.FromUInt(0xFF264F78), manager.HighlightColor);
        Assert.Equal(IndicatorColor.FromUInt(0xFF264F78), manager.DefaultHighlightColor);
    }

    [Fact]
    public void HighlightColor_FollowsTheTheme_WhileItIsStillTheDefault()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);

        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(IndicatorColor.FromUInt(0xFFB3D4FF), manager.HighlightColor);
        Assert.Equal(IndicatorColor.FromUInt(0xFFB3D4FF), manager.DefaultHighlightColor);

        themeManager.CurrentTheme = ThemeColors.Dark;
        Assert.Equal(IndicatorColor.FromUInt(0xFF264F78), manager.HighlightColor);
    }

    [Fact]
    public void HighlightColor_CustomizedValue_StaysAcrossThemeChanges()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var custom = IndicatorColor.FromRgb(0x12, 0x34, 0x56);

        manager.SetHighlightColor(custom);
        themeManager.CurrentTheme = ThemeColors.Light;
        themeManager.CurrentTheme = ThemeColors.Dark;

        Assert.Equal(custom, manager.HighlightColor);
    }

    [Fact]
    public void SetHighlightColor_RaisesPropertyChangedOnlyForARealChange()
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        var raised = new System.Collections.Generic.List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        manager.SetHighlightColor(manager.HighlightColor);
        Assert.Empty(raised);

        manager.SetHighlightColor(IndicatorColor.FromRgb(1, 2, 3));
        Assert.Equal(new[] { nameof(manager.HighlightColor) }, raised);
    }

    /// <summary>The two built-in highlights are blues, are readable with the matching default body text colour (WCAG contrast
    /// ratio of at least 4.5, the AA threshold for body text) and stand apart from the matching default card background.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DefaultHighlightColor_IsABlueThatKeepsTheDefaultBodyTextReadable(bool isDark)
    {
        const double MinimumTextContrast = 4.5;
        var highlight = IndicatorColor.FromUInt(NotesSettingsConstants.GetDefaultHighlightColorArgb(isDark));
        var text = NotesSettingsConstants.GetDefaultBodyTextColor(isDark);
        var background = isDark ? ThemeColors.Dark.ShellBackground : ThemeColors.Light.ShellBackground;

        Assert.True(highlight.B > highlight.R && highlight.B > highlight.G, "the highlight must be blue-dominant");
        Assert.True(Contrast(highlight, text) >= MinimumTextContrast, $"text/highlight contrast {Contrast(highlight, text):F2}");
        Assert.NotEqual(highlight, background);
        Assert.NotEqual(NotesSettingsConstants.GetDefaultHighlightColorArgb(true), NotesSettingsConstants.GetDefaultHighlightColorArgb(false));
    }

    private static double Contrast(IndicatorColor a, IndicatorColor b)
    {
        var (lighter, darker) = (Luminance(a), Luminance(b));
        if (lighter < darker) (lighter, darker) = (darker, lighter);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(IndicatorColor color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : System.Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    // ---- Notes background follows Settings > Theme > "App Background" while it is not customized ----

    [Fact]
    public void BodyBackgroundColor_InTheLightTheme_IsTheThemeAppBackground()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);

        themeManager.CurrentTheme = ThemeColors.Light;

        Assert.Equal(ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);
        Assert.Equal(ThemeColors.Light.ShellBackground, manager.DefaultBodyBackgroundColor);
        Assert.NotEqual(ThemeColors.Dark.ShellBackground, manager.BodyBackgroundColor);
    }

    [Fact]
    public void BodyBackgroundColor_SwitchingBackAndForth_AlwaysEqualsTheCurrentThemeBackground()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);

        themeManager.CurrentTheme = ThemeColors.Light;
        themeManager.CurrentTheme = ThemeColors.Dark;
        Assert.Equal(ThemeColors.Dark.ShellBackground, manager.BodyBackgroundColor);

        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);
    }

    [Fact]
    public void BodyBackgroundColor_FollowsAThemeBackgroundTheUserEditedInSettingsTheme()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var edited = IndicatorColor.FromRgb(0x20, 0x30, 0x40);

        themeManager.CurrentTheme = ThemeColors.Dark with { ShellBackground = edited };
        Assert.Equal(edited, manager.BodyBackgroundColor);

        // The edited value is not a built-in preset: the next change must still be followed (it was applied by the manager itself).
        var editedAgain = IndicatorColor.FromRgb(0x50, 0x60, 0x70);
        themeManager.CurrentTheme = ThemeColors.Dark with { ShellBackground = editedAgain };
        Assert.Equal(editedAgain, manager.BodyBackgroundColor);

        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);
    }

    [Fact]
    public void BodyBackgroundColor_PickedValue_StaysInTheModeItWasPickedIn_AndNeverLeaksIntoTheOtherMode()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var custom = IndicatorColor.FromRgb(0x12, 0x34, 0x56);

        manager.SetBodyBackgroundColor(custom);
        Assert.Equal(custom, manager.BodyBackgroundColor);

        // The Light mode keeps following its own Theme "App Background", whatever was picked for the Dark mode.
        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);

        themeManager.CurrentTheme = ThemeColors.Dark with { ShellBackground = IndicatorColor.FromRgb(0x01, 0x01, 0x01) };
        Assert.Equal(custom, manager.BodyBackgroundColor);
    }

    [Fact]
    public void BodyBackgroundColor_PicksAreIndependentPerMode()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var darkPick = IndicatorColor.FromRgb(0x07, 0x10, 0x20);
        var lightPick = IndicatorColor.FromRgb(0xFA, 0xF0, 0xE6);

        manager.SetBodyBackgroundColor(darkPick);
        themeManager.CurrentTheme = ThemeColors.Light;
        manager.SetBodyBackgroundColor(lightPick);
        Assert.Equal(lightPick, manager.BodyBackgroundColor);

        themeManager.CurrentTheme = ThemeColors.Dark;
        Assert.Equal(darkPick, manager.BodyBackgroundColor);
        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(lightPick, manager.BodyBackgroundColor);
    }

    [Fact]
    public void ColorPicks_RoundTripBothModes_AndRaiseOnlyWhenTheShownColorMoves()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var darkPick = IndicatorColor.FromRgb(0x07, 0x10, 0x20);
        var lightPick = IndicatorColor.FromRgb(0xFA, 0xF0, 0xE6);
        var raised = new System.Collections.Generic.List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        manager.SetColorPicks(NotesColorKey.BodyBackground, new ModeColorPicks(darkPick, lightPick));
        Assert.Equal(new ModeColorPicks(darkPick, lightPick), manager.GetColorPicks(NotesColorKey.BodyBackground));
        Assert.Equal(darkPick, manager.BodyBackgroundColor);
        Assert.Equal(1, raised.FindAll(name => name == nameof(manager.BodyBackgroundColor)).Count);

        // Changing only the pick of the mode that is not shown moves nothing visible.
        manager.SetColorPicks(NotesColorKey.BodyBackground, new ModeColorPicks(darkPick, null));
        Assert.Equal(1, raised.FindAll(name => name == nameof(manager.BodyBackgroundColor)).Count);
        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(ThemeColors.Light.ShellBackground, manager.BodyBackgroundColor);
    }

    // ---- Every Appearance color keeps one pick per Light/Dark mode (owner decision D-3) ----

    private static void Pick(NotesSettingsManager manager, NotesColorKey key, IndicatorColor value)
    {
        switch (key)
        {
            case NotesColorKey.BodyText: manager.SetBodyTextColor(value); break;
            case NotesColorKey.BodyBackground: manager.SetBodyBackgroundColor(value); break;
            case NotesColorKey.Url: manager.SetUrlColor(value); break;
            case NotesColorKey.Hashtag: manager.SetHashtagColor(value); break;
            case NotesColorKey.Highlight: manager.SetHighlightColor(value); break;
            default: throw new System.ArgumentOutOfRangeException(nameof(key));
        }
    }

    private static IndicatorColor ShownColor(NotesSettingsManager manager, NotesColorKey key) => key switch
    {
        NotesColorKey.BodyText => manager.BodyTextColor,
        NotesColorKey.BodyBackground => manager.BodyBackgroundColor,
        NotesColorKey.Url => manager.UrlColor,
        NotesColorKey.Hashtag => manager.HashtagColor,
        NotesColorKey.Highlight => manager.HighlightColor,
        _ => throw new System.ArgumentOutOfRangeException(nameof(key)),
    };

    private static IndicatorColor DefaultOf(NotesSettingsManager manager, NotesColorKey key) => key switch
    {
        NotesColorKey.BodyBackground => manager.DefaultBodyBackgroundColor,
        NotesColorKey.Highlight => manager.DefaultHighlightColor,
        _ => manager.DefaultBodyTextColor,
    };

    /// <summary>A color picked in the Dark mode (say a dark navy text) must not leave the Light mode with an unreadable color.</summary>
    [Theory]
    [InlineData(NotesColorKey.BodyText)]
    [InlineData(NotesColorKey.BodyBackground)]
    [InlineData(NotesColorKey.Url)]
    [InlineData(NotesColorKey.Hashtag)]
    [InlineData(NotesColorKey.Highlight)]
    public void ColorPick_BelongsToTheModeItWasMadeIn_AndNeverLeaksIntoTheOther(NotesColorKey key)
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var darkPick = IndicatorColor.FromRgb(0x07, 0x10, 0x20);
        var lightPick = IndicatorColor.FromRgb(0xFA, 0xF0, 0xE6);

        Pick(manager, key, darkPick);
        Assert.Equal(darkPick, ShownColor(manager, key));

        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(DefaultOf(manager, key), ShownColor(manager, key));
        Pick(manager, key, lightPick);
        Assert.Equal(lightPick, ShownColor(manager, key));

        themeManager.CurrentTheme = ThemeColors.Dark;
        Assert.Equal(darkPick, ShownColor(manager, key));
        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(lightPick, ShownColor(manager, key));
    }

    [Theory]
    [InlineData(NotesColorKey.BodyText)]
    [InlineData(NotesColorKey.BodyBackground)]
    [InlineData(NotesColorKey.Url)]
    [InlineData(NotesColorKey.Hashtag)]
    [InlineData(NotesColorKey.Highlight)]
    public void ColorPick_OfTheThemesOwnDefault_ClearsThePick(NotesColorKey key)
    {
        var manager = new NotesSettingsManager(new FakeThemeManager());
        Pick(manager, key, IndicatorColor.FromRgb(1, 2, 3));

        Pick(manager, key, DefaultOf(manager, key));

        Assert.Equal(default, manager.GetColorPicks(key));
    }

    /// <summary>Every color announces its own property when only the mode changes.</summary>
    [Fact]
    public void SwitchingTheMode_AnnouncesEveryColorThatFollowsTheTheme()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var raised = new System.Collections.Generic.List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        themeManager.CurrentTheme = ThemeColors.Light;

        Assert.Contains(nameof(manager.BodyTextColor), raised);
        Assert.Contains(nameof(manager.BodyBackgroundColor), raised);
        Assert.Contains(nameof(manager.UrlColor), raised);
        Assert.Contains(nameof(manager.HashtagColor), raised);
        Assert.Contains(nameof(manager.HighlightColor), raised);
    }

    [Fact]
    public void BodyBackgroundColor_PickingTheThemeBackground_GoesBackToFollowingIt()
    {
        var themeManager = new FakeThemeManager { CurrentTheme = ThemeColors.Light };
        var manager = new NotesSettingsManager(themeManager);
        manager.SetBodyBackgroundColor(IndicatorColor.FromRgb(0x12, 0x34, 0x56));

        manager.SetBodyBackgroundColor(manager.DefaultBodyBackgroundColor);
        var edited = IndicatorColor.FromRgb(0xEE, 0xEE, 0xEE);
        themeManager.CurrentTheme = ThemeColors.Light with { ShellBackground = edited };

        Assert.Equal(edited, manager.BodyBackgroundColor);
    }

    [Fact]
    public void BodyBackgroundColor_RaisesPropertyChangedWhenOnlyTheModeChanges()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        var raised = new System.Collections.Generic.List<string?>();
        manager.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        themeManager.CurrentTheme = ThemeColors.Light;

        Assert.Contains(nameof(manager.BodyBackgroundColor), raised);
        Assert.Equal(1, raised.FindAll(name => name == nameof(manager.BodyBackgroundColor)).Count);
    }

    [Fact]
    public void DefaultBodyTextColor_IsTheCurrentThemesDefault()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);
        Assert.Equal(ThemeColors.Dark.ShellText, manager.DefaultBodyTextColor);

        themeManager.CurrentTheme = ThemeColors.Light;
        Assert.Equal(ThemeColors.Light.ShellText, manager.DefaultBodyTextColor);
    }

    [Fact]
    public void DefaultBodyBackgroundColor_IsTheCurrentThemesBackground()
    {
        var themeManager = new FakeThemeManager();
        var manager = new NotesSettingsManager(themeManager);

        Assert.Equal(ThemeColors.Dark.ShellBackground, manager.DefaultBodyBackgroundColor);

        var edited = IndicatorColor.FromRgb(0x0A, 0x0B, 0x0C);
        themeManager.CurrentTheme = ThemeColors.Light with { ShellBackground = edited };
        Assert.Equal(edited, manager.DefaultBodyBackgroundColor);
    }

    /// <summary>The fallback is read from the Theme preset (one definition), so a settings manager without a theme still shows the Dark Theme background.</summary>
    [Fact]
    public void FallbackBodyBackgroundColor_IsTheDarkThemePresetBackground()
    {
        Assert.Equal(ThemeColors.Dark.ShellBackground, NotesSettingsConstants.FallbackBodyBackgroundColor);
    }

}
