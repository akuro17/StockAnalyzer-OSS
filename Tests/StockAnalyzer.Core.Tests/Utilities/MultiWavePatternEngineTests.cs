using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Tests.Utilities;

public class MultiWavePatternEngineTests
{
    private static CoreCandleData CreateBlock(bool isBullish, decimal low, decimal high)
    {
        return new CoreCandleData(
            Timestamp: DateTime.UtcNow,
            Open: isBullish ? low : high,
            High: high,
            Low: low,
            Close: isBullish ? high : low,
            Volume: 100
        );
    }

    [Fact]
    public void ProcessNextBlock_MultipleBlocks_ExtractsWavesCorrectly()
    {
        var blocks = new List<CoreCandleData>
        {
            CreateBlock(true, 10, 11),
            CreateBlock(true, 11, 12),
            CreateBlock(true, 12, 13), // Wave 1: Bullish 10 to 13
            // W1 length = 3 blocks. High 13, Low 10. index 0 to 2

            CreateBlock(false, 12, 13), 
            CreateBlock(false, 11, 12), // Wave 2: Bearish 13 to 11
            // W2 length = 2 blocks. High 13, Low 11. index 3 to 4

            CreateBlock(true, 11, 12),
            CreateBlock(true, 12, 13), // Wave 3: Bullish 11 to 13
            // W3 length = 2 blocks. High 13, Low 11. index 5 to 6
        };

        var engine = new MultiWavePatternEngine();
        foreach (var block in blocks)
        {
            engine.ProcessNextBlock(block);
        }

        var waves = engine.Waves;

        Assert.Equal(3, waves.Count);
        
        Assert.True(waves[0].IsBullish);
        Assert.Equal(10, waves[0].Low);
        Assert.Equal(13, waves[0].High);
        Assert.Equal(0, waves[0].StartIndex);
        Assert.Equal(2, waves[0].EndIndex);
        Assert.Equal(3, waves[0].BlockCount);
        Assert.Equal(1.0, waves[0].PurityScore);
        Assert.Equal(3.0, waves[0].MomentumScore);

        Assert.False(waves[1].IsBullish);
        Assert.Equal(11, waves[1].Low);
        Assert.Equal(13, waves[1].High);
        Assert.Equal(3, waves[1].StartIndex);
        Assert.Equal(4, waves[1].EndIndex);
        Assert.Equal(2, waves[1].BlockCount);
        Assert.Equal(1.0, waves[1].PurityScore);
        Assert.Equal(2.0, waves[1].MomentumScore);

        Assert.True(waves[2].IsBullish);
        Assert.Equal(11, waves[2].Low);
        Assert.Equal(13, waves[2].High);
        Assert.Equal(5, waves[2].StartIndex);
        Assert.Equal(6, waves[2].EndIndex);
        Assert.Equal(2, waves[2].BlockCount);
        Assert.Equal(1.0, waves[2].PurityScore);
        Assert.Equal(2.0, waves[2].MomentumScore);
    }

    [Fact]
    public void ProcessNextBlock_DoubleTopBreakout_IsDetected()
    {
        var engine = new MultiWavePatternEngine();

        // W1 Up (index 0): 10 to 20
        engine.ProcessNextBlock(CreateBlock(true, 10, 20));  
        // W2 Down (index 1): 20 to 15
        engine.ProcessNextBlock(CreateBlock(false, 15, 20)); 
        
        // W3 Up (index 2): 15 to 19 (forming, no breakout yet)
        engine.ProcessNextBlock(CreateBlock(true, 15, 19));  
        Assert.Empty(engine.Signals);

        // W3 Up continues: hits 21! Breakout occurs immediately
        engine.ProcessNextBlock(CreateBlock(true, 19, 21));  
        
        var signals = engine.Signals;

        Assert.Single(signals);
        Assert.Equal(MultiWavePatternType.DoubleTopBreakout, signals[0].PatternType);
        Assert.Equal(3, signals[0].TriggerIndex); // The 4th block processed is index 3
        Assert.Equal(20m, signals[0].TriggerPrice);
        Assert.Equal(MultiWavePhase.Breakout, engine.CurrentPhase);
        
        Assert.Equal(1.0, signals[0].ConfidenceScore);
        Assert.Equal(15m, signals[0].InvalidationPrice); // W2.Low
        Assert.Equal(0, signals[0].StartWaveIndex);
        Assert.Equal(2, signals[0].EndWaveIndex);
    }

    [Fact]
    public void ProcessNextBlock_BullishCatapult_IsDetected()
    {
        var engine = new MultiWavePatternEngine();
        
        // W1(Up): 10 to 20
        engine.ProcessNextBlock(CreateBlock(true, 10, 20)); // idx 0
        // W2(Down): 20 to 15
        engine.ProcessNextBlock(CreateBlock(false, 15, 20)); // idx 1
        // W3(Up): 15 to 20
        engine.ProcessNextBlock(CreateBlock(true, 15, 20)); // idx 2
        // W4(Down): 20 to 15
        engine.ProcessNextBlock(CreateBlock(false, 15, 20)); // idx 3
        
        // W5(Up): 15 to 19
        engine.ProcessNextBlock(CreateBlock(true, 15, 19)); // idx 4
        // W5(Up): 19 to 21 -> Triple Top Breakout
        engine.ProcessNextBlock(CreateBlock(true, 19, 21)); // idx 5

        Assert.Single(engine.Signals);
        Assert.Equal(MultiWavePatternType.TripleTopBreakout, engine.Signals[0].PatternType);
        Assert.Equal(MultiWavePhase.Breakout, engine.CurrentPhase); // Current phase Breakout
        
        // W6(Down): 21 to 18 (Pullback)
        engine.ProcessNextBlock(CreateBlock(false, 18, 21)); // reversal triggered -> W6 begins
        Assert.Equal(MultiWavePhase.Pullback, engine.CurrentPhase); // Reversal after a signal within 5 blocks

        // W7(Up): 18 to 20
        engine.ProcessNextBlock(CreateBlock(true, 18, 20)); // reversal triggered -> W7 begins
        Assert.Equal(MultiWavePhase.Pullback, engine.CurrentPhase); 
        
        // W7(Up): 20 to 22 -> Catapult!
        engine.ProcessNextBlock(CreateBlock(true, 20, 22));

        var signals = engine.Signals;
        Assert.Equal(2, signals.Count);

        var catapult = signals.Last();
        Assert.Equal(MultiWavePatternType.BullishCatapult, catapult.PatternType);
        Assert.Equal(21m, catapult.TriggerPrice); // prev2.High is W5's High (21)
        Assert.Equal(MultiWavePhase.Expansion, engine.CurrentPhase);
        
        Assert.Equal(1.0, catapult.ConfidenceScore);
        Assert.Equal(18m, catapult.InvalidationPrice); // W6.Low
        Assert.Equal(0, catapult.StartWaveIndex);
        Assert.Equal(6, catapult.EndWaveIndex);
    }

    [Fact]
    public void ProcessNextBlock_WithNoise_CalculatesPurityCorrectly()
    {
        var engine = new MultiWavePatternEngine();
        
        // Block 1: Up, dist 1
        engine.ProcessNextBlock(CreateBlock(true, 10, 11)); 
        
        // Block 2: Up, but with heavy noise (long lower wick to 8)
        engine.ProcessNextBlock(new CoreCandleData(DateTime.UtcNow, 11, 12, 8, 12, 100)); 
        
        // Distance of Block 2 = 12 - 8 = 4. Total Distance = 1 + 4 = 5.
        // Overall wave High = 12, Low = 8. Progress = 12 - 8 = 4.
        // Purity = 4 / 5 = 0.8
        
        Assert.Single(engine.Waves);
        Assert.Equal(2, engine.Waves[0].BlockCount);
        Assert.Equal(0.8, engine.Waves[0].PurityScore);
        Assert.Equal(4.0, engine.Waves[0].MomentumScore);
    }

    [Fact]
    public void ProcessNextBlock_DoubleBottomBreakout_IsDetected()
    {
        var engine = new MultiWavePatternEngine();

        // W1 Down (index 0): 20 down to 10
        engine.ProcessNextBlock(CreateBlock(false, 10, 20));  
        // W2 Up (index 1): 10 up to 15
        engine.ProcessNextBlock(CreateBlock(true, 10, 15)); 
        
        // W3 Down (index 2): 15 down to 11 (forming, no breakout yet)
        engine.ProcessNextBlock(CreateBlock(false, 11, 15));  
        Assert.Empty(engine.Signals);

        // W3 Down continues: hits 9! Breakout occurs immediately
        engine.ProcessNextBlock(CreateBlock(false, 9, 11));  
        
        var signals = engine.Signals;

        Assert.Single(signals);
        Assert.Equal(MultiWavePatternType.DoubleBottomBreakout, signals[0].PatternType);
        Assert.Equal(MultiWavePhase.Breakout, engine.CurrentPhase);
        
        Assert.Equal(1.0, signals[0].ConfidenceScore);
        Assert.Equal(15m, signals[0].InvalidationPrice); // W2.High (Resistance)
        Assert.Equal(0, signals[0].StartWaveIndex);
        Assert.Equal(2, signals[0].EndWaveIndex);
    }
}
