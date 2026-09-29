using System;
using System.Collections.Generic;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Analysis;

namespace StockAnalyzer.Tests.Analysis;

public class PivotDetectionEngineTests
{
    private CandleData CreateCandle(decimal o, decimal h, decimal l, decimal c, int index)
    {
        return new CandleData
        {
            Timestamp = new DateTime(2025, 1, 1).AddDays(index),
            Open = o,
            High = h,
            Low = l,
            Close = c,
            Volume = 1000
        };
    }

    [Fact]
    public void ExtractPivots_ShouldExtractCorrectSwingHighAndLow_WithZeroAllocationLogic()
    {
        // Arrange
        var candles = new List<CandleData>
        {
            CreateCandle(10, 15, 5, 12, 0),
            CreateCandle(12, 18, 10, 15, 1),
            CreateCandle(15, 25, 12, 20, 2), // Swing High (25)
            CreateCandle(20, 20, 15, 18, 3),
            CreateCandle(18, 17, 8, 10, 4),  // Swing Low (8)
            CreateCandle(10, 12, 10, 11, 5),
            CreateCandle(11, 14, 11, 13, 6)
        };

        // Act
        var pivots = new List<FractalPivot>();
        PivotDetectionEngine.ExtractPivots(candles, 2, 2, pivots);

        // Assert
        Assert.Equal(2, pivots.Count);
        
        Assert.Equal(FractalPivotType.High, pivots[0].Type);
        Assert.Equal(2, pivots[0].Index);
        Assert.Equal(25m, pivots[0].Price);

        Assert.Equal(FractalPivotType.Low, pivots[1].Type);
        Assert.Equal(4, pivots[1].Index);
        Assert.Equal(8m, pivots[1].Price);
    }
    
    [Fact]
    public void GenerateSequentialCandidates_ShouldPairCorrectly_WithoutLINQ()
    {
        // Arrange
        var pivots = new List<FractalPivot>
        {
            new FractalPivot { Type = FractalPivotType.High, Index = 2, Price = 25m, Timestamp = DateTime.UnixEpoch },
            new FractalPivot { Type = FractalPivotType.Low, Index = 4, Price = 8m, Timestamp = DateTime.UnixEpoch },
            new FractalPivot { Type = FractalPivotType.High, Index = 7, Price = 30m, Timestamp = DateTime.UnixEpoch },
            new FractalPivot { Type = FractalPivotType.Low, Index = 9, Price = 10m, Timestamp = DateTime.UnixEpoch }
        };

        // Act
        var candidates = new List<TrendlineCandidate>();
        PivotPipeline.GenerateSequentialCandidates(pivots, candidates);

        // Assert
        Assert.Equal(2, candidates.Count);
        
        // High to High Candidate (Resistance)
        Assert.Equal(FractalPivotType.High, candidates[0].Type);
        Assert.Equal(2, candidates[0].StartPoint.Index);
        Assert.Equal(7, candidates[0].EndPoint.Index);
        
        // Low to Low Candidate (Support)
        Assert.Equal(FractalPivotType.Low, candidates[1].Type);
        Assert.Equal(4, candidates[1].StartPoint.Index);
        Assert.Equal(9, candidates[1].EndPoint.Index);
    }
}
