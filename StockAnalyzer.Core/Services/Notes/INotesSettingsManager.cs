using System;
using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Services.Notes
{
    public interface INotesSettingsManager : INotifyPropertyChanged
    {
        /// <summary>Side length, in pixels, of a rendered attachment thumbnail (Settings &gt; Notes,
        /// "Images" section) - applies to the timeline card's bottom-row thumbnails, an inline image
        /// (when <see cref="ImageDisplayMode"/> is Inline) and the New Note compose panel's preview.</summary>
        double ThumbnailSizePixels { get; }

        /// <summary>Where an attached image renders on a posted Note (Settings &gt; Notes, "Images"
        /// section): AttachmentList (default, today's behavior) or Inline (at its original
        /// text-cursor position).</summary>
        NoteImageDisplayMode ImageDisplayMode { get; }

        /// <summary>Max number of attachment thumbnails rendered on a Note card (Settings &gt; Notes,
        /// "Images" section) before the rest collapse into a "+N" count.</summary>
        int MaxRenderedThumbnails { get; }

        /// <summary>Character-count threshold beyond which a Note card's body is truncated behind a "Read more" toggle (Settings &gt; Notes).</summary>
        int ReadMoreMaxCharacters { get; }

        /// <summary>Newline-count threshold beyond which a Note card's body is truncated behind a "Read more" toggle (Settings &gt; Notes).</summary>
        int ReadMoreMaxLines { get; }

        /// <summary>Total reply-chain length beyond which the timeline collapses the middle of the chain behind a dotted-line indicator (Settings &gt; Notes). A chain at or under this length is always shown in full.</summary>
        int ThreadCollapseThreshold { get; }

        /// <summary>Number of cards kept visible at the tail of a collapsed reply chain (Settings &gt; Notes); the same number are also kept visible at the head.</summary>
        int TailVisibleCount { get; }

        /// <summary>Height, in pixels, of the dotted vertical line drawn in place of a collapsed reply-chain segment (Settings &gt; Notes).</summary>
        double ConnectorLineLength { get; }

        /// <summary>Length, in pixels, of each dash in the collapsed reply-chain's dotted vertical line (Settings &gt; Notes).</summary>
        double DashLength { get; }

        /// <summary>Number of visible timeline cards (per <see cref="NoteThreadOrganizer.BuildThreadedDisplayOrder"/>'s
        /// result, i.e. excluding notes hidden by thread-collapse) loaded at once into the main
        /// timeline - both on initial display/filter change and for each subsequent infinite-scroll
        /// batch (Settings &gt; Notes).</summary>
        int TimelinePageSize { get; }

        /// <summary>Maximum number of upcoming catalysts displayed in the sidebar (Settings &gt; Notes, "Upcoming Catalysts" section).</summary>
        int UpcomingCatalystsLimit { get; }

        /// <summary>Threshold in days relative to today (default: -1, meaning up to 1 day past due) for keeping scheduled notes in the Upcoming Catalysts sidebar (Settings &gt; Notes, "Upcoming Catalysts" section).</summary>
        int UpcomingCatalystsPastDaysRetention { get; }

        /// <summary>Font size applied to every piece of text a Note renders - card body, header/ticker,
        /// timestamps, quoted-note preview, attachment counts (Settings &gt; Notes, both the main
        /// timeline/detail page and the Trash/orphaned-files views). Independent of the app's general
        /// font settings so changing Settings &gt; Fonts never affects Notes.</summary>
        double BodyFontSize { get; }

        /// <summary>Foreground color applied to every piece of text a Note renders (Settings &gt; Notes,
        /// both the main timeline/detail page and the Trash/orphaned-files views). Every Appearance
        /// color below follows one contract: until the user picks a color for the Light/Dark mode in effect it is that theme's
        /// default (and follows a mode switch); a pick belongs only to the mode it was made in, so a color chosen for one mode
        /// never leaks into the other (see <see cref="GetColorPicks"/>).</summary>
        IndicatorColor BodyTextColor { get; }

        /// <summary>Background color applied to every Note card (Settings &gt; Notes, both the main
        /// timeline/detail page and the Trash/orphaned-files views). Same per-mode contract as
        /// <see cref="BodyTextColor"/>; the default is the Theme "App Background".</summary>
        IndicatorColor BodyBackgroundColor { get; }

        /// <summary>Foreground color for clickable URL segments inside a Note body (Settings &gt; Notes,
        /// part of the "Appearance" section). Always an explicit value (no on/off toggle) - defaults
        /// to the same value as <see cref="BodyTextColor"/> but does not track later changes to it.
        /// Same per-mode contract as <see cref="BodyTextColor"/>.</summary>
        IndicatorColor UrlColor { get; }

        /// <summary>Foreground color for clickable hashtag segments inside a Note body (Settings &gt;
        /// Notes, part of the "Appearance" section); same always-explicit contract as
        /// <see cref="UrlColor"/>.</summary>
        IndicatorColor HashtagColor { get; }

        /// <summary>Background color of the text a user highlights (mouse selection) inside a Note body
        /// (Settings &gt; Notes, part of the "Appearance" section). Same per-mode
        /// contract as <see cref="BodyTextColor"/>: the default is a light blue in
        /// the Light theme and a dark blue in the Dark theme. Default interface member so an existing
        /// implementation (and its tests) keeps compiling unchanged; the production manager overrides it.</summary>
        IndicatorColor HighlightColor => IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHighlightColorArgb);

        /// <summary>The built-in <see cref="HighlightColor"/> for the CURRENT theme (what "Reset to default" restores).</summary>
        IndicatorColor DefaultHighlightColor => IndicatorColor.FromUInt(NotesSettingsConstants.DefaultHighlightColorArgb);

        void SetHighlightColor(IndicatorColor value) { }

        /// <summary>The built-in <see cref="BodyBackgroundColor"/>: the "App Background" colour of Settings &gt; Theme for the
        /// theme currently in effect (including a value the user changed there). While the Notes background has not been
        /// customized it follows this value; "Reset to default" restores it. Default interface member (see
        /// <see cref="HighlightColor"/>); the production manager overrides it.</summary>
        IndicatorColor DefaultBodyBackgroundColor => NotesSettingsConstants.FallbackBodyBackgroundColor;

        /// <summary>The colors the user picked for <paramref name="key"/>, per Light/Dark mode (null = the default of that theme). A
        /// settings page snapshots and restores this instead of the color property, which only shows the mode in effect. Default
        /// interface member: an implementation without a per-mode notion keeps one color for both modes.</summary>
        ModeColorPicks GetColorPicks(NotesColorKey key)
        {
            var current = CurrentColorOf(key);
            return new ModeColorPicks(current, current);
        }

        /// <summary>Restores picks taken with <see cref="GetColorPicks"/>. The default member applies one color to the mode in effect.</summary>
        void SetColorPicks(NotesColorKey key, ModeColorPicks picks)
        {
            var color = picks.Dark ?? picks.Light ?? DefaultColorOf(key);
            switch (key)
            {
                case NotesColorKey.BodyText: SetBodyTextColor(color); break;
                case NotesColorKey.BodyBackground: SetBodyBackgroundColor(color); break;
                case NotesColorKey.Url: SetUrlColor(color); break;
                case NotesColorKey.Hashtag: SetHashtagColor(color); break;
                case NotesColorKey.Highlight: SetHighlightColor(color); break;
                default: throw new ArgumentOutOfRangeException(nameof(key), key, $"Unhandled {nameof(NotesColorKey)} value.");
            }
        }

        private IndicatorColor CurrentColorOf(NotesColorKey key) => key switch
        {
            NotesColorKey.BodyText => BodyTextColor,
            NotesColorKey.BodyBackground => BodyBackgroundColor,
            NotesColorKey.Url => UrlColor,
            NotesColorKey.Hashtag => HashtagColor,
            NotesColorKey.Highlight => HighlightColor,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, $"Unhandled {nameof(NotesColorKey)} value."),
        };

        private IndicatorColor DefaultColorOf(NotesColorKey key) => key switch
        {
            NotesColorKey.BodyText or NotesColorKey.Url or NotesColorKey.Hashtag => DefaultBodyTextColor,
            NotesColorKey.BodyBackground => DefaultBodyBackgroundColor,
            NotesColorKey.Highlight => DefaultHighlightColor,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, $"Unhandled {nameof(NotesColorKey)} value."),
        };

        /// <summary>The built-in <see cref="BodyTextColor"/> for the CURRENT theme (what "Reset to default" restores, also for
        /// <see cref="UrlColor"/> and <see cref="HashtagColor"/>, whose default is the body text color). Default interface
        /// member (see <see cref="HighlightColor"/>); the production manager overrides it.</summary>
        IndicatorColor DefaultBodyTextColor => NotesSettingsConstants.FallbackBodyTextColor;

        void SetThumbnailSizePixels(double value);
        void SetImageDisplayMode(NoteImageDisplayMode value);
        void SetMaxRenderedThumbnails(int value);
        void SetReadMoreMaxCharacters(int value);
        void SetReadMoreMaxLines(int value);
        void SetThreadCollapseThreshold(int value);
        void SetTailVisibleCount(int value);
        void SetConnectorLineLength(double value);
        void SetDashLength(double value);
        void SetTimelinePageSize(int value);
        void SetUpcomingCatalystsLimit(int value);
        void SetUpcomingCatalystsPastDaysRetention(int value);
        void SetBodyFontSize(double value);
        void SetBodyTextColor(IndicatorColor value);
        void SetBodyBackgroundColor(IndicatorColor value);
        void SetUrlColor(IndicatorColor value);
        void SetHashtagColor(IndicatorColor value);

        Task SaveAsync();
        Task LoadAsync();
    }
}
