using System;
using Avalonia;
using SkiaSharp;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Core.Models.ReinforcementLearning;

namespace StockAnalyzer.Avalonia.Views.Chart.Renderers;

/// <summary>Reusable paints and cached UI strings; no model work or formatting in either draw path.</summary>
public sealed class RlQRenderer : IDisposable
{
    private readonly SKPaint _paint = new() { IsAntialias = true, StrokeWidth = (float)RlContract.StrokeThickness };
    private readonly SKFont _font = new();
    private readonly object _gate = new();
    private bool _disposed;
    private RlPresentation? _hudPresentation, _panelPresentation;
    private float _hudSize, _baseSize, _helperSize;
    private CachedText? _hud, _minimum, _maximum, _units, _legend, _hold, _buy, _sell;
    private sealed class CachedText : IDisposable
    {
        public readonly SKTextBlob Blob;
        public readonly float Width;
        public CachedText(string text, SKFont font, SKPaint paint)
        { Blob = SKTextBlob.Create(text, font); Width = paint.MeasureText(text); }
        public void Dispose() => Blob.Dispose();
    }
    /// <summary>Called on presentation/font changes, outside the render path.</summary>
    public void PrepareHud(RlPresentation? p, float size) { lock (_gate) { if (!_disposed) PrepareHudCore(p, size); } }
    private void PrepareHudCore(RlPresentation? p, float size)
    {
        if (ReferenceEquals(p, _hudPresentation) && size == _hudSize) return;
        _hud?.Dispose(); _hud = null; _hudPresentation = p; _hudSize = size;
        if (p is null || size <= 0) return;
        _font.Size = size; _paint.TextSize = size; _hud = new(p.HudText, _font, _paint);
    }
    /// <summary>Creates immutable text blobs before Avalonia schedules the drawing operation.</summary>
    public void PreparePanel(RlPresentation? p, float baseSize, float helperSize) { lock (_gate) { if (!_disposed) PreparePanelCore(p, baseSize, helperSize); } }
    private void PreparePanelCore(RlPresentation? p, float baseSize, float helperSize)
    {
        if (ReferenceEquals(p, _panelPresentation) && baseSize == _baseSize && helperSize == _helperSize) return;
        ClearPanel(); _panelPresentation = p; _baseSize = baseSize; _helperSize = helperSize;
        if (p is null || baseSize <= 0 || helperSize <= 0) return;
        _font.Size = helperSize; _paint.TextSize = helperSize;
        _minimum = new(p.AxisMinimumText, _font, _paint); _maximum = new(p.AxisMaximumText, _font, _paint);
        _units = new(p.Units, _font, _paint); _legend = new(p.Legend, _font, _paint);
        _font.Size = baseSize; _paint.TextSize = baseSize;
        _hold = new(p.HoldText, _font, _paint); _buy = new(p.BuyText, _font, _paint); _sell = new(p.SellText, _font, _paint);
    }
    private void ClearPanel()
    {
        _minimum?.Dispose(); _maximum?.Dispose(); _units?.Dispose(); _legend?.Dispose(); _hold?.Dispose(); _buy?.Dispose(); _sell?.Dispose();
        _minimum = _maximum = _units = _legend = _hold = _buy = _sell = null;
    }
    public void DrawHud(SKCanvas canvas, Rect area, RlPresentation? p, float fontSize, SKColor foreground)
    { lock (_gate) { if (!_disposed) DrawHudCore(canvas, area, p, fontSize, foreground); } }
    private void DrawHudCore(SKCanvas canvas, Rect area, RlPresentation? p, float fontSize, SKColor foreground)
    {
        if (p is null || _hud is null || !ReferenceEquals(p, _hudPresentation) || fontSize != _hudSize) return;
        _paint.Color = foreground;
        float inset = (float)RlContract.HudInset;
        float width = _hud.Width;
        if (width + inset * 2 > area.Width || fontSize + inset * 2 > area.Height) return;
        canvas.Save(); canvas.ClipRect(new SKRect((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom));
        canvas.DrawText(_hud.Blob, (float)area.Right - inset - width, (float)area.Top + inset + fontSize, _paint);
        canvas.Restore();
    }
    public void DrawPanel(SKCanvas canvas, Rect area, RlPresentation p, float baseSize, float helperSize,
        SKColor foreground, SKColor hold, SKColor buy, SKColor sell)
    { lock (_gate) { if (!_disposed) DrawPanelCore(canvas, area, p, baseSize, helperSize, foreground, hold, buy, sell); } }
    private void DrawPanelCore(SKCanvas canvas, Rect area, RlPresentation p, float baseSize, float helperSize,
        SKColor foreground, SKColor hold, SKColor buy, SKColor sell)
    {
        if (area.Width <= 0 || area.Height <= 0 || _hold is null || !ReferenceEquals(p, _panelPresentation)
            || baseSize != _baseSize || helperSize != _helperSize) return;
        float margin = (float)RlContract.PanelVerticalMargin;
        _paint.Color = foreground;
        float axisWidth = Math.Max(_minimum!.Width, _maximum!.Width) + margin;
        float left = (float)area.Left + axisWidth, right = (float)area.Right - margin;
        float top = (float)area.Top + margin + baseSize, bottom = (float)area.Bottom - margin - baseSize;
        if (right <= left || bottom <= top) return;
        canvas.Save(); canvas.ClipRect(new SKRect((float)area.Left, (float)area.Top, (float)area.Right, (float)area.Bottom));
        float Y(float q) => (float)(bottom - ((double)q - p.AxisMinimum) / (p.AxisMaximum - p.AxisMinimum) * (bottom - top));
        float zero = Y(0); _paint.Style = SKPaintStyle.Stroke;
        canvas.DrawLine(left, zero, right, zero, _paint); _paint.Style = SKPaintStyle.Fill;
        canvas.DrawText(_maximum.Blob, (float)area.Left, top, _paint);
        canvas.DrawText(_minimum.Blob, (float)area.Left, bottom, _paint);
        canvas.DrawText(_units!.Blob, left, (float)area.Top + margin, _paint);
        float legendWidth = _legend!.Width;
        if (legendWidth < right - left) canvas.DrawText(_legend.Blob, right - legendWidth, (float)area.Top + margin, _paint);
        float slot = (right - left) / RlContract.ActionCount;
        DrawBar(canvas, left, slot, zero, Y(p.Hold), hold, _hold, bottom + baseSize, foreground, p.SelectedAction == RlAction.Hold);
        DrawBar(canvas, left + slot, slot, zero, Y(p.Buy), buy, _buy!, bottom + baseSize, foreground, p.SelectedAction == RlAction.Buy);
        DrawBar(canvas, left + slot * 2, slot, zero, Y(p.Sell), sell, _sell!, bottom + baseSize, foreground, p.SelectedAction == RlAction.Sell);
        canvas.Restore();
    }
    private void DrawBar(SKCanvas canvas, float x, float slot, float zero, float qY, SKColor color, CachedText label, float labelY, SKColor foreground, bool selected)
    {
        float inset = (float)RlContract.PanelVerticalMargin;
        _paint.Color = color;
        if (slot > inset * 2)
        {
            var bar = new SKRect(x + inset, Math.Min(zero, qY), x + slot - inset, Math.Max(zero, qY));
            canvas.DrawRect(bar, _paint);
            if (selected) { _paint.Color = foreground; _paint.Style = SKPaintStyle.Stroke; _paint.StrokeWidth = (float)RlContract.StrokeThickness; canvas.DrawRect(bar, _paint); _paint.Style = SKPaintStyle.Fill; }
        }
        _paint.Color = foreground; float width = label.Width;
        if (width < slot) canvas.DrawText(label.Blob, x + (slot - width) / 2, labelY, _paint);
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _hud?.Dispose(); ClearPanel(); _paint.Dispose(); _font.Dispose(); } }
}
