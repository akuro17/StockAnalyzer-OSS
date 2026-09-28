using System;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// Represents a point in Chart Coordinates (Time, Price)
/// </summary>
public struct ChartPoint
{
    public DateTime Time { get; set; }
    public decimal Price { get; set; }

    public ChartPoint(DateTime time, decimal price)
    {
        Time = time;
        Price = price;
    }
}
