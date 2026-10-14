using System;
using System.Reflection;
using System.Text.Json;
using SkiaSharp;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Drawing;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class TextAnnotatedFontTests
{
    [Fact]
    public void TextObject_FontFamily_UpdatesTypeface_AndInvalidatesCache()
    {
        var pt = new ChartPoint(DateTime.UtcNow, 100m);
        var textObj = new TextObject(pt, "Sample");

        Assert.Null(textObj.FontFamily);

        var paintField = typeof(TextObject).GetField("_textPaint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(paintField);
        var initialPaint = (SKPaint)paintField.GetValue(textObj)!;
        Assert.Same(DrawingThemeContext.DefaultTextTypeface, initialPaint.Typeface);

        // Set to a font
        textObj.FontFamily = "Arial";
        Assert.Equal("Arial", textObj.FontFamily);

        var updatedPaint = (SKPaint)paintField.GetValue(textObj)!;
        var expectedArial = DrawingFontHelper.ResolveTypeface("Arial");
        Assert.Same(expectedArial, updatedPaint.Typeface);

        // Reset to null
        textObj.FontFamily = null;
        Assert.Null(textObj.FontFamily);
        Assert.Same(DrawingThemeContext.DefaultTextTypeface, updatedPaint.Typeface);
    }

    [Fact]
    public void CalloutObject_FontFamily_UpdatesTypeface()
    {
        var p1 = new ChartPoint(DateTime.UtcNow, 100m);
        var p2 = new ChartPoint(DateTime.UtcNow, 110m);
        var callout = new CalloutObject(p1, p2);

        Assert.Null(callout.FontFamily);
        callout.FontFamily = "Arial";
        Assert.Equal("Arial", callout.FontFamily);

        var paintField = typeof(CalloutObject).GetField("_textPaint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(paintField);
        var paint = (SKPaint)paintField.GetValue(callout)!;
        Assert.Same(DrawingFontHelper.ResolveTypeface("Arial"), paint.Typeface);
    }

    [Fact]
    public void ClothoidCalloutObject_FontFamily_UpdatesTypeface()
    {
        var p1 = new ChartPoint(DateTime.UtcNow, 100m);
        var p2 = new ChartPoint(DateTime.UtcNow, 110m);
        var clothoid = new ClothoidCalloutObject(p1, p2);

        Assert.Null(clothoid.FontFamily);
        clothoid.FontFamily = "Arial";
        Assert.Equal("Arial", clothoid.FontFamily);

        var paintField = typeof(ClothoidCalloutObject).GetField("_textPaint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(paintField);
        var paint = (SKPaint)paintField.GetValue(clothoid)!;
        Assert.Same(DrawingFontHelper.ResolveTypeface("Arial"), paint.Typeface);
    }

    [Fact]
    public void LineTextObject_TopAndBottomFontFamily_AreIndependent()
    {
        var p1 = new ChartPoint(DateTime.UtcNow, 100m);
        var p2 = new ChartPoint(DateTime.UtcNow.AddDays(1), 120m);
        var lineText = new LineTextObject(p1, p2);

        Assert.Null(lineText.TopFontFamily);
        Assert.Null(lineText.BottomFontFamily);

        lineText.TopFontFamily = "Arial";
        lineText.BottomFontFamily = "Times New Roman";

        Assert.Equal("Arial", lineText.TopFontFamily);
        Assert.Equal("Times New Roman", lineText.BottomFontFamily);

        var annotField = typeof(LineTextObject).GetField("_annotations", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(annotField);
        var annot = (LineTextAnnotationSet)annotField.GetValue(lineText)!;

        var topPaintField = typeof(LineTextAnnotationSet).GetField("_topTextPaint", BindingFlags.NonPublic | BindingFlags.Instance);
        var bottomPaintField = typeof(LineTextAnnotationSet).GetField("_bottomTextPaint", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(topPaintField);
        Assert.NotNull(bottomPaintField);

        var topPaint = (SKPaint)topPaintField.GetValue(annot)!;
        var bottomPaint = (SKPaint)bottomPaintField.GetValue(annot)!;

        Assert.Same(DrawingFontHelper.ResolveTypeface("Arial"), topPaint.Typeface);
        Assert.Same(DrawingFontHelper.ResolveTypeface("Times New Roman"), bottomPaint.Typeface);
    }

    [Fact]
    public void CurveLineTextObject_TopAndBottomFontFamily_AreIndependent()
    {
        var curveText = new CurveLineTextObject();

        curveText.TopFontFamily = "Arial";
        curveText.BottomFontFamily = "Courier New";

        Assert.Equal("Arial", curveText.TopFontFamily);
        Assert.Equal("Courier New", curveText.BottomFontFamily);
    }

    [Fact]
    public void Codec_RoundTrip_PreservesFontFamilyProperties()
    {
        var manager = new ChartObjectManager();
        var key = new DrawingDocumentKey("TEST", TimeframeType.Daily);

        var textObj = new TextObject(new ChartPoint(DateTime.UtcNow, 100m), "Annotated")
        {
            FontFamily = "Arial"
        };
        var lineText = new LineTextObject(
            new ChartPoint(DateTime.UtcNow, 100m),
            new ChartPoint(DateTime.UtcNow.AddDays(1), 120m))
        {
            TopText = "Upper",
            TopFontFamily = "Arial",
            BottomText = "Lower",
            BottomFontFamily = "Times New Roman"
        };

        manager.AddObject(textObj);
        manager.AddObject(lineText);

        var state = DrawingDocumentCodec.Capture(manager, key, 1L);
        using var materialized = DrawingDocumentCodec.Materialize(state);

        var objects = materialized.Objects[ChartDrawingContextType.Standard];
        Assert.Equal(2, objects.Count);

        var restoredText = Assert.IsType<TextObject>(objects[0]);
        Assert.Equal("Arial", restoredText.FontFamily);

        var restoredLine = Assert.IsType<LineTextObject>(objects[1]);
        Assert.Equal("Arial", restoredLine.TopFontFamily);
        Assert.Equal("Times New Roman", restoredLine.BottomFontFamily);
    }

    [Fact]
    public void Codec_BackwardCompatibility_NullFontFamilyOnMissingProperty()
    {
        // JSON from an older version that didn't have fontFamily property
        var legacyJson = @"{
            ""$type"": ""TextObject"",
            ""points"": [{""time"": ""2024-01-01T00:00:00Z"", ""price"": 100.0}],
            ""text"": ""Legacy text"",
            ""fontSize"": 14.0
        }";

        var options = new JsonSerializerOptions();
        options.Converters.Add(new ChartObjectJsonConverter());

        var obj = JsonSerializer.Deserialize<IChartObject>(legacyJson, options);
        var textObj = Assert.IsType<TextObject>(obj);

        Assert.Equal("Legacy text", textObj.Text);
        Assert.Null(textObj.FontFamily);
    }
}
