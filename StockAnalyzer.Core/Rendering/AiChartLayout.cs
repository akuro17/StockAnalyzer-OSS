namespace StockAnalyzer.Core.Rendering;

public enum AiChartPanelKind
{
    Price,
    Volume,
    Oscillator,
}

/// <summary>One panel: its rows in the image, top inclusive and bottom exclusive (pixel-edge coordinates, origin at the top left).</summary>
public readonly record struct AiChartPanelRect(AiChartPanelKind Kind, int TopPx, int BottomPx)
{
    public int HeightPx => BottomPx - TopPx;
}

/// <summary>What fits and what does not for a requested image, with the numbers that a refusal tells the caller (rules R-1 and R-4 of plan 15).</summary>
/// <param name="IsValid">True when the width, height and bar count are all acceptable.</param>
/// <param name="WidthTooNarrow">The plot area is narrower than <c>barCount x MinBarSpacingPx</c>, or the image is narrower than the style's minimum.</param>
/// <param name="HeightTooShort">Some panel would be lower than its minimum height, or the image is lower than the style's minimum.</param>
/// <param name="MaxBarsForWidth">The most bars this width can draw: <c>floor(plotWidth / MinBarSpacingPx)</c>.</param>
/// <param name="MinWidthPx">The narrowest width that draws this bar count.</param>
/// <param name="MinHeightPx">The lowest height that holds this panel layout.</param>
public readonly record struct AiChartSizeCheck(bool IsValid, bool WidthTooNarrow, bool HeightTooShort, int MaxBarsForWidth, int MinWidthPx, int MinHeightPx);

/// <summary>
/// The pixel layout of an image: the plot area and the panels from top to bottom (price, volume when shown, then the oscillators). Everything comes from
/// <see cref="AiChartImageStyle"/> constants and the requested size; no text is measured, so the plot area is the same on every machine and for every price.
/// </summary>
public sealed class AiChartLayout
{
    private AiChartLayout(int widthPx, int heightPx, IReadOnlyList<AiChartPanelRect> panels)
    {
        WidthPx = widthPx;
        HeightPx = heightPx;
        Panels = panels;
    }

    public int WidthPx { get; }

    public int HeightPx { get; }

    public int PlotLeftPx => AiChartImageStyle.MarginLeftPx;

    public int PlotRightPx => WidthPx - AiChartImageStyle.MarginRightPx;

    public int PlotWidthPx => PlotRightPx - PlotLeftPx;

    public IReadOnlyList<AiChartPanelRect> Panels { get; }

    public AiChartPanelRect PricePanel => Panels[0];

    /// <summary>Checks the size for a bar count and a panel set. Never throws; the caller turns a refusal into its own error.</summary>
    public static AiChartSizeCheck CheckSize(int widthPx, int heightPx, int barCount, bool includeVolume, int oscillatorCount)
    {
        int horizontalMargins = AiChartImageStyle.MarginLeftPx + AiChartImageStyle.MarginRightPx;
        int plotWidth = widthPx - horizontalMargins;
        int maxBars = plotWidth <= 0 ? 0 : (int)Math.Floor(plotWidth / AiChartImageStyle.MinBarSpacingPx);
        int minWidth = Math.Max(AiChartImageStyle.MinImageWidthPx, (int)Math.Ceiling(barCount * AiChartImageStyle.MinBarSpacingPx) + horizontalMargins);
        bool widthTooNarrow = widthPx < AiChartImageStyle.MinImageWidthPx || plotWidth < barCount * AiChartImageStyle.MinBarSpacingPx;

        List<(AiChartPanelKind Kind, double Weight, int MinHeight)> specs = PanelSpecs(includeVolume, oscillatorCount);
        int minHeight = RequiredHeight(specs);
        bool heightTooShort = heightPx < AiChartImageStyle.MinImageHeightPx || Split(heightPx, specs) is null;

        return new AiChartSizeCheck(!widthTooNarrow && !heightTooShort, widthTooNarrow, heightTooShort, maxBars, minWidth, minHeight);
    }

    /// <summary>Builds the layout. The size must have passed <see cref="CheckSize"/>.</summary>
    public static AiChartLayout Create(int widthPx, int heightPx, bool includeVolume, int oscillatorCount)
    {
        List<(AiChartPanelKind Kind, double Weight, int MinHeight)> specs = PanelSpecs(includeVolume, oscillatorCount);
        AiChartPanelRect[]? panels = Split(heightPx, specs);
        if (panels is null || widthPx < AiChartImageStyle.MinImageWidthPx)
        {
            throw new ArgumentException($"The image size {widthPx}x{heightPx} does not hold this panel layout; check AiChartLayout.CheckSize first.");
        }

        return new AiChartLayout(widthPx, heightPx, panels);
    }

    private static List<(AiChartPanelKind Kind, double Weight, int MinHeight)> PanelSpecs(bool includeVolume, int oscillatorCount)
    {
        if (oscillatorCount < 0 || oscillatorCount > AiChartImageStyle.MaxOscillatorPanels)
        {
            throw new ArgumentOutOfRangeException(nameof(oscillatorCount), oscillatorCount, $"At most {AiChartImageStyle.MaxOscillatorPanels} oscillator panels.");
        }

        var specs = new List<(AiChartPanelKind, double, int)>
        {
            (AiChartPanelKind.Price, AiChartImageStyle.PricePanelWeight, AiChartImageStyle.MinPricePanelHeightPx),
        };
        if (includeVolume)
        {
            specs.Add((AiChartPanelKind.Volume, AiChartImageStyle.VolumePanelWeight, AiChartImageStyle.MinSubPanelHeightPx));
        }

        for (int i = 0; i < oscillatorCount; i++)
        {
            specs.Add((AiChartPanelKind.Oscillator, AiChartImageStyle.OscillatorPanelWeight, AiChartImageStyle.MinSubPanelHeightPx));
        }

        return specs;
    }

    /// <summary>
    /// The lowest image height that holds the panels: the first height from the style's minimum image height upward at which the integer split gives every panel its
    /// minimum. (By weights alone the inner height must reach <c>max(panelMin x weightSum / panelWeight)</c>; whole-row rounding can shift that by a row, so the split itself decides.)
    /// </summary>
    private static int RequiredHeight(List<(AiChartPanelKind Kind, double Weight, int MinHeight)> specs)
    {
        int height = AiChartImageStyle.MinImageHeightPx;
        while (Split(height, specs) is null)
        {
            height++;
        }

        return height;
    }

    /// <summary>Splits the inner height by the weights into whole rows (boundaries rounded from the running weight, so the rows add up exactly); null when a panel is below its minimum.</summary>
    private static AiChartPanelRect[]? Split(int heightPx, List<(AiChartPanelKind Kind, double Weight, int MinHeight)> specs)
    {
        int inner = heightPx - AiChartImageStyle.MarginTopPx - AiChartImageStyle.MarginBottomPx - AiChartImageStyle.PanelGapPx * (specs.Count - 1);
        if (inner <= 0)
        {
            return null;
        }

        double weightSum = specs.Sum(s => s.Weight);
        var panels = new AiChartPanelRect[specs.Count];
        double running = 0;
        for (int i = 0; i < specs.Count; i++)
        {
            int start = (int)Math.Round(inner * running / weightSum, MidpointRounding.AwayFromZero);
            running += specs[i].Weight;
            int end = (int)Math.Round(inner * running / weightSum, MidpointRounding.AwayFromZero);
            int top = AiChartImageStyle.MarginTopPx + start + i * AiChartImageStyle.PanelGapPx;
            panels[i] = new AiChartPanelRect(specs[i].Kind, top, top + (end - start));
            if (end - start < specs[i].MinHeight)
            {
                return null;
            }
        }

        return panels;
    }
}
