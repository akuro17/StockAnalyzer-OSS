using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class NurbsBehaviorsTests
{
    [Fact]
    public void NurbsTrendCurveBehavior_StepRequirementsAndCreation()
    {
        var behavior = new NurbsTrendCurveBehavior();
        Assert.Equal(0, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<NurbsTrendCurveObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p0, obj.Points[1]);

        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 120m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p1, obj.Points[1]);
    }

    [Fact]
    public void NurbsConicBehavior_StepRequirementsAndCreation()
    {
        var behavior = new NurbsConicBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.True(behavior.FinishesOnRelease);

        var center = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(center);

        Assert.IsType<NurbsConicObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(center, obj.Points[0]);
        Assert.Equal(center, obj.Points[1]);

        var edge = new ChartPoint(new DateTime(2025, 1, 3), 150m);
        behavior.UpdatePoint(obj, 1, edge);
        Assert.Equal(center, obj.Points[0]);
        Assert.Equal(edge, obj.Points[1]);
    }

    [Fact]
    public void NurbsEllipseBehavior_StepRequirementsAndCreation()
    {
        var behavior = new NurbsEllipseBehavior();
        Assert.Equal(2, behavior.RequiredSteps);
        Assert.True(behavior.FinishesOnRelease);

        var center = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(center);

        Assert.IsType<NurbsEllipseObject>(obj);
        Assert.Equal(2, obj.Points.Count);
        Assert.Equal(center, obj.Points[0]);
        Assert.Equal(center, obj.Points[1]);

        var corner = new ChartPoint(new DateTime(2025, 1, 4), 180m);
        behavior.UpdatePoint(obj, 1, corner);
        Assert.Equal(center, obj.Points[0]);
        Assert.Equal(corner, obj.Points[1]);
    }

    [Fact]
    public void NurbsParabolaBehavior_StepRequirementsAndCreation()
    {
        var behavior = new NurbsParabolaBehavior();
        Assert.Equal(3, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<NurbsParabolaObject>(obj);
        Assert.Equal(3, obj.Points.Count);

        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 200m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p1, obj.Points[1]);
        Assert.Equal(p1, obj.Points[2]);

        var p2 = new ChartPoint(new DateTime(2025, 1, 3), 100m);
        behavior.UpdatePoint(obj, 2, p2);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
        Assert.Equal(p2, obj.Points[2]);
    }

    [Fact]
    public void NurbsHyperbolaBehavior_StepRequirementsAndCreation()
    {
        var behavior = new NurbsHyperbolaBehavior();
        Assert.Equal(3, behavior.RequiredSteps);
        Assert.False(behavior.FinishesOnRelease);

        var p0 = new ChartPoint(new DateTime(2025, 1, 1), 100m);
        var obj = behavior.CreateObject(p0);

        Assert.IsType<NurbsHyperbolaObject>(obj);
        Assert.Equal(3, obj.Points.Count);

        var p1 = new ChartPoint(new DateTime(2025, 1, 2), 200m);
        behavior.UpdatePoint(obj, 1, p1);
        Assert.Equal(p1, obj.Points[1]);
        Assert.Equal(p1, obj.Points[2]);

        var p2 = new ChartPoint(new DateTime(2025, 1, 3), 100m);
        behavior.UpdatePoint(obj, 2, p2);
        Assert.Equal(p0, obj.Points[0]);
        Assert.Equal(p1, obj.Points[1]);
        Assert.Equal(p2, obj.Points[2]);
    }
}
