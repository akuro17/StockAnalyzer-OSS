namespace StockAnalyzer.Core.Models.Portfolio;

public readonly record struct HeatmapEntry(
    string Ticker,
    string Region,
    string Sector,
    float Return,
    float Weight
);