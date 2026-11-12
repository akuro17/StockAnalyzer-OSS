using System.Globalization;

namespace StockAnalyzer.Core.Drawing;

/// <summary>Formatting facts shared by every drawing-value calculator.</summary>
public static class DrawingValueFormat
{
    /// <summary>The culture the GUI formats the value texts with (StockAnalyzer.Avalonia/Program.cs sets en-US as the default thread culture).</summary>
    public const string CultureName = "en-US";

    /// <summary>
    /// The culture of every value text (FormattedText, keys and labels with numbers or dates). The calculators pass it to every conversion, so a text never depends on the
    /// culture of the calling thread. It is not <see cref="CultureInfo.InvariantCulture"/>: the invariant percent pattern differs from en-US.
    /// </summary>
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CultureName);
}
