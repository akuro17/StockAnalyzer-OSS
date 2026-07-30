namespace StockAnalyzer.Core.Models;

/// <summary>
/// Represents a simple 2D point with Double precision.
/// UI-agnostic replacement for Avalonia.Point or System.Drawing.Point.
/// </summary>
public readonly record struct Point(double X, double Y);
