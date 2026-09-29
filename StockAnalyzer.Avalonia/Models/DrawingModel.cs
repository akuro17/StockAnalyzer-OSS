using Avalonia;
using System;

namespace StockAnalyzer.Avalonia.Models;

public enum DrawingType
{
    None,
    TrendLine,
    FibonacciRetracement
}

public class DrawingItem
{
    public DrawingType Type { get; set; }
    public global::Avalonia.Point StartPoint { get; set; }
    public global::Avalonia.Point EndPoint { get; set; }
    public bool IsSelected { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString();
}
