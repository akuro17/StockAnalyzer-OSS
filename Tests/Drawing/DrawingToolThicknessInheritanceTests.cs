using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

/// <summary>
/// Settings -> Chart -> Drawings -> Line Thickness: every newly-created drawing tool MUST
/// inherit its initial Thickness from DrawingThemeContext.DefaultStrokeThickness (SA_UI_INTERACTION.md
/// "Default Inheritance with Individual Override"). Regression test for a bug where several
/// tools' constructors set Thickness to a hardcoded literal AFTER their base class already
/// inherited the live global default, silently overriding it (e.g. RangeSplineObject reported
/// as always 2.0 regardless of the configured Line Thickness setting).
/// </summary>
public class DrawingToolThicknessInheritanceTests
{
    [Fact]
    public void RangeSplineObject_InheritsGlobalDefaultThickness()
    {
        var obj = new RangeSplineObject();
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
    }

    [Fact]
    public void NurbsHyperbolaObject_InheritsGlobalDefaultThickness()
    {
        var obj = new NurbsHyperbolaObject();
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
    }

    [Fact]
    public void NurbsConicObject_InheritsGlobalDefaultThickness()
    {
        var obj = new NurbsConicObject();
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
    }

    [Fact]
    public void NurbsEllipseObject_InheritsGlobalDefaultThickness()
    {
        var obj = new NurbsEllipseObject();
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
    }

    [Fact]
    public void NurbsParabolaObject_InheritsGlobalDefaultThickness()
    {
        var obj = new NurbsParabolaObject();
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
    }

    [Fact]
    public void NurbsTrendCurveObject_InheritsGlobalDefaultThickness()
    {
        var obj = new NurbsTrendCurveObject();
        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, obj.Thickness);
    }

    [Fact]
    public void DtwProjectionBehavior_CreatedObject_InheritsGlobalDefaultThickness()
    {
        var behavior = new DtwProjectionBehavior();
        var created = behavior.CreateObject(new ChartPoint(new System.DateTime(2025, 1, 1), 100m));

        Assert.Equal(DrawingThemeContext.DefaultStrokeThickness, created.Thickness);
    }
}
