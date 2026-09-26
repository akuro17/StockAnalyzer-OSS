using System.ComponentModel;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Notes;

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

        /// <summary>Font size applied to every piece of text a Note renders - card body, header/ticker,
        /// timestamps, quoted-note preview, attachment counts (Settings &gt; Notes, both the main
        /// timeline/detail page and the Trash/orphaned-files views). Independent of the app's general
        /// font settings so changing Settings &gt; Fonts never affects Notes.</summary>
        double BodyFontSize { get; }

        /// <summary>Foreground color applied to every piece of text a Note renders (Settings &gt; Notes,
        /// both the main timeline/detail page and the Trash/orphaned-files views). While still at its
        /// default value (never explicitly customized by the user), this follows the app's Light/Dark
        /// theme automatically; once the user picks an explicit color, it stops tracking theme changes.</summary>
        IndicatorColor BodyTextColor { get; }

        /// <summary>Background color applied to every Note card (Settings &gt; Notes, both the main
        /// timeline/detail page and the Trash/orphaned-files views). Same default-tracks-theme,
        /// customized-value-is-sticky contract as <see cref="BodyTextColor"/>.</summary>
        IndicatorColor BodyBackgroundColor { get; }

        /// <summary>Foreground color for clickable URL segments inside a Note body (Settings &gt; Notes,
        /// part of the "Appearance" section). Always an explicit value (no on/off toggle) - defaults
        /// to the same value as <see cref="BodyTextColor"/> but does not track later changes to it.
        /// Same default-tracks-theme, customized-value-is-sticky contract as <see cref="BodyTextColor"/>.</summary>
        IndicatorColor UrlColor { get; }

        /// <summary>Foreground color for clickable hashtag segments inside a Note body (Settings &gt;
        /// Notes, part of the "Appearance" section); same always-explicit contract as
        /// <see cref="UrlColor"/>.</summary>
        IndicatorColor HashtagColor { get; }

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
        void SetBodyFontSize(double value);
        void SetBodyTextColor(IndicatorColor value);
        void SetBodyBackgroundColor(IndicatorColor value);
        void SetUrlColor(IndicatorColor value);
        void SetHashtagColor(IndicatorColor value);

        Task SaveAsync();
        Task LoadAsync();
    }
}
