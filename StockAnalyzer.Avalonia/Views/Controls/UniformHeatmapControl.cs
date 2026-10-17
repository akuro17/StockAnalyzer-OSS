using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models.Analysis;
using Point = Avalonia.Point;

namespace StockAnalyzer.Avalonia.Views.Controls;

/// <summary>
/// High-performance Avalonia control for rendering Uniform Grid Heatmaps.
/// Strictly eliminates market-cap variable slicing (treemaps), guarantees equal tile dimensions,
/// zero UI freezes, and employs a perceptually uniform Oklab diverging colormap with universal design support.
/// </summary>
public sealed class UniformHeatmapControl : Control, IDisposable
{
    public static readonly StyledProperty<IReadOnlyList<UniformHeatmapItem>?> ItemsProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, IReadOnlyList<UniformHeatmapItem>?>(nameof(Items));

    public static readonly StyledProperty<double> ScaleThresholdProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, double>(nameof(ScaleThreshold), 0.05); // +/- 5%

    public static readonly StyledProperty<string?> SelectedTickerProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, string?>(nameof(SelectedTicker));

    public static readonly StyledProperty<string?> HoveredTickerProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, string?>(nameof(HoveredTicker));

    public static readonly StyledProperty<HeatmapColorScheme> ColorSchemeProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, HeatmapColorScheme>(nameof(ColorScheme), HeatmapColorScheme.Classic);

    public event EventHandler<UniformHeatmapItem>? TileClicked;

    // Fixed Grid Geometry
    public const float TargetTileWidth = 120.0f;
    public const float TargetTileHeight = 70.0f;
    public const float CellGap = 4.0f;

    // Perceptual Colors
    private static readonly SKColor NeutralColor = new(0x2B, 0x2F, 0x3A);         // Dark Slate Gray
    private static readonly SKColor BullishPeakClassic = new(0x00, 0xC8, 0x53);   // Vivid Emerald
    private static readonly SKColor BearishPeakClassic = new(0xD5, 0x00, 0x00);   // Vivid Crimson
    private static readonly SKColor BullishPeakColorblind = new(0x1E, 0x88, 0xE5);// Sky Blue
    private static readonly SKColor BearishPeakColorblind = new(0xFB, 0x8C, 0x00);// Vivid Amber / Orange
    private static readonly SKColor NoDataColor = new(0x1E, 0x20, 0x24);          // Dark Neutral
    private static readonly SKColor TileBorderColor = new(0x37, 0x3A, 0x40);
    private static readonly SKColor HoverBorderColor = new(0xFA, 0xCC, 0x15);     // Yellow accent
    private static readonly SKColor SelectedBorderColor = new(0x38, 0xBD, 0xF8);  // Sky blue accent

    // Precomputed 512-entry Oklab Diverging LUTs
    private static readonly SKColor[] ClassicLut = GenerateOklabLut(NeutralColor, BullishPeakClassic, BearishPeakClassic, 512);
    private static readonly SKColor[] ColorblindLut = GenerateOklabLut(NeutralColor, BullishPeakColorblind, BearishPeakColorblind, 512);

    // Reusable Paints
    private readonly SKPaint _tileFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _tileBorderPaint = new() { Color = TileBorderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1.0f, IsAntialias = true };
    private readonly SKPaint _activeBorderPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2.0f, IsAntialias = true };
    private readonly SKPaint _hatchPaint = new() { Color = new SKColor(0x2E, 0x32, 0x38), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
    private readonly SKPaint _tickerTextPaint = new() { Color = SKColors.White, Style = SKPaintStyle.Fill, TextSize = 14.0f, IsAntialias = true, FakeBoldText = true };
    private readonly SKPaint _returnTextPaint = new() { Color = SKColors.White, Style = SKPaintStyle.Fill, TextSize = 12.0f, IsAntialias = true };
    private readonly SKPaint _noDataTextPaint = new() { Color = new SKColor(0x9E, 0x9E, 0x9E), Style = SKPaintStyle.Fill, TextSize = 12.0f, IsAntialias = true };
    private readonly SKPaint _shadowTextPaint = new() { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Fill, TextSize = 12.0f, IsAntialias = true };

    static UniformHeatmapControl()
    {
        AffectsRender<UniformHeatmapControl>(ItemsProperty, ScaleThresholdProperty, SelectedTickerProperty, HoveredTickerProperty, ColorSchemeProperty);
        AffectsMeasure<UniformHeatmapControl>(ItemsProperty);
    }

    public UniformHeatmapControl()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<UniformHeatmapItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double ScaleThreshold
    {
        get => GetValue(ScaleThresholdProperty);
        set => SetValue(ScaleThresholdProperty, value);
    }

    public string? SelectedTicker
    {
        get => GetValue(SelectedTickerProperty);
        set => SetValue(SelectedTickerProperty, value);
    }

    public string? HoveredTicker
    {
        get => GetValue(HoveredTickerProperty);
        set => SetValue(HoveredTickerProperty, value);
    }

    public HeatmapColorScheme ColorScheme
    {
        get => GetValue(ColorSchemeProperty);
        set => SetValue(ColorSchemeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items == null || items.Count == 0)
        {
            return new Size(0, 0);
        }

        double width = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? 800.0
            : availableSize.Width;

        int n = items.Count;
        int columns = Math.Max(1, (int)Math.Floor((width + CellGap) / (TargetTileWidth + CellGap)));
        int rows = (int)Math.Ceiling(n / (double)columns);

        double desiredHeight = (rows * TargetTileHeight) + (Math.Max(0, rows - 1) * CellGap);
        return new Size(width, desiredHeight);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            var oldRect = change.GetOldValue<Rect>();
            var newRect = change.GetNewValue<Rect>();
            if (Math.Abs(oldRect.Width - newRect.Width) > 0.5)
            {
                InvalidateMeasure();
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var items = Items;
        if (items == null || items.Count == 0) return;

        double scale = ScaleThreshold;
        string? selected = SelectedTicker;
        string? hovered = HoveredTicker;
        var scheme = ColorScheme;

        context.Custom(new UniformHeatmapDrawOperation(
            this,
            new Rect(Bounds.Size),
            items,
            scale,
            selected,
            hovered,
            scheme));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        int idx = HitTestIndex(pos);

        var items = Items;
        string? newHover = (items != null && idx >= 0 && idx < items.Count) ? items[idx].Ticker : null;
        if (HoveredTicker != newHover)
        {
            HoveredTicker = newHover;
            Cursor = newHover != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        int idx = HitTestIndex(pos);

        var items = Items;
        if (items != null && idx >= 0 && idx < items.Count)
        {
            var item = items[idx];
            SelectedTicker = item.Ticker;
            TileClicked?.Invoke(this, item);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (HoveredTicker != null)
        {
            HoveredTicker = null;
            Cursor = Cursor.Default;
            InvalidateVisual();
        }
    }

    private int HitTestIndex(Point pos)
    {
        var items = Items;
        if (items == null || items.Count == 0 || Bounds.Width <= 0) return -1;

        float width = (float)Bounds.Width;
        int columns = Math.Max(1, (int)Math.Floor((width + CellGap) / (TargetTileWidth + CellGap)));
        float actualTileWidth = (width - ((columns - 1) * CellGap)) / columns;

        float cellW = actualTileWidth + CellGap;
        float cellH = TargetTileHeight + CellGap;

        if (pos.X < 0 || pos.Y < 0) return -1;

        int col = (int)(pos.X / cellW);
        int row = (int)(pos.Y / cellH);

        if (col < 0 || col >= columns || row < 0) return -1;

        // Check if inside tile (not within gap)
        float tileX = col * cellW;
        float tileY = row * cellH;
        if (pos.X > tileX + actualTileWidth || pos.Y > tileY + TargetTileHeight) return -1;

        int index = (row * columns) + col;
        return (index >= 0 && index < items.Count) ? index : -1;
    }

    internal void RenderSkia(
        SKCanvas canvas,
        Rect bounds,
        IReadOnlyList<UniformHeatmapItem>? items,
        double scaleThreshold,
        string? selectedTicker,
        string? hoveredTicker,
        HeatmapColorScheme colorScheme)
    {
        if (items == null || items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        float width = (float)bounds.Width;
        int n = items.Count;
        int columns = Math.Max(1, (int)Math.Floor((width + CellGap) / (TargetTileWidth + CellGap)));
        float actualTileWidth = (width - ((columns - 1) * CellGap)) / columns;

        double scale = double.IsNaN(scaleThreshold) ? 0.05 : Math.Clamp(scaleThreshold, 0.001, 1.0);
        string? hovered = hoveredTicker;
        string? selected = selectedTicker;
        HeatmapColorScheme scheme = colorScheme;

        canvas.Save();
        try
        {
            for (int i = 0; i < n; i++)
            {
                var item = items[i];
                int col = i % columns;
                int row = i / columns;

                float x = col * (actualTileWidth + CellGap);
                float y = row * (TargetTileHeight + CellGap);

                var tileRect = new SKRoundRect(new SKRect(x, y, x + actualTileWidth, y + TargetTileHeight), 4.0f, 4.0f);
                bool isNan = double.IsNaN(item.ReturnPercentage);

                if (isNan)
                {
                    // Missing data: Dark neutral tile with diagonal line hatch pattern
                    canvas.Save();
                    canvas.ClipRoundRect(tileRect, antialias: true);
                    _tileFillPaint.Color = NoDataColor;
                    canvas.DrawRoundRect(tileRect, _tileFillPaint);

                    for (float hx = -TargetTileHeight; hx < actualTileWidth + TargetTileHeight; hx += 16.0f)
                    {
                        canvas.DrawLine(x + hx, y + TargetTileHeight, x + hx + TargetTileHeight, y, _hatchPaint);
                    }
                    canvas.Restore();
                }
                else
                {
                    // Interpolate Perceptually Uniform Oklab Diverging Color
                    SKColor tileColor = GetDivergingColor(item.ReturnPercentage, scale, scheme);
                    _tileFillPaint.Color = tileColor;
                    canvas.DrawRoundRect(tileRect, _tileFillPaint);
                }

                // Border
                bool isHovered = item.Ticker == hovered;
                bool isSelected = item.Ticker == selected;

                if (isSelected)
                {
                    _activeBorderPaint.Color = SelectedBorderColor;
                    canvas.DrawRoundRect(tileRect, _activeBorderPaint);
                }
                else if (isHovered)
                {
                    _activeBorderPaint.Color = HoverBorderColor;
                    canvas.DrawRoundRect(tileRect, _activeBorderPaint);
                }
                else
                {
                    canvas.DrawRoundRect(tileRect, _tileBorderPaint);
                }

                // Ticker Label
                float textX = x + 8.0f;
                float textY = y + 26.0f;
                canvas.DrawText(item.Ticker, textX, textY, _tickerTextPaint);

                // Return Percentage Label
                float retY = y + 48.0f;
                if (isNan)
                {
                    canvas.DrawText(" — ", textX, retY, _noDataTextPaint);
                }
                else
                {
                    string sign = item.ReturnPercentage >= 0 ? "+" : "";
                    string returnStr = $"{sign}{item.ReturnPercentage * 100.0:F2}%";

                    // Subtle text shadow for readability
                    canvas.DrawText(returnStr, textX + 1.0f, retY + 1.0f, _shadowTextPaint);
                    canvas.DrawText(returnStr, textX, retY, _returnTextPaint);
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// Evaluates perceptually uniform diverging color in Oklab color space via precomputed LUT.
    /// </summary>
    public static SKColor GetDivergingColor(double ret, double scaleThreshold, HeatmapColorScheme colorScheme = HeatmapColorScheme.Classic)
    {
        if (double.IsNaN(ret)) return NoDataColor;

        double scale = double.IsNaN(scaleThreshold) ? 0.05 : Math.Clamp(scaleThreshold, 0.001, 1.0);
        double t = Math.Clamp(ret / scale, -1.0, 1.0);
        int idx = (int)Math.Round(((t + 1.0) * 0.5) * 511.0);
        idx = Math.Clamp(idx, 0, 511);

        var lut = colorScheme == HeatmapColorScheme.ColorblindSafe ? ColorblindLut : ClassicLut;
        return lut[idx];
    }

    private static SKColor[] GenerateOklabLut(SKColor neutral, SKColor bullish, SKColor bearish, int size)
    {
        var lut = new SKColor[size];
        var neutralLab = OklabColor.FromSrgb(neutral);
        var bullishLab = OklabColor.FromSrgb(bullish);
        var bearishLab = OklabColor.FromSrgb(bearish);

        int half = size / 2;
        // Negative half: bearish to neutral
        for (int i = 0; i < half; i++)
        {
            double t = (double)i / half;
            var lab = OklabColor.Lerp(bearishLab, neutralLab, t);
            lut[i] = lab.ToSrgb();
        }

        // Positive half: neutral to bullish
        for (int i = half; i < size; i++)
        {
            double t = (double)(i - half) / (size - 1 - half);
            var lab = OklabColor.Lerp(neutralLab, bullishLab, t);
            lut[i] = lab.ToSrgb();
        }

        return lut;
    }

    private readonly struct OklabColor
    {
        public readonly double L;
        public readonly double A;
        public readonly double B;

        public OklabColor(double l, double a, double b)
        {
            L = l;
            A = a;
            B = b;
        }

        public static OklabColor FromSrgb(SKColor color)
        {
            double r = SrgbToLinear(color.Red / 255.0);
            double g = SrgbToLinear(color.Green / 255.0);
            double b = SrgbToLinear(color.Blue / 255.0);

            double lPrime = (0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b);
            double mPrime = (0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b);
            double sPrime = (0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b);

            double l = Math.Cbrt(lPrime);
            double m = Math.Cbrt(mPrime);
            double s = Math.Cbrt(sPrime);

            double lOut = (0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s);
            double aOut = (1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s);
            double bOut = (0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s);

            return new OklabColor(lOut, aOut, bOut);
        }

        public SKColor ToSrgb()
        {
            double l = L + (0.3963377774 * A) + (0.2158037573 * B);
            double m = L - (0.1055613458 * A) - (0.0638541728 * B);
            double s = L - (0.0894841775 * A) - (1.2914855480 * B);

            double lPrime = l * l * l;
            double mPrime = m * m * m;
            double sPrime = s * s * s;

            double rLin = (+4.0767416621 * lPrime) - (3.3077115913 * mPrime) + (0.2309699292 * sPrime);
            double gLin = (-1.2684380046 * lPrime) + (2.6097574011 * mPrime) - (0.3413193965 * sPrime);
            double bLin = (-0.0041960863 * lPrime) - (0.7034186147 * mPrime) + (1.7076147010 * sPrime);

            byte r = (byte)Math.Clamp((int)Math.Round(LinearToSrgb(rLin) * 255.0), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round(LinearToSrgb(gLin) * 255.0), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round(LinearToSrgb(bLin) * 255.0), 0, 255);

            return new SKColor(r, g, b, 255);
        }

        public static OklabColor Lerp(OklabColor c1, OklabColor c2, double t)
        {
            return new OklabColor(
                c1.L + (t * (c2.L - c1.L)),
                c1.A + (t * (c2.A - c1.A)),
                c1.B + (t * (c2.B - c1.B))
            );
        }

        private static double SrgbToLinear(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        private static double LinearToSrgb(double c) =>
            c <= 0.0031308 ? c * 12.92 : (1.055 * Math.Pow(Math.Max(c, 0.0), 1.0 / 2.4)) - 0.055;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HoveredTicker = null;
    }

    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _tileFillPaint.Dispose();
        _tileBorderPaint.Dispose();
        _activeBorderPaint.Dispose();
        _hatchPaint.Dispose();
        _tickerTextPaint.Dispose();
        _returnTextPaint.Dispose();
        _noDataTextPaint.Dispose();
        _shadowTextPaint.Dispose();
    }

    private sealed class UniformHeatmapDrawOperation : SkiaLeaseDrawOperation
    {
        private readonly UniformHeatmapControl _owner;
        private readonly IReadOnlyList<UniformHeatmapItem> _items;
        private readonly double _scaleThreshold;
        private readonly string? _selectedTicker;
        private readonly string? _hoveredTicker;
        private readonly HeatmapColorScheme _colorScheme;

        public UniformHeatmapDrawOperation(
            UniformHeatmapControl owner,
            Rect bounds,
            IReadOnlyList<UniformHeatmapItem> items,
            double scaleThreshold,
            string? selectedTicker,
            string? hoveredTicker,
            HeatmapColorScheme colorScheme)
            : base(bounds)
        {
            _owner = owner;
            _items = items;
            _scaleThreshold = scaleThreshold;
            _selectedTicker = selectedTicker;
            _hoveredTicker = hoveredTicker;
            _colorScheme = colorScheme;
        }

        protected override void RenderCore(SKCanvas canvas, Rect bounds)
        {
            _owner.RenderSkia(
                canvas,
                bounds,
                _items,
                _scaleThreshold,
                _selectedTicker,
                _hoveredTicker,
                _colorScheme);
        }
    }
}
