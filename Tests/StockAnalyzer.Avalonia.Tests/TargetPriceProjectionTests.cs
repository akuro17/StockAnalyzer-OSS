using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests;

/// <summary>
/// Tests for Target Price Projection calculation logic (all 3 modes).
/// </summary>
public class TargetPriceProjectionTests
{
    private static ChartPoint MakePoint(decimal price) =>
        new ChartPoint(new DateTime(2025, 1, 1), price);

    [Fact]
    public void CalculateTargetPrices_ReturnsAllFive_WhenAllEnabled()
    {
        // P1=100, P2=200, P3=150
        var obj = new TargetPriceProjectionObject(
            MakePoint(100m), MakePoint(200m), MakePoint(150m));

        var results = obj.CalculateTargetPrices();

        Assert.Equal(5, results.Count);

        // V: P2 + (P2 - P3) = 200 + (200 - 150) = 250
        Assert.Contains(results, r => r.Name == "V" && r.Price == 250m);

        // N: P3 + (P2 - P1) = 150 + (200 - 100) = 250
        Assert.Contains(results, r => r.Name == "N" && r.Price == 250m);

        // %: P3 * (P2 / P1) = 150 * (200 / 100) = 300
        Assert.Contains(results, r => r.Name == "%" && r.Price == 300m);

        // E: P2 + (P2 - P1) = 200 + (200 - 100) = 300
        Assert.Contains(results, r => r.Name == "E" && r.Price == 300m);

        // NT: P3 + (P3 - P1) = 150 + (150 - 100) = 200
        Assert.Contains(results, r => r.Name == "NT" && r.Price == 200m);
    }

    [Fact]
    public void CalculateTargetPrices_RespectsToggleProperties()
    {
        var obj = new TargetPriceProjectionObject(
            MakePoint(100m), MakePoint(200m), MakePoint(150m));
        
        obj.ShowEqualDistance = false;
        obj.ShowEqualPercentage = true;
        obj.ShowDoubleReturn = false;
        obj.ShowEProjection = false;
        obj.ShowNTProjection = false;

        var results = obj.CalculateTargetPrices();

        Assert.Single(results);
        Assert.Equal("%", results[0].Name);
        Assert.Equal(300m, results[0].Price);
    }

    [Fact]
    public void CalculateTargetPrices_EqualPercentage_Omitted_WhenP1IsZero()
    {
        var obj = new TargetPriceProjectionObject(
            MakePoint(0m), MakePoint(150m), MakePoint(120m));

        var results = obj.CalculateTargetPrices();

        // Should return V, N, E, and NT, but not %
        Assert.Equal(4, results.Count);
        Assert.DoesNotContain(results, r => r.Name == "%");
    }

    [Fact]
    public void CalculateTargetPrices_DoubleReturn_CalculatesCorrectly_BearishCase()
    {
        // P1=200 (swing high), P2=100 (swing low), P3=150
        // V Target = P2 + (P2 - P3) = 100 + (100 - 150) = 50
        var obj = new TargetPriceProjectionObject(
            MakePoint(200m), MakePoint(100m), MakePoint(150m));

        var results = obj.CalculateTargetPrices();

        Assert.Contains(results, r => r.Name == "V" && r.Price == 50m);
    }

    [Fact]
    public void Translate_MovesAllPoints()
    {
        var obj = new TargetPriceProjectionObject(
            MakePoint(100m), MakePoint(200m), MakePoint(150m));
        var delta = TimeSpan.FromDays(1);
        decimal priceDelta = 10m;

        obj.Translate(delta, priceDelta);

        Assert.Equal(110m, obj.Points[0].Price);
        Assert.Equal(210m, obj.Points[1].Price);
        Assert.Equal(160m, obj.Points[2].Price);
    }

    [Fact]
    public void CalculateTargetPrices_InsufficientPoints_ReturnsEmpty()
    {
        var obj = new TargetPriceProjectionObject(
            MakePoint(100m), MakePoint(200m), MakePoint(150m));

        // Remove point to simulate insufficient state (only 2 points left)
        obj.Points.RemoveAt(2);

        var results = obj.CalculateTargetPrices();
        
        // With 2 points, V is no longer calculated
        Assert.Empty(results);
        
        // With 1 point
        obj.Points.RemoveAt(1);
        results = obj.CalculateTargetPrices();
        Assert.Empty(results);
    }
}
