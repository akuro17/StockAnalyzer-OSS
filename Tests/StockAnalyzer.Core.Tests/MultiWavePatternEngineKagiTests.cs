using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class MultiWavePatternEngineKagiTests
{
    [Fact]
    public void ProcessKagiSegments_ShouldProduceDoubleTopBreakout()
    {
        // Arrange: Create alternating bullish/bearish segments like Kagi converter produces
        // Pattern: Rising market with pullbacks
        var blocks = new List<CoreCandleData>
        {
            // Up leg 1: 100 -> 110
            new(DateTime.Now, 100m, 110m, 100m, 110m, 100),
            // Down leg 1: 110 -> 105
            new(DateTime.Now, 110m, 110m, 105m, 105m, 100),
            // Up leg 2: 105 -> 115 (breaks above wave 0's High of 110)
            new(DateTime.Now, 105m, 115m, 105m, 115m, 100),
        };

        // Act
        var engine = new MultiWavePatternEngine();
        foreach (var b in blocks) engine.ProcessNextBlock(b);

        // Assert
        Assert.Equal(3, engine.Waves.Count); // 3 alternating direction waves
        Assert.True(engine.Signals.Count > 0, $"Expected at least one signal, got {engine.Signals.Count}. Waves: {string.Join(", ", engine.Waves.Select(w => $"[{w.IsBullish}:{w.High}/{w.Low}]"))}");
        Assert.Equal(MultiWavePatternType.DoubleTopBreakout, engine.Signals[0].PatternType);
    }

    [Fact]
    public void ProcessKagiSegments_SignalsSurviveToList()
    {
        // Arrange: Verify the .ToList() fix works
        var blocks = new List<CoreCandleData>
        {
            new(DateTime.Now, 100m, 110m, 100m, 110m, 100),
            new(DateTime.Now, 110m, 110m, 105m, 105m, 100),
            new(DateTime.Now, 105m, 115m, 105m, 115m, 100),
        };

        // Act
        var engine = new MultiWavePatternEngine();
        foreach (var b in blocks) engine.ProcessNextBlock(b);
        var signals = engine.Signals.ToList(); // Materialize
        engine.Clear(); // This would clear the internal list

        // Assert
        Assert.True(signals.Count > 0, "Signals should survive after engine.Clear()");
    }
}
