using SkiaSharp;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// The single definition of which paint a bin of a Volume Profile / Time at Price histogram gets, shared by the indicator renderer and both drawing tools:
/// the Point of Control bin first, else a Value Area bin, else the base colour. The Point of Control bin always lies inside the Value Area, so without this
/// precedence its own colour would never be visible. No allocation (render loop).
/// </summary>
internal static class ProfileBinPaintSelector
{
    public static SKPaint Select(bool isPointOfControlBin, bool isValueAreaBin, SKPaint pocPaint, SKPaint valueAreaPaint, SKPaint basePaint)
        => isPointOfControlBin ? pocPaint : isValueAreaBin ? valueAreaPaint : basePaint;
}
