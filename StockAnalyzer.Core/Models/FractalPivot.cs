using System;

namespace StockAnalyzer.Core.Models;

public readonly record struct FractalPivot
{
    public FractalPivotType Type { get; init; }
    public int Index { get; init; }
    public decimal Price { get; init; }
    public DateTime Timestamp { get; init; }
}
