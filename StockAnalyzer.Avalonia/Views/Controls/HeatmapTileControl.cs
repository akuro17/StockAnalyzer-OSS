using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Theme;

namespace StockAnalyzer.Avalonia.Views.Controls;

public readonly record struct TileRenderData(SKRect Rect, int LutIndex, string Ticker, string ReturnText, bool ShouldDrawText);
public readonly record struct TileBounds(SKRect Rect, string Ticker);

public sealed class HeatmapRenderSnapshot
{
    public static readonly HeatmapRenderSnapshot Empty = new(
        Array.Empty<TileRenderData>(),
        Array.Empty<SKRect>(),
        Array.Empty<TileBounds>());

    public TileRenderData[] Tiles { get; }
    public SKRect[] GroupBorders { get; }
    public TileBounds[] HitMap { get; }

    public HeatmapRenderSnapshot(
        TileRenderData[] tiles,
        SKRect[] groupBorders,
        TileBounds[] hitMap)
    {
        Tiles = tiles;
        GroupBorders = groupBorders;
        HitMap = hitMap;
    }
}

file sealed class HeatmapDrawOperation : ICustomDrawOperation
{
    private readonly global::Avalonia.Rect _bounds;
    private readonly HeatmapRenderSnapshot _snapshot;
    private readonly SKPaint[] _fillLut;
    private readonly SKPaint? _borderPaint;
    private readonly SKPaint? _textPaint;
    private readonly SKPaint? _largeTextPaint;
    private readonly SKPaint? _groupBorderPaint;

    public HeatmapDrawOperation(
        global::Avalonia.Rect bounds,
        HeatmapRenderSnapshot snapshot,
        SKPaint[] fillLut,
        SKPaint? borderPaint,
        SKPaint? textPaint,
        SKPaint? largeTextPaint,
        SKPaint? groupBorderPaint)
    {
        _bounds = bounds;
        _snapshot = snapshot;
        _fillLut = fillLut;
        _borderPaint = borderPaint;
        _textPaint = textPaint;
        _largeTextPaint = largeTextPaint;
        _groupBorderPaint = groupBorderPaint;
    }

    public global::Avalonia.Rect Bounds => _bounds;
    public void Dispose() { /* Managed snapshot; native paints are owned and disposed by HeatmapTileControl */ }
    public bool HitTest(global::Avalonia.Point p) => _bounds.Contains(p);
    public bool Equals(ICustomDrawOperation? other) =>
        other is HeatmapDrawOperation op &&
        ReferenceEquals(_snapshot, op._snapshot) &&
        _bounds == op._bounds;

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (leaseFeature == null) return;

        var tiles = _snapshot.Tiles;
        if (tiles.Length == 0) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            canvas.DrawRect(tile.Rect, _fillLut[tile.LutIndex]);
            if (_borderPaint != null)
            {
                canvas.DrawRect(tile.Rect, _borderPaint);
            }

            if (tile.ShouldDrawText && _largeTextPaint != null && _textPaint != null)
            {
                canvas.Save();
                canvas.ClipRect(tile.Rect);
                canvas.DrawText(tile.Ticker, tile.Rect.Left + 4, tile.Rect.Top + 13, _largeTextPaint);
                canvas.DrawText(tile.ReturnText, tile.Rect.Left + 4, tile.Rect.Top + 24, _textPaint);
                canvas.Restore();
            }
        }

        if (_groupBorderPaint != null)
        {
            var borders = _snapshot.GroupBorders;
            for (int i = 0; i < borders.Length; i++)
            {
                canvas.DrawRect(borders[i], _groupBorderPaint);
            }
        }
    }
}

public class HeatmapTileControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<HeatmapEntry>?> EntriesProperty =
        AvaloniaProperty.Register<HeatmapTileControl, IReadOnlyList<HeatmapEntry>?>(nameof(Entries));

    public IReadOnlyList<HeatmapEntry>? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    private readonly SKPaint[] _fillLut = new SKPaint[101];
    private SKPaint? _borderPaint;
    private SKPaint? _textPaint;
    private SKPaint? _largeTextPaint;
    private SKPaint? _groupBorderPaint;
    private ThemeColors? _lastTheme;

    private HeatmapRenderSnapshot _currentSnapshot = HeatmapRenderSnapshot.Empty;
    private IReadOnlyList<HeatmapEntry>? _lastEntries;
    private Size _lastBoundsSize;
    private bool _isSnapshotDirty = true;

    static HeatmapTileControl()
    {
        AffectsRender<HeatmapTileControl>(EntriesProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EntriesProperty)
        {
            _isSnapshotDirty = true;
        }
    }

    public void InvalidateSnapshot()
    {
        _isSnapshotDirty = true;
        InvalidateVisual();
    }

    private void InitializePaints(ThemeColors colors)
    {
        if (_lastTheme == colors && _borderPaint != null) return;
        _lastTheme = colors;

        var bullishColor = colors.Bullish.ToSkColor();
        var bearishColor = colors.Bearish.ToSkColor();
        var midpointColor = colors.ChartBackground.ToSkColor();

        for (int i = 0; i <= 100; i++)
        {
            float t = i / 100f;
            SKColor color;
            if (t < 0.5f)
            {
                color = Interpolate(bearishColor, midpointColor, t * 2);
            }
            else
            {
                color = Interpolate(midpointColor, bullishColor, (t - 0.5f) * 2);
            }
            if (_fillLut[i] == null)
            {
                _fillLut[i] = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
            }
            _fillLut[i].Color = color;
        }

        if (_borderPaint == null) _borderPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        _borderPaint.Color = colors.ShellBorder.ToSkColor();

        if (_groupBorderPaint == null) _groupBorderPaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        _groupBorderPaint.Color = colors.ShellBorder.ToSkColor();

        if (_textPaint == null) _textPaint = new SKPaint { IsAntialias = true, TextSize = 10 };
        _textPaint.Color = SKColors.White;

        if (_largeTextPaint == null) _largeTextPaint = new SKPaint { IsAntialias = true, TextSize = 13, FakeBoldText = true };
        _largeTextPaint.Color = SKColors.White;
    }

    private static SKColor Interpolate(SKColor c1, SKColor c2, float t)
    {
        return new SKColor(
            (byte)(c1.Red + (c2.Red - c1.Red) * t),
            (byte)(c1.Green + (c2.Green - c1.Green) * t),
            (byte)(c1.Blue + (c2.Blue - c1.Blue) * t)
        );
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Volatile.Write(ref _currentSnapshot, HeatmapRenderSnapshot.Empty);
        _lastEntries = null;
        _isSnapshotDirty = true;
        for (int i = 0; i <= 100; i++)
        {
            _fillLut[i]?.Dispose();
            _fillLut[i] = null!;
        }
        _borderPaint?.Dispose();
        _borderPaint = null;
        _textPaint?.Dispose();
        _textPaint = null;
        _largeTextPaint?.Dispose();
        _largeTextPaint = null;
        _groupBorderPaint?.Dispose();
        _groupBorderPaint = null;
        _lastTheme = null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        var skP = new SKPoint((float)p.X, (float)p.Y);

        var snapshot = Volatile.Read(ref _currentSnapshot);
        var hitMap = snapshot.HitMap;
        for (int i = 0; i < hitMap.Length; i++)
        {
            if (hitMap[i].Rect.Contains(skP.X, skP.Y))
            {
                WeakReferenceMessenger.Default.Send(new TickerSelectedMessage(hitMap[i].Ticker));
                break;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        var entries = Entries;
        if (entries == null || entries.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            Volatile.Write(ref _currentSnapshot, HeatmapRenderSnapshot.Empty);
            return;
        }

        var themeManager = App.Current?.Services?.GetService<IThemeManager>();
        var themeColors = themeManager?.CurrentTheme ?? ThemeColors.Dark;
        InitializePaints(themeColors);

        if (_isSnapshotDirty || !ReferenceEquals(entries, _lastEntries) || Bounds.Size != _lastBoundsSize || Volatile.Read(ref _currentSnapshot) == HeatmapRenderSnapshot.Empty)
        {
            _isSnapshotDirty = false;
            _lastEntries = entries;
            _lastBoundsSize = Bounds.Size;
            var newSnapshot = BuildSnapshot(entries, new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height));
            Volatile.Write(ref _currentSnapshot, newSnapshot);
        }

        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot.Tiles.Length > 0)
        {
            context.Custom(new HeatmapDrawOperation(
                new global::Avalonia.Rect(Bounds.Size),
                snapshot,
                _fillLut,
                _borderPaint,
                _textPaint,
                _largeTextPaint,
                _groupBorderPaint));
        }
    }

    private static HeatmapRenderSnapshot BuildSnapshot(IReadOnlyList<HeatmapEntry> entries, SKRect bounds)
    {
        var tiles = new List<TileRenderData>(entries.Count);
        var groupBorders = new List<SKRect>();
        var hitMap = new List<TileBounds>(entries.Count);

        // 1. Hierarchical Grouping (Region > Sector)
        var grouped = entries
            .GroupBy(e => e.Region)
            .Select(rg => new GroupInfo(
                rg.Key,
                (float)rg.Sum(e => e.Weight),
                rg.GroupBy(e => e.Sector)
                  .Select(sg => new GroupInfo(
                      null, // Sub-group title omitted for simplicity here
                      (float)sg.Sum(e => e.Weight),
                      null,
                      sg.OrderByDescending(e => e.Weight).ToList()))
                  .OrderByDescending(s => s.Weight)
                  .ToList(),
                null))
            .OrderByDescending(r => r.Weight)
            .ToList();

        // 2. Proportional Layout Calculation (Slice-and-Dice)
        LayoutHierarchical(bounds, grouped, true, tiles, groupBorders, hitMap);

        return new HeatmapRenderSnapshot(tiles.ToArray(), groupBorders.ToArray(), hitMap.ToArray());
    }

    private static void LayoutHierarchical(
        SKRect rect,
        List<GroupInfo> groups,
        bool horizontal,
        List<TileRenderData> tiles,
        List<SKRect> groupBorders,
        List<TileBounds> hitMap)
    {
        float totalWeight = groups.Sum(g => g.Weight);
        if (totalWeight <= 0 || rect.Width <= 0 || rect.Height <= 0) return;

        float currentOffset = horizontal ? rect.Left : rect.Top;

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            float ratio = g.Weight / totalWeight;
            SKRect slice;

            if (horizontal)
            {
                float w = rect.Width * ratio;
                slice = new SKRect(currentOffset, rect.Top, currentOffset + w, rect.Bottom);
                currentOffset += w;
            }
            else
            {
                float h = rect.Height * ratio;
                slice = new SKRect(rect.Left, currentOffset, rect.Right, currentOffset + h);
                currentOffset += h;
            }

            if (g.SubGroups != null)
            {
                // Recursive call for next level (vertical)
                LayoutHierarchical(slice, g.SubGroups, !horizontal, tiles, groupBorders, hitMap);
                
                // Draw Region boundary
                if (!string.IsNullOrEmpty(g.Title))
                {
                    groupBorders.Add(slice);
                }
            }
            else if (g.Tickers != null)
            {
                // Bottom level (Tickers)
                LayoutTickers(slice, g.Tickers, !horizontal, tiles, hitMap);
            }
        }
    }

    private static void LayoutTickers(
        SKRect rect,
        List<HeatmapEntry> tickers,
        bool horizontal,
        List<TileRenderData> tiles,
        List<TileBounds> hitMap)
    {
        float totalWeight = tickers.Sum(t => (float)t.Weight);
        if (totalWeight <= 0) return;

        float currentOffset = horizontal ? rect.Left : rect.Top;

        for (int i = 0; i < tickers.Count; i++)
        {
            var t = tickers[i];
            float ratio = (float)t.Weight / totalWeight;
            SKRect tile;

            if (horizontal)
            {
                float w = rect.Width * ratio;
                tile = new SKRect(currentOffset, rect.Top, currentOffset + w, rect.Bottom);
                currentOffset += w;
            }
            else
            {
                float h = rect.Height * ratio;
                tile = new SKRect(rect.Left, currentOffset, rect.Right, currentOffset + h);
                currentOffset += h;
            }

            hitMap.Add(new TileBounds(tile, t.Ticker));

            float ret = (float)t.Return;
            int lutIndex = Math.Clamp((int)((ret + 0.05f) * 1000), 0, 100);
            bool shouldDrawText = tile.Width > 35 && tile.Height > 25;
            tiles.Add(new TileRenderData(tile, lutIndex, t.Ticker, $"{t.Return:P2}", shouldDrawText));
        }
    }

    private class GroupInfo
    {
        public string? Title { get; }
        public float Weight { get; }
        public List<GroupInfo>? SubGroups { get; }
        public List<HeatmapEntry>? Tickers { get; }

        public GroupInfo(string? title, float weight, List<GroupInfo>? subGroups, List<HeatmapEntry>? tickers)
        {
            Title = title;
            Weight = weight;
            SubGroups = subGroups;
            Tickers = tickers;
        }
    }
}
