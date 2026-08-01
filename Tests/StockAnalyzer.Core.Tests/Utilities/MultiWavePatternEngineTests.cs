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

            CreateBlock(false, 12, 13), 
            CreateBlock(false, 11, 12), // Wave 2: Bearish 13 to 11

            CreateBlock(true, 11, 12),
            CreateBlock(true, 12, 13), // Wave 3: Bullish 11 to 13
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
        Assert.Equal(3, signals[0].TriggerIndex);
        Assert.Equal(20m, signals[0].TriggerPrice);
        Assert.Equal(MultiWavePhase.Breakout, engine.CurrentPhase);
        
        // [UPDATED] 3-factor score: must be in [0.0, 1.0]. Not 1.0 due to pullback depth.
        Assert.InRange(signals[0].ConfidenceScore, 0.0, 1.0);
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
        Assert.Equal(MultiWavePhase.Breakout, engine.CurrentPhase);
        
        // W6(Down): 21 to 18 (Pullback)
        engine.ProcessNextBlock(CreateBlock(false, 18, 21));
        Assert.Equal(MultiWavePhase.Pullback, engine.CurrentPhase);

        // W7(Up): 18 to 20
        engine.ProcessNextBlock(CreateBlock(true, 18, 20));
        Assert.Equal(MultiWavePhase.Pullback, engine.CurrentPhase); 
        
        // W7(Up): 20 to 22 -> Catapult!
        engine.ProcessNextBlock(CreateBlock(true, 20, 22));

        var signals = engine.Signals;
        Assert.Equal(2, signals.Count);

        var catapult = signals.Last();
        Assert.Equal(MultiWavePatternType.BullishCatapult, catapult.PatternType);
        Assert.Equal(21m, catapult.TriggerPrice);
        Assert.Equal(MultiWavePhase.Expansion, engine.CurrentPhase);
        
        // [UPDATED] 3-factor score: must be in [0.0, 1.0].
        Assert.InRange(catapult.ConfidenceScore, 0.0, 1.0);
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
        
        // [UPDATED] 3-factor score: must be in [0.0, 1.0].
        Assert.InRange(signals[0].ConfidenceScore, 0.0, 1.0);
        Assert.Equal(15m, signals[0].InvalidationPrice); // W2.High (Resistance)
        Assert.Equal(0, signals[0].StartWaveIndex);
        Assert.Equal(2, signals[0].EndWaveIndex);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NEW TESTS: Boundary Values, Range Invariants, Monotonicity
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfidenceScore_IsAlwaysInRange_ForDoubleTopSignal()
    {
        // Arrange: minimal valid Double Top
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(true, 10, 20));   // W1 Up
        engine.ProcessNextBlock(CreateBlock(false, 15, 20));  // W2 Down
        engine.ProcessNextBlock(CreateBlock(true, 15, 21));   // W3 Up -> breakout

        // Act & Assert
        Assert.Single(engine.Signals);
        double score = engine.Signals[0].ConfidenceScore;
        Assert.InRange(score, 0.0, 1.0);
    }

    [Fact]
    public void ConfidenceScore_WithZeroBoxSize_DoesNotThrow_AndIsInRange()
    {
        // Guard: boxSize <= 0 must be clamped to 1.0 internally (no DivideByZeroException)
        var engine = new MultiWavePatternEngine(boxSize: 0m);
        engine.ProcessNextBlock(CreateBlock(true, 10, 20));
        engine.ProcessNextBlock(CreateBlock(false, 15, 20));
        engine.ProcessNextBlock(CreateBlock(true, 15, 21));

        Assert.Single(engine.Signals);
        Assert.InRange(engine.Signals[0].ConfidenceScore, 0.0, 1.0);
    }

    [Fact]
    public void ConfidenceScore_Monotonicity_LargerImpulseYieldsHigherWaveStrength()
    {
        // Weak Double Top: single-block impulse waves
        var engineWeak = new MultiWavePatternEngine(boxSize: 1.0m);
        engineWeak.ProcessNextBlock(CreateBlock(true, 10, 20));   // W1: 1 block
        engineWeak.ProcessNextBlock(CreateBlock(false, 15, 20));  // W2: 1 block
        engineWeak.ProcessNextBlock(CreateBlock(true, 15, 21));   // W3: 1 block -> breakout

        // Strong Double Top: multi-block impulse waves
        var engineStrong = new MultiWavePatternEngine(boxSize: 1.0m);
        engineStrong.ProcessNextBlock(CreateBlock(true, 10, 13));   // W1 block 1
        engineStrong.ProcessNextBlock(CreateBlock(true, 13, 16));   // W1 block 2
        engineStrong.ProcessNextBlock(CreateBlock(true, 16, 20));   // W1 block 3
        engineStrong.ProcessNextBlock(CreateBlock(false, 15, 20));  // W2: 1 block
        engineStrong.ProcessNextBlock(CreateBlock(true, 15, 18));   // W3 block 1
        engineStrong.ProcessNextBlock(CreateBlock(true, 18, 21));   // W3 block 2 -> breakout

        Assert.Single(engineWeak.Signals);
        Assert.Single(engineStrong.Signals);

        double weakScore   = engineWeak.Signals[0].ConfidenceScore;
        double strongScore = engineStrong.Signals[0].ConfidenceScore;

        // Multi-block impulse should yield higher WaveStrength component
        Assert.True(strongScore > weakScore,
            $"Expected strongScore ({strongScore:F4}) > weakScore ({weakScore:F4})");
    }

    [Fact]
    public void ConfidenceScore_ShallowerPullback_YieldsHigherScore()
    {
        // Deep pullback (W2 retraces most of W1)
        var engineDeep = new MultiWavePatternEngine(boxSize: 1.0m);
        engineDeep.ProcessNextBlock(CreateBlock(true, 10, 20));   // W1: 10 range
        engineDeep.ProcessNextBlock(CreateBlock(false, 11, 20));  // W2: deep pullback (9 units)
        engineDeep.ProcessNextBlock(CreateBlock(true, 11, 21));   // W3 breakout

        // Shallow pullback (W2 barely retraces W1)
        var engineShallow = new MultiWavePatternEngine(boxSize: 1.0m);
        engineShallow.ProcessNextBlock(CreateBlock(true, 10, 20));  // W1: 10 range
        engineShallow.ProcessNextBlock(CreateBlock(false, 18, 20)); // W2: shallow pullback (2 units)
        engineShallow.ProcessNextBlock(CreateBlock(true, 18, 21));  // W3 breakout

        Assert.Single(engineDeep.Signals);
        Assert.Single(engineShallow.Signals);

        double deepScore    = engineDeep.Signals[0].ConfidenceScore;
        double shallowScore = engineShallow.Signals[0].ConfidenceScore;

        // Shallower pullback should produce a higher PullbackShallowness score
        Assert.True(shallowScore > deepScore,
            $"Expected shallowScore ({shallowScore:F4}) > deepScore ({deepScore:F4})");
    }

    [Fact]
    public void ScoreBreakdown_Fields_AreStoredInSignal()
    {
        // Verify that score decomposition fields are populated (not all zero)
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(true, 10, 20));
        engine.ProcessNextBlock(CreateBlock(false, 18, 20)); // shallow pullback
        engine.ProcessNextBlock(CreateBlock(true, 18, 21));

        Assert.Single(engine.Signals);
        var signal = engine.Signals[0];

        // WaveStrengthScore must be > 0 (impulse has at least 1 block)
        Assert.True(signal.WaveStrengthScore > 0.0,
            $"WaveStrengthScore should be > 0, got {signal.WaveStrengthScore}");

        // PullbackScore + BreakoutScore together must be > 0
        Assert.True(signal.PullbackScore >= 0.0);
        Assert.True(signal.BreakoutScore >= 0.0);

        // All sub-scores must be clamped to [0.0, 1.0]
        Assert.InRange(signal.WaveStrengthScore, 0.0, 1.0);
        Assert.InRange(signal.PullbackScore,     0.0, 1.0);
        Assert.InRange(signal.BreakoutScore,     0.0, 1.0);
    }

    [Fact]
    public void DoubleBottom_ConfidenceScore_IsInRangeAndDecomposed()
    {
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(false, 10, 20));   // W1 Down
        engine.ProcessNextBlock(CreateBlock(true, 10, 18));    // W2 Up (shallow recovery)
        engine.ProcessNextBlock(CreateBlock(false, 9, 18));    // W3 Down -> breakout below 10

        Assert.Single(engine.Signals);
        var signal = engine.Signals[0];
        Assert.Equal(MultiWavePatternType.DoubleBottomBreakout, signal.PatternType);
        Assert.InRange(signal.ConfidenceScore, 0.0, 1.0);
        Assert.InRange(signal.WaveStrengthScore, 0.0, 1.0);
        Assert.InRange(signal.PullbackScore,     0.0, 1.0);
        Assert.InRange(signal.BreakoutScore,     0.0, 1.0);
    }

    [Fact]
    public void TripleTopBreakout_ConfidenceScore_IsInRangeAndDecomposed()
    {
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(true, 10, 20));  // W1 Up
        engine.ProcessNextBlock(CreateBlock(false, 15, 20)); // W2 Down
        engine.ProcessNextBlock(CreateBlock(true, 15, 20));  // W3 Up
        engine.ProcessNextBlock(CreateBlock(false, 15, 20)); // W4 Down
        engine.ProcessNextBlock(CreateBlock(true, 15, 21));  // W5 Up -> Triple Top Breakout

        Assert.Single(engine.Signals);
        var signal = engine.Signals[0];
        Assert.Equal(MultiWavePatternType.TripleTopBreakout, signal.PatternType);
        Assert.InRange(signal.ConfidenceScore, 0.0, 1.0);
        Assert.InRange(signal.WaveStrengthScore, 0.0, 1.0);
        Assert.InRange(signal.PullbackScore, 0.0, 1.0);
        Assert.InRange(signal.BreakoutScore, 0.0, 1.0);
    }

    [Fact]
    public void TripleBottomBreakout_ConfidenceScore_IsInRangeAndDecomposed()
    {
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(false, 10, 20)); // W1 Down
        engine.ProcessNextBlock(CreateBlock(true, 10, 15));  // W2 Up
        engine.ProcessNextBlock(CreateBlock(false, 10, 15)); // W3 Down
        engine.ProcessNextBlock(CreateBlock(true, 10, 15));  // W4 Up
        engine.ProcessNextBlock(CreateBlock(false, 9, 15));  // W5 Down -> Triple Bottom Breakout

        Assert.Single(engine.Signals);
        var signal = engine.Signals[0];
        Assert.Equal(MultiWavePatternType.TripleBottomBreakout, signal.PatternType);
        Assert.InRange(signal.ConfidenceScore, 0.0, 1.0);
        Assert.InRange(signal.WaveStrengthScore, 0.0, 1.0);
        Assert.InRange(signal.PullbackScore, 0.0, 1.0);
        Assert.InRange(signal.BreakoutScore, 0.0, 1.0);
    }

    [Fact]
    public void BullishTriangleBreakout_ConfidenceScore_IsInRangeAndDecomposed()
    {
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(true, 10, 20));  // W1 Up (high 20)
        engine.ProcessNextBlock(CreateBlock(false, 12, 20)); // W2 Down (low 12)
        engine.ProcessNextBlock(CreateBlock(true, 12, 18));  // W3 Up (lower high 18)
        engine.ProcessNextBlock(CreateBlock(false, 14, 18)); // W4 Down (higher low 14)
        engine.ProcessNextBlock(CreateBlock(true, 14, 19));  // W5 Up -> Bullish Triangle Breakout (>18)

        Assert.Single(engine.Signals);
        var signal = engine.Signals[0];
        Assert.Equal(MultiWavePatternType.BullishTriangleBreakout, signal.PatternType);
        Assert.InRange(signal.ConfidenceScore, 0.0, 1.0);
        Assert.InRange(signal.WaveStrengthScore, 0.0, 1.0);
        Assert.InRange(signal.PullbackScore, 0.0, 1.0);
        Assert.InRange(signal.BreakoutScore, 0.0, 1.0);
    }

    [Fact]
    public void BearishTriangleBreakout_ConfidenceScore_IsInRangeAndDecomposed()
    {
        var engine = new MultiWavePatternEngine(boxSize: 1.0m);
        engine.ProcessNextBlock(CreateBlock(false, 10, 20)); // W1 Down (low 10)
        engine.ProcessNextBlock(CreateBlock(true, 10, 18));  // W2 Up (high 18)
        engine.ProcessNextBlock(CreateBlock(false, 12, 18)); // W3 Down (higher low 12)
        engine.ProcessNextBlock(CreateBlock(true, 12, 16));  // W4 Up (lower high 16)
        engine.ProcessNextBlock(CreateBlock(false, 11, 16)); // W5 Down -> Bearish Triangle Breakout (<12)

        Assert.Single(engine.Signals);
        var signal = engine.Signals[0];
        Assert.Equal(MultiWavePatternType.BearishTriangleBreakout, signal.PatternType);
        Assert.InRange(signal.ConfidenceScore, 0.0, 1.0);
        Assert.InRange(signal.WaveStrengthScore, 0.0, 1.0);
        Assert.InRange(signal.PullbackScore, 0.0, 1.0);
        Assert.InRange(signal.BreakoutScore, 0.0, 1.0);
    }
}
