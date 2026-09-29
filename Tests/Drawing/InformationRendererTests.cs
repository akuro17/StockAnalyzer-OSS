using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class InformationRendererTests
{
    [Fact]
    public void TruncateWithEllipsis_ShortText_ReturnsOriginal()
    {
        using var paint = new SKPaint { TextSize = 12f };
        string text = "RSI (14)";
        float textWidth = paint.MeasureText(text);

        string result = InformationRenderer.TruncateWithEllipsis(text, textWidth + 50f, paint);

        Assert.Equal(text, result);
    }

    [Fact]
    public void TruncateWithEllipsis_LongText_AppendsEllipsisAndFitsMaxWidth()
    {
        using var paint = new SKPaint { TextSize = 12f };
        string longName = "Adaptive Moving Average (10, 2, 30) Very Long Name Series";
        float fullWidth = paint.MeasureText(longName);
        float constrainedWidth = fullWidth * 0.5f;

        string result = InformationRenderer.TruncateWithEllipsis(longName, constrainedWidth, paint);

        Assert.EndsWith("...", result);
        Assert.True(result.Length < longName.Length);
        float resultWidth = paint.MeasureText(result);
        Assert.True(resultWidth <= constrainedWidth, $"Measured result {resultWidth} exceeded {constrainedWidth}");
    }

    [Fact]
    public void TruncateWithEllipsis_ExtremeNarrowWidth_HandlesSafely()
    {
        using var paint = new SKPaint { TextSize = 12f };
        string text = "MACD (12, 26, 9)";

        string result = InformationRenderer.TruncateWithEllipsis(text, 5f, paint);

        Assert.NotNull(result);
        Assert.True(result == "..." || result == string.Empty);
    }

    [Fact]
    public void TruncateWithEllipsis_NullOrEmpty_ReturnsEmpty()
    {
        using var paint = new SKPaint { TextSize = 12f };

        Assert.Equal(string.Empty, InformationRenderer.TruncateWithEllipsis(null, 100f, paint));
        Assert.Equal(string.Empty, InformationRenderer.TruncateWithEllipsis(string.Empty, 100f, paint));
        Assert.Equal(string.Empty, InformationRenderer.TruncateWithEllipsis("SMA", 0f, paint));
        Assert.Equal(string.Empty, InformationRenderer.TruncateWithEllipsis("SMA", -10f, paint));
    }

    [Fact]
    public void CalculateCardPosition_StandardChartArea_PositionsAtTopLeftWithMargin()
    {
        var chartArea = new global::Avalonia.Rect(0, 0, 1000, 600);
        var pos = InformationRenderer.CalculateCardPosition(chartArea, 300f, 200f);

        Assert.Equal(10f, pos.X);
        Assert.Equal(10f, pos.Y);
    }

    [Fact]
    public void CalculateCardPosition_WithChartOffsets_PositionsAtTopLeftRelativeOffset()
    {
        var chartArea = new global::Avalonia.Rect(50, 40, 1000, 600);
        var pos = InformationRenderer.CalculateCardPosition(chartArea, 300f, 200f);

        Assert.Equal(60f, pos.X);
        Assert.Equal(50f, pos.Y);
    }

    [Fact]
    public void CalculateCardPosition_NarrowChartArea_ClampsWithinBounds()
    {
        var chartArea = new global::Avalonia.Rect(0, 0, 250, 150);
        var pos = InformationRenderer.CalculateCardPosition(chartArea, 300f, 200f);

        Assert.True(pos.X >= 0f);
        Assert.True(pos.Y >= 0f);
        Assert.Equal(5f, pos.X); // Math.Max(0 + 5f, 250 - 300 - 5f) -> 5f
        Assert.Equal(5f, pos.Y); // Math.Max(0 + 5f, 150 - 200 - 5f) -> 5f
    }
}
