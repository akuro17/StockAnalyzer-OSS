using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class DrawingToolCategoryTests
{
    [Fact]
    public void GetCategories_LinesCategory_ContainsCatenaryCurve()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var linesCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Lines");

        Assert.NotNull(linesCategory);
        var toolInfo = linesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.CatenaryCurve);

        Assert.NotNull(toolInfo);
        Assert.Equal("\u2312", toolInfo.Icon);
        Assert.Equal("DrawTool_CatenaryCurve", toolInfo.NameKey);
    }

    [Fact]
    public void BehaviorRegistry_HasCatenaryCurveBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.CatenaryCurve);
        Assert.NotNull(behavior);
        Assert.Equal(3, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void GetCategories_LinesCategory_ContainsPolyline()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var linesCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Lines");

        Assert.NotNull(linesCategory);
        var toolInfo = linesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.Polyline);

        Assert.NotNull(toolInfo);
        Assert.Equal("\u29E2", toolInfo.Icon);
        Assert.Equal("DrawTool_Polyline", toolInfo.NameKey);
    }

    [Fact]
    public void BehaviorRegistry_HasPolylineBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.Polyline);
        Assert.NotNull(behavior);
        Assert.Equal(0, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void GetCategories_LinesCategory_ContainsNurbsTrendCurve()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var linesCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Lines");

        Assert.NotNull(linesCategory);
        var toolInfo = linesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.NurbsTrendCurve);

        Assert.NotNull(toolInfo);
        Assert.Equal("\u223F", toolInfo.Icon);
        Assert.Equal("DrawTool_NurbsTrendCurve", toolInfo.NameKey);
    }

    [Fact]
    public void BehaviorRegistry_HasNurbsTrendCurveBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.NurbsTrendCurve);
        Assert.NotNull(behavior);
        Assert.Equal(0, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }

    [Fact]
    public void GetCategories_ShapesCategory_ContainsNurbsConic()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var shapesCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Shapes");

        Assert.NotNull(shapesCategory);
        var toolInfo = shapesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.NurbsConic);

        Assert.NotNull(toolInfo);
        Assert.Equal("\u25CE", toolInfo.Icon);
        Assert.Equal("DrawTool_NurbsConic", toolInfo.NameKey);
    }

    [Fact]
    public void BehaviorRegistry_HasNurbsConicBehavior()
    {
        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.NurbsConic);
        Assert.NotNull(behavior);
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.True(behavior.FinishesOnRelease);
    }

    [Fact]
    public void GetCategories_And_Registry_DedicatedConicTools()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var linesCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Lines");
        var shapesCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Shapes");

        Assert.NotNull(linesCategory);
        Assert.NotNull(shapesCategory);

        var parabolaInfo = linesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.NurbsParabola);
        Assert.NotNull(parabolaInfo);
        Assert.Equal("DrawTool_NurbsParabola", parabolaInfo.NameKey);

        var hyperbolaInfo = linesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.NurbsHyperbola);
        Assert.NotNull(hyperbolaInfo);
        Assert.Equal("DrawTool_NurbsHyperbola", hyperbolaInfo.NameKey);

        var ellipseInfo = shapesCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.NurbsEllipse);
        Assert.NotNull(ellipseInfo);
        Assert.Equal("DrawTool_NurbsEllipse", ellipseInfo.NameKey);

        var parabolaBehavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.NurbsParabola);
        Assert.NotNull(parabolaBehavior);
        Assert.Equal(3, parabolaBehavior.RequiredSteps);

        var hyperbolaBehavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.NurbsHyperbola);
        Assert.NotNull(hyperbolaBehavior);
        Assert.Equal(3, hyperbolaBehavior.RequiredSteps);

        var ellipseBehavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.NurbsEllipse);
        Assert.NotNull(ellipseBehavior);
        Assert.Equal(2, ellipseBehavior.RequiredSteps);
    }

    [Fact]
    public void GetCategories_And_Registry_RangeSplineTool()
    {
        var categories = DrawingToolCategoryService.GetCategories();
        var channelsCategory = categories.FirstOrDefault(c => c.NameKey == "DrawCat_Channels");

        Assert.NotNull(channelsCategory);
        var toolInfo = channelsCategory.Tools.FirstOrDefault(t => t.Tool == DrawingTool.RangeSpline);

        Assert.NotNull(toolInfo);
        Assert.Equal("\u223D", toolInfo.Icon);
        Assert.Equal("DrawTool_RangeSpline", toolInfo.NameKey);

        var behavior = DrawingToolBehaviorRegistry.GetBehavior(DrawingTool.RangeSpline);
        Assert.NotNull(behavior);
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);
    }
}
