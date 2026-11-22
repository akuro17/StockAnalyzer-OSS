using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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

    public static readonly StyledProperty<string?> SelectedTickerProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, string?>(nameof(SelectedTicker));

    public static readonly StyledProperty<string?> HoveredTickerProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, string?>(nameof(HoveredTicker));

    public static readonly StyledProperty<HeatmapPalette> PaletteProperty =
        AvaloniaProperty.Register<UniformHeatmapControl, HeatmapPalette>(nameof(Palette), HeatmapPalette.Default);

    public event EventHandler<UniformHeatmapItem>? TileClicked;

    // Fixed Grid Geometry
    public const float TargetTileWidth = 120.0f;
    public const float TargetTileHeight = 70.0f;
    public const float CellGap = 4.0f;

    // Perceptual Colors
    private static readonly SKColor NeutralColor = new(0x2B, 0x2F, 0x3A);         // Dark Slate Gray
    private static readonly SKColor NoDataColor = new(0x1E, 0x20, 0x24);          // Dark Neutral
    private static readonly SKColor TileBorderColor = new(0x37, 0x3A, 0x40);
    private static readonly SKColor HoverBorderColor = new(0xFA, 0xCC, 0x15);     // Yellow accent
    private static readonly SKColor SelectedBorderColor = new(0x38, 0xBD, 0xF8);  // Sky blue accent

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
        AffectsRender<UniformHeatmapControl>(ItemsProperty, SelectedTickerProperty, HoveredTickerProperty, PaletteProperty);
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

    public HeatmapPalette Palette
    {
        get => GetValue(PaletteProperty);
        set => SetValue(PaletteProperty, value);
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

        var palette = Palette;
        string? selected = SelectedTicker;
        string? hovered = HoveredTicker;

        context.Custom(new UniformHeatmapDrawOperation(
            this,
            new Rect(Bounds.Size),
            items,
            palette,
            selected,
            hovered));
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
        HeatmapPalette palette,
        string? selectedTicker,
        string? hoveredTicker)
    {
        if (items == null || items.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return;

        palette ??= HeatmapPalette.Default;

        float width = (float)bounds.Width;
        int n = items.Count;
        int columns = Math.Max(1, (int)Math.Floor((width + CellGap) / (TargetTileWidth + CellGap)));
        float actualTileWidth = (width - ((columns - 1) * CellGap)) / columns;

        string? hovered = hoveredTicker;
        string? selected = selectedTicker;

        // Configure text paints and alignment from palette snapshot
        float tickerFontSize = (float)palette.FontSize;
        float returnFontSize = (float)Math.Max(8.0, Math.Round(tickerFontSize * (12.0 / 14.0)));

        _tickerTextPaint.TextSize = tickerFontSize;
        _returnTextPaint.TextSize = returnFontSize;

        _shadowTextPaint.TextSize = returnFontSize;
        _noDataTextPaint.TextSize = returnFontSize;

        SKTextAlign skAlignment = palette.TextAlignment switch
        {
            HeatmapTextAlignment.Left => SKTextAlign.Left,
            HeatmapTextAlignment.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        _tickerTextPaint.TextAlign = skAlignment;
        _returnTextPaint.TextAlign = skAlignment;
        _noDataTextPaint.TextAlign = skAlignment;
        _shadowTextPaint.TextAlign = skAlignment;

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

                // Tile fill plus the WCAG-resolved ink for that fill (precomputed per palette; allocation-free)
                var (tileColor, inkColor, drawShadow) = GetPaletteRenderColors(item.ReturnPercentage, palette);
                _tickerTextPaint.Color = inkColor;
                _returnTextPaint.Color = inkColor;

                if (isNan)
                {
                    // Missing data: Dark neutral tile with diagonal line hatch pattern
                    canvas.Save();
                    canvas.ClipRoundRect(tileRect, antialias: true);
                    _tileFillPaint.Color = tileColor;
                    canvas.DrawRoundRect(tileRect, _tileFillPaint);

                    for (float hx = -TargetTileHeight; hx < actualTileWidth + TargetTileHeight; hx += 16.0f)
                    {
                        canvas.DrawLine(x + hx, y + TargetTileHeight, x + hx + TargetTileHeight, y, _hatchPaint);
                    }
                    canvas.Restore();
                }
                else
                {
                    // Perceptually uniform Neutral-to-Peak Oklab colour resolved via HeatmapPalette
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

                // Text Layout Coordinates (respecting alignment and font sizes)
                float textX = palette.TextAlignment switch
                {
                    HeatmapTextAlignment.Left => x + 8.0f,
                    HeatmapTextAlignment.Right => x + actualTileWidth - 8.0f,
                    _ => x + (actualTileWidth / 2.0f)
                };

                float textY = y + 10.0f + tickerFontSize;
                float retY = textY + returnFontSize + 8.0f;

                // Ticker Label
                canvas.DrawText(item.Ticker, textX, textY, _tickerTextPaint);

                // Return Percentage Label
                if (isNan)
                {
                    canvas.DrawText(" — ", textX, retY, _noDataTextPaint);
                }
                else
                {
                    double rounded = Math.Round(item.ReturnPercentage * 100.0, 2, MidpointRounding.AwayFromZero);
                    string returnStr = rounded switch
                    {
                        > 0.0 => $"+{rounded.ToString("F2", CultureInfo.InvariantCulture)}%",
                        < 0.0 => $"{rounded.ToString("F2", CultureInfo.InvariantCulture)}%",
                        _ => "0.00%"
                    };

                    // Subtle text shadow for light ink only; a dark shadow under dark ink would reduce legibility
                    if (drawShadow)
                    {
                        canvas.DrawText(returnStr, textX + 1.0f, retY + 1.0f, _shadowTextPaint);
                    }

                    canvas.DrawText(returnStr, textX, retY, _returnTextPaint);
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    private const int MaxLutIndex = HeatmapColorRamp.LutEntryCount - 1;

    /// <summary>
    /// Per-palette colour tables: tile fill, WCAG-resolved ink and shadow flag for every ramp slot.
    /// Built once per palette snapshot so RenderSkia performs no colour math.
    /// </summary>
    private sealed class PaletteLuts
    {
        public readonly SKColor Neutral;
        public readonly SKColor NeutralText;
        public readonly bool NeutralDrawShadow;

        public readonly SKColor[] Up = new SKColor[HeatmapColorRamp.LutEntryCount];
        public readonly SKColor[] UpText = new SKColor[HeatmapColorRamp.LutEntryCount];
        public readonly bool[] UpDrawShadow = new bool[HeatmapColorRamp.LutEntryCount];

        public readonly SKColor[] Down = new SKColor[HeatmapColorRamp.LutEntryCount];
        public readonly SKColor[] DownText = new SKColor[HeatmapColorRamp.LutEntryCount];
        public readonly bool[] DownDrawShadow = new bool[HeatmapColorRamp.LutEntryCount];

        public readonly SKColor NoDataText;
        public readonly bool NoDataDrawShadow;

        public PaletteLuts(HeatmapPalette palette)
        {
            Neutral = ToSkColor(palette.Neutral);
            (NeutralText, NeutralDrawShadow) = ResolveInk(palette.Neutral, palette.TextColor);

            var noDataBackground = new IndicatorColor(255, NoDataColor.Red, NoDataColor.Green, NoDataColor.Blue);
            (NoDataText, NoDataDrawShadow) = ResolveInk(noDataBackground, palette.TextColor);

            Span<IndicatorColor> ramp = stackalloc IndicatorColor[HeatmapColorRamp.LutEntryCount];
            FillRamp(palette.Neutral, palette.Up, palette.TextColor, ramp, Up, UpText, UpDrawShadow);
            FillRamp(palette.Neutral, palette.Down, palette.TextColor, ramp, Down, DownText, DownDrawShadow);
        }

        private static void FillRamp(
            IndicatorColor neutral,
            IndicatorColor peak,
            IndicatorColor preferredText,
            Span<IndicatorColor> ramp,
            SKColor[] fill,
            SKColor[] text,
            bool[] drawShadow)
        {
            HeatmapColorRamp.BuildLut(neutral, peak, ramp);
            for (int i = 0; i < ramp.Length; i++)
            {
                fill[i] = ToSkColor(ramp[i]);
                (text[i], drawShadow[i]) = ResolveInk(ramp[i], preferredText);
            }
        }

        private static (SKColor Ink, bool DrawShadow) ResolveInk(IndicatorColor background, IndicatorColor preferredText)
        {
            var (ink, isLightOnDarker) = WcagColorContrast.ResolveAccessibleTextColor(background, preferredText);
            return (ToSkColor(ink), isLightOnDarker);
        }

        private static SKColor ToSkColor(IndicatorColor c) => new(c.R, c.G, c.B, c.A);
    }

    private static readonly ConditionalWeakTable<HeatmapPalette, PaletteLuts> _paletteLutCache = new();

    /// <summary>
    /// Evaluates the perceptually uniform Neutral-to-Peak colour (Oklab) via settings-driven HeatmapPalette.
    /// </summary>
    public static SKColor GetPaletteColor(double ret, HeatmapPalette palette) =>
        GetPaletteRenderColors(ret, palette).FillColor;

    /// <summary>
    /// Resolves the tile fill, the WCAG 2.1 AA ink colour for that fill, and whether the light-ink shadow is drawn.
    /// With <see cref="HeatmapPalette.StepCount"/> == 1 every non-neutral tile shows the full peak colour.
    /// </summary>
    internal static (SKColor FillColor, SKColor TextColor, bool DrawShadow) GetPaletteRenderColors(double ret, HeatmapPalette palette)
    {
        palette ??= HeatmapPalette.Default;
        var luts = _paletteLutCache.GetValue(palette, static p => new PaletteLuts(p));

        var sample = HeatmapColorMapper.Classify(ret, palette.ThresholdRatio, palette.StepCount);
        switch (sample.Kind)
        {
            case HeatmapColorKind.Neutral:
                return (luts.Neutral, luts.NeutralText, luts.NeutralDrawShadow);
            case HeatmapColorKind.Up:
                {
                    int idx = ResolveLutIndex(sample.Factor, palette.StepCount);
                    return (luts.Up[idx], luts.UpText[idx], luts.UpDrawShadow[idx]);
                }
            case HeatmapColorKind.Down:
                {
                    int idx = ResolveLutIndex(sample.Factor, palette.StepCount);
                    return (luts.Down[idx], luts.DownText[idx], luts.DownDrawShadow[idx]);
                }
            default:
                return (NoDataColor, luts.NoDataText, luts.NoDataDrawShadow);
        }
    }

    private static int ResolveLutIndex(double factor, int stepCount)
    {
        // Classify yields factor 0 for a single step; that single level must render the full peak colour.
        double effectiveFactor = stepCount == 1 ? 1.0 : factor;
        return Math.Clamp((int)Math.Round(effectiveFactor * MaxLutIndex), 0, MaxLutIndex);
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
        private readonly HeatmapPalette _palette;
        private readonly string? _selectedTicker;
        private readonly string? _hoveredTicker;

        public UniformHeatmapDrawOperation(
            UniformHeatmapControl owner,
            Rect bounds,
            IReadOnlyList<UniformHeatmapItem> items,
            HeatmapPalette palette,
            string? selectedTicker,
            string? hoveredTicker)
            : base(bounds)
        {
            _owner = owner;
            _items = items;
            _palette = palette;
            _selectedTicker = selectedTicker;
            _hoveredTicker = hoveredTicker;
        }

        protected override void RenderCore(SKCanvas canvas, Rect bounds)
        {
            _owner.RenderSkia(
                canvas,
                bounds,
                _items,
                _palette,
                _selectedTicker,
                _hoveredTicker);
        }
    }
}
