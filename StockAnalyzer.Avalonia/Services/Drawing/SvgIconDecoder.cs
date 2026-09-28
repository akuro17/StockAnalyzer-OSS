using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Lightweight SVG icon parser and rasterizer.
/// Parses SVG path elements and viewBox attributes, rendering scalable icons
/// into high-resolution (256x256) SKBitmap representations for Zero-Allocation 60fps chart rendering.
/// </summary>
public static class SvgIconDecoder
{
    private const int TargetRasterSize = 256;
    private const float Padding = 16f;

    public static SKBitmap? RenderToSkBitmap(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        try
        {
            var content = File.ReadAllText(filePath);
            return RenderToSkBitmapFromXml(content);
        }
        catch
        {
            return null;
        }
    }

    public static SKBitmap? RenderToSkBitmapFromXml(string svgXml)
    {
        if (string.IsNullOrWhiteSpace(svgXml)) return null;

        try
        {
            var doc = XDocument.Parse(svgXml);
            var svgElement = doc.Root;
            if (svgElement == null || !svgElement.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
                return null;

            // Extract all path 'd' data
            var paths = svgElement.Descendants()
                .Where(e => e.Name.LocalName.Equals("path", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("d")?.Value)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList();

            if (paths.Count == 0) return null;

            using var combinedPath = new SKPath();
            foreach (var d in paths)
            {
                using var parsed = SKPath.ParseSvgPathData(d!);
                if (parsed != null)
                {
                    combinedPath.AddPath(parsed);
                }
            }

            if (combinedPath.IsEmpty) return null;

            // Determine source bounds
            SKRect srcBounds;
            var viewBoxAttr = svgElement.Attribute("viewBox")?.Value;
            if (!string.IsNullOrWhiteSpace(viewBoxAttr))
            {
                var parts = viewBoxAttr.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 &&
                    float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vx) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vy) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vw) &&
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vh) &&
                    vw > 0 && vh > 0)
                {
                    srcBounds = new SKRect(vx, vy, vx + vw, vy + vh);
                }
                else
                {
                    srcBounds = combinedPath.TightBounds;
                }
            }
            else
            {
                srcBounds = combinedPath.TightBounds;
            }

            if (srcBounds.Width <= 0 || srcBounds.Height <= 0) return null;

            // Create 256x256 transparent bitmap
            var bitmap = new SKBitmap(TargetRasterSize, TargetRasterSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);

                float availableSize = TargetRasterSize - Padding * 2f;
                float scale = Math.Min(availableSize / srcBounds.Width, availableSize / srcBounds.Height);

                float destW = srcBounds.Width * scale;
                float destH = srcBounds.Height * scale;
                float destX = Padding + (availableSize - destW) / 2f;
                float destY = Padding + (availableSize - destH) / 2f;

                canvas.Translate(destX, destY);
                canvas.Scale(scale);
                canvas.Translate(-srcBounds.Left, -srcBounds.Top);

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = SKColors.White // Solid white with alpha mask for clean color tinting
                };

                canvas.DrawPath(combinedPath, paint);
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
