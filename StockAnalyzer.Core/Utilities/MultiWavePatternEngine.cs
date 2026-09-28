using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

public enum MultiWavePhase
{
    Accumulation,
    Breakout,
    Pullback,
    Expansion
}

/// <summary>
/// Stateful engine to incrementally extract waves from blocks and detect higher-order patterns O(1).
/// Designed for real-time scanning without full recalculations on every tick.
/// </summary>
public class MultiWavePatternEngine
{
    private readonly decimal _tolerance;
    private readonly decimal _boxSize;
    private readonly List<WaveNode> _waves;
    private readonly List<MultiWaveSignal> _signals;

    // State Tracking
    private int _currentIndex;
    private bool _hasStarted;
    private bool _currentDirection;
    private int _startIndex;
    private decimal _currentHigh;
    private decimal _currentLow;
    private decimal _currentTotalDistance;
    
    // To prevent emitting the same signal multiple times during a growing wave
    private int _lastSignalWaveIndex = -1;

    public IReadOnlyList<WaveNode> Waves => _waves;
    public IReadOnlyList<MultiWaveSignal> Signals => _signals;
    public MultiWavePhase CurrentPhase { get; private set; }

    public MultiWavePatternEngine(decimal tolerance = 0.0001m, decimal boxSize = 1.0m)
    {
        _tolerance = tolerance;
        _boxSize = boxSize > 0m ? boxSize : 1.0m;
        _waves = new List<WaveNode>(128);
        _signals = new List<MultiWaveSignal>(32);
        _currentIndex = -1;
        _hasStarted = false;
        CurrentPhase = MultiWavePhase.Accumulation;
    }

    /// <summary>
    /// Resets the engine state for reuse, enabling Zero-Allocation processing.
    /// </summary>
    public void Clear()
    {
        _waves.Clear();
        _signals.Clear();
        _currentIndex = -1;
        _hasStarted = false;
        _lastSignalWaveIndex = -1;
        CurrentPhase = MultiWavePhase.Accumulation;
    }

    public void ProcessNextBlock(CoreCandleData block)
    {
        _currentIndex++;
        decimal blockDistance = System.Math.Abs(block.High - block.Low);

        if (!_hasStarted)
        {
            _hasStarted = true;
            _currentDirection = block.IsBullish;
            _startIndex = _currentIndex;
            _currentHigh = block.High;
            _currentLow = block.Low;
            _currentTotalDistance = blockDistance;

            _waves.Add(new WaveNode(_currentDirection, _startIndex, _currentIndex, _currentHigh, _currentLow, 1, 1.0, (double)blockDistance));
            return;
        }

        if (block.IsBullish == _currentDirection)
        {
            _currentTotalDistance += blockDistance;
            // Continue current wave
            if (block.High > _currentHigh) _currentHigh = block.High;
            if (block.Low < _currentLow) _currentLow = block.Low;

            int blockCount = _currentIndex - _startIndex + 1;
            decimal progress = System.Math.Abs(_currentHigh - _currentLow);
            double purity = _currentTotalDistance == 0 ? 1.0 : (double)(progress / _currentTotalDistance);
            if (progress == 0) purity = 1.0;
            double momentum = (double)progress;

            // Optimistic update of the active wave
            _waves[_waves.Count - 1] = new WaveNode(_currentDirection, _startIndex, _currentIndex, _currentHigh, _currentLow, blockCount, purity, momentum);
            
            // Check if this wave update triggers any patterns
            CheckForPatternsOnActiveWave();
        }
        else
        {
            // Direction reversal -> The previous wave is locked in.
            _currentDirection = block.IsBullish;
            _startIndex = _currentIndex;
            _currentHigh = block.High;
            _currentLow = block.Low;
            _currentTotalDistance = blockDistance;
            
            _waves.Add(new WaveNode(_currentDirection, _startIndex, _currentIndex, _currentHigh, _currentLow, 1, 1.0, (double)blockDistance));
            
            // Determine phase transition:
            // A reversal often means moving from Accumulation -> Pullback, or Breakout -> Pullback
            if (_signals.Count > 0 && _signals[_signals.Count - 1].TriggerIndex >= _startIndex - 5)
            {
                CurrentPhase = MultiWavePhase.Pullback;
            }
            else
            {
                CurrentPhase = MultiWavePhase.Accumulation;
            }
            
            // Check for patterns immediately on the new unit 1-block wave
            CheckForPatternsOnActiveWave();
        }

        // --- Phase 2: Core Algorithm Optimization (Invalidation Check) ---
        // Incrementally check all non-invalidated signals against current block price.
        // This moves O(N_historic) logic from the renderer to the O(1) engine per block.
        for (int i = 0; i < _signals.Count; i++)
        {
            var signal = _signals[i];
            if (signal.IsInvalidated) continue;

            // Do not invalidate in the same block as the trigger (unless truly immediate, 
            // but usually we check triggerIndex + 1 onwards)
            if (_currentIndex <= signal.TriggerIndex) continue;

            bool invalidated = false;
            if (signal.IsBullish)
            {
                if (block.Close < signal.InvalidationPrice) invalidated = true;
            }
            else
            {
                if (block.Close > signal.InvalidationPrice) invalidated = true;
            }

            if (invalidated)
            {
                _signals[i] = signal with { IsInvalidated = true, InvalidationIndex = _currentIndex };
            }
        }
    }

    /// <summary>
    /// Computes WaveStrength: normalized impulse block count relative to a reference cap.
    /// Zero-Allocation. Guard: blockCount=0 → 0.0.
    /// </summary>
    private static double ComputeWaveStrength(int impulseBlockCount, int referenceBlockCount)
    {
        if (referenceBlockCount <= 0) return 0.0;
        return System.Math.Clamp((double)impulseBlockCount / referenceBlockCount, 0.0, 1.0);
    }

    /// <summary>
    /// Computes PullbackShallowness: how shallow the pullback is relative to the impulse.
    /// Zero-Allocation. Guard: impulseRange=0 → 1.0 (ideal no-pullback state).
    /// </summary>
    private static double ComputePullbackShallowness(decimal impulseRange, decimal pullbackRange)
    {
        if (impulseRange <= 0m) return 1.0;
        double ratio = (double)(pullbackRange / impulseRange);
        return System.Math.Clamp(1.0 - ratio, 0.0, 1.0);
    }

    /// <summary>
    /// Computes BreakoutWidth: breakout distance as a ratio of the impulse range.
    /// Zero-Allocation. Guard: impulseRange=0 → 0.0.
    /// Note: boxSize was removed because (breakoutDist/boxSize)/(impulseRange/boxSize)
    ///       algebraically simplifies to breakoutDist/impulseRange, making boxSize a dead parameter.
    /// </summary>
    private static double ComputeBreakoutWidth(decimal breakoutDistance, decimal impulseRange)
    {
        if (impulseRange <= 0m) return 0.0;
        return System.Math.Clamp((double)(breakoutDistance / impulseRange), 0.0, 1.0);
    }

    private static class Weights
    {
        public const int DoublePatternRefBlocks = 6;
        public const int TriplePatternRefBlocks = 9;

        // Double Top/Bottom weights: W=(0.40, 0.40, 0.20)
        public const double DoubleWs = 0.40;
        public const double DoublePs = 0.40;
        public const double DoubleBw = 0.20;

        // Triple Top/Bottom weights: W=(0.35, 0.35, 0.30)
        public const double TripleWs = 0.35;
        public const double TriplePs = 0.35;
        public const double TripleBw = 0.30;

        // Triangle weights: W=(0.30, 0.40, 0.30)
        public const double TriangleWs = 0.30;
        public const double TrianglePs = 0.40;
        public const double TriangleBw = 0.30;

        // Catapult weights: W=(0.35, 0.30, 0.35)
        public const double CatapultWs = 0.35;
        public const double CatapultPs = 0.30;
        public const double CatapultBw = 0.35;
    }

    private void CheckForPatternsOnActiveWave()
    {
        if (_waves.Count < 3) return;

        int i = _waves.Count - 1;
        
        // Ensure we don't emit multiple signals for the exact same wave (no duplicate breakouts)
        if (_lastSignalWaveIndex == i) return;

        var wave = _waves[i];
        var prev2 = _waves[i - 2];
        
        if (wave.IsBullish)
        {
            if (wave.High > prev2.High)
            {
                var prev1 = _waves[i - 1]; // needed for InvalidationPrice!
                bool isTripleTop = false;
                if (i >= 4)
                {
                    var prev3 = _waves[i - 3];
                    var prev4 = _waves[i - 4];
                    if (System.Math.Abs(prev2.High - prev4.High) < _tolerance)
                    {
                        isTripleTop = true;
                        // TripleTop weights: W=(0.35, 0.35, 0.30) — strength and pullback equally valued, breakout matters
                        decimal impulseRange = prev2.High - System.Math.Min(prev1.Low, prev3.Low);
                        decimal pullbackRange = System.Math.Max(prev1.High - prev1.Low, prev3.High - prev3.Low);
                        decimal breakoutDist  = wave.High - prev2.High;
                        double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount + prev4.BlockCount, Weights.TriplePatternRefBlocks);
                        double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                        double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                        double confidence = System.Math.Clamp(Weights.TripleWs * ws + Weights.TriplePs * ps + Weights.TripleBw * bw, 0.0, 1.0);
                        decimal invalidation = System.Math.Min(prev1.Low, prev3.Low);
                        decimal height = prev2.High - invalidation;
                        string patternName = FormatPatternName(MultiWavePatternType.TripleTopBreakout);
                        string priceRange = $"Target: {prev2.High + height * 1.0m:F2} - {prev2.High + height * 1.618m:F2}";
                        _signals.Add(new MultiWaveSignal(MultiWavePatternType.TripleTopBreakout, true, _currentIndex, prev2.High, prev2.High, confidence, invalidation, i - 4, i, prev2.High + height * 1.0m, prev2.High + height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                        _lastSignalWaveIndex = i;
                        CurrentPhase = MultiWavePhase.Breakout;
                    }

                    if (!isTripleTop && prev2.High < prev4.High && prev1.Low > prev3.Low)
                    {
                        // BullishTriangle weights: W=(0.30, 0.40, 0.30) — pullback shallowness is critical for converging triangle
                        decimal impulseRange = prev4.High - System.Math.Min(prev3.Low, prev1.Low);
                        decimal pullbackRange = prev1.High - prev1.Low;
                        decimal breakoutDist  = wave.High - prev2.High;
                        double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount, Weights.DoublePatternRefBlocks);
                        double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                        double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                        double confidence = System.Math.Clamp(Weights.TriangleWs * ws + Weights.TrianglePs * ps + Weights.TriangleBw * bw, 0.0, 1.0);
                        decimal invalidation = prev1.Low; // Triangle apex is tight
                        decimal height = prev2.High - invalidation;
                        string patternName = FormatPatternName(MultiWavePatternType.BullishTriangleBreakout);
                        string priceRange = $"Target: {prev2.High + height * 1.0m:F2} - {prev2.High + height * 1.618m:F2}";
                        _signals.Add(new MultiWaveSignal(MultiWavePatternType.BullishTriangleBreakout, true, _currentIndex, prev2.High, prev2.High, confidence, invalidation, i - 4, i, prev2.High + height * 1.0m, prev2.High + height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                        _lastSignalWaveIndex = i;
                        CurrentPhase = MultiWavePhase.Breakout;
                    }
                }

                if (_lastSignalWaveIndex != i) 
                {
                    bool isCatapult = false;
                    if (i >= 6)
                    {
                        var prev4 = _waves[i - 4];
                        var prev6 = _waves[i - 6];
                        if (prev2.High > prev4.High && System.Math.Abs(prev4.High - prev6.High) < _tolerance)
                        {
                            isCatapult = true;
                            // BullishCatapult weights: W=(0.35, 0.30, 0.35) — breakout power + wave strength equally dominant
                            decimal impulseRange = wave.High - prev1.Low;
                            decimal pullbackRange = prev1.High - prev1.Low;
                            decimal breakoutDist  = wave.High - prev2.High;
                            double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount + prev4.BlockCount, Weights.TriplePatternRefBlocks);
                            double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                            double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                            double confidence = System.Math.Clamp(Weights.CatapultWs * ws + Weights.CatapultPs * ps + Weights.CatapultBw * bw, 0.0, 1.0);
                            decimal invalidation = prev1.Low; // the tiny pullback is the invalidation point
                            decimal height = prev2.High - invalidation;
                            string patternName = FormatPatternName(MultiWavePatternType.BullishCatapult);
                            string priceRange = $"Target: {prev2.High + height * 1.0m:F2} - {prev2.High + height * 1.618m:F2}";
                            _signals.Add(new MultiWaveSignal(MultiWavePatternType.BullishCatapult, true, _currentIndex, prev2.High, prev2.High, confidence, invalidation, i - 6, i, prev2.High + height * 1.0m, prev2.High + height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                            _lastSignalWaveIndex = i;
                            CurrentPhase = MultiWavePhase.Expansion;
                        }
                    }

                    if (!isCatapult)
                    {
                        // DoubleTop weights: W=(0.40, 0.40, 0.20) — strength and pullback depth are equally key
                        decimal impulseRange = prev2.High - prev1.Low;
                        decimal pullbackRange = prev1.High - prev1.Low;
                        decimal breakoutDist  = wave.High - prev2.High;
                        double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount, Weights.DoublePatternRefBlocks);
                        double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                        double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                        double confidence = System.Math.Clamp(Weights.DoubleWs * ws + Weights.DoublePs * ps + Weights.DoubleBw * bw, 0.0, 1.0);
                        decimal invalidation = prev1.Low;
                        decimal height = prev2.High - invalidation;
                        string patternName = FormatPatternName(MultiWavePatternType.DoubleTopBreakout);
                        string priceRange = $"Target: {prev2.High + height * 1.0m:F2} - {prev2.High + height * 1.618m:F2}";
                        _signals.Add(new MultiWaveSignal(MultiWavePatternType.DoubleTopBreakout, true, _currentIndex, prev2.High, prev2.High, confidence, invalidation, i - 2, i, prev2.High + height * 1.0m, prev2.High + height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                        _lastSignalWaveIndex = i;
                        CurrentPhase = MultiWavePhase.Breakout;
                    }
                }
            }
        }
        else
        {
            if (wave.Low < prev2.Low)
            {
                var prev1 = _waves[i - 1]; // needed for InvalidationPrice!
                bool isTripleBottom = false;
                if (i >= 4)
                {
                    var prev3 = _waves[i - 3];
                    var prev4 = _waves[i - 4];
                    if (System.Math.Abs(prev2.Low - prev4.Low) < _tolerance)
                    {
                        isTripleBottom = true;
                        // TripleBottom weights: W=(0.35, 0.35, 0.30)
                        decimal impulseRange = System.Math.Max(prev1.High, prev3.High) - prev2.Low;
                        decimal pullbackRange = System.Math.Max(prev1.High - prev1.Low, prev3.High - prev3.Low);
                        decimal breakoutDist  = prev2.Low - wave.Low;
                        double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount + prev4.BlockCount, Weights.TriplePatternRefBlocks);
                        double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                        double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                        double confidence = System.Math.Clamp(Weights.TripleWs * ws + Weights.TriplePs * ps + Weights.TripleBw * bw, 0.0, 1.0);
                        decimal invalidation = System.Math.Max(prev1.High, prev3.High);
                        decimal height = invalidation - prev2.Low;
                        string patternName = FormatPatternName(MultiWavePatternType.TripleBottomBreakout);
                        string priceRange = $"Target: {prev2.Low - height * 1.0m:F2} - {prev2.Low - height * 1.618m:F2}";
                        _signals.Add(new MultiWaveSignal(MultiWavePatternType.TripleBottomBreakout, false, _currentIndex, prev2.Low, prev2.Low, confidence, invalidation, i - 4, i, prev2.Low - height * 1.0m, prev2.Low - height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                        _lastSignalWaveIndex = i;
                        CurrentPhase = MultiWavePhase.Breakout;
                    }

                    if (!isTripleBottom && prev2.Low > prev4.Low && prev1.High < prev3.High)
                    {
                        // BearishTriangle weights: W=(0.30, 0.40, 0.30)
                        decimal impulseRange = System.Math.Max(prev3.High, prev1.High) - prev4.Low;
                        decimal pullbackRange = prev1.High - prev1.Low;
                        decimal breakoutDist  = prev2.Low - wave.Low;
                        double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount, Weights.DoublePatternRefBlocks);
                        double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                        double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                        double confidence = System.Math.Clamp(Weights.TriangleWs * ws + Weights.TrianglePs * ps + Weights.TriangleBw * bw, 0.0, 1.0);
                        decimal invalidation = prev1.High;
                        decimal height = invalidation - prev2.Low;
                        string patternName = FormatPatternName(MultiWavePatternType.BearishTriangleBreakout);
                        string priceRange = $"Target: {prev2.Low - height * 1.0m:F2} - {prev2.Low - height * 1.618m:F2}";
                        _signals.Add(new MultiWaveSignal(MultiWavePatternType.BearishTriangleBreakout, false, _currentIndex, prev2.Low, prev2.Low, confidence, invalidation, i - 4, i, prev2.Low - height * 1.0m, prev2.Low - height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                        _lastSignalWaveIndex = i;
                        CurrentPhase = MultiWavePhase.Breakout;
                    }
                }

                if (_lastSignalWaveIndex != i)
                {
                    bool isCatapult = false;
                    if (i >= 6)
                    {
                        var prev4 = _waves[i - 4];
                        var prev6 = _waves[i - 6];
                        if (prev2.Low < prev4.Low && System.Math.Abs(prev4.Low - prev6.Low) < _tolerance)
                        {
                            isCatapult = true;
                            // BearishCatapult weights: W=(0.35, 0.30, 0.35)
                            decimal impulseRange = prev1.High - wave.Low;
                            decimal pullbackRange = prev1.High - prev1.Low;
                            decimal breakoutDist  = prev2.Low - wave.Low;
                            double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount + prev4.BlockCount, Weights.TriplePatternRefBlocks);
                            double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                            double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                            double confidence = System.Math.Clamp(Weights.CatapultWs * ws + Weights.CatapultPs * ps + Weights.CatapultBw * bw, 0.0, 1.0);
                            decimal invalidation = prev1.High;
                            decimal height = invalidation - prev2.Low;
                            string patternName = FormatPatternName(MultiWavePatternType.BearishCatapult);
                            string priceRange = $"Target: {prev2.Low - height * 1.0m:F2} - {prev2.Low - height * 1.618m:F2}";
                            _signals.Add(new MultiWaveSignal(MultiWavePatternType.BearishCatapult, false, _currentIndex, prev2.Low, prev2.Low, confidence, invalidation, i - 6, i, prev2.Low - height * 1.0m, prev2.Low - height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                            _lastSignalWaveIndex = i;
                            CurrentPhase = MultiWavePhase.Expansion;
                        }
                    }

                    if (!isCatapult)
                    {
                        // DoubleBottom weights: W=(0.40, 0.40, 0.20)
                        decimal impulseRange = prev1.High - prev2.Low;
                        decimal pullbackRange = prev1.High - prev1.Low;
                        decimal breakoutDist  = prev2.Low - wave.Low;
                        double ws = ComputeWaveStrength(wave.BlockCount + prev2.BlockCount, Weights.DoublePatternRefBlocks);
                        double ps = ComputePullbackShallowness(impulseRange, pullbackRange);
                        double bw = ComputeBreakoutWidth(breakoutDist, impulseRange);
                        double confidence = System.Math.Clamp(Weights.DoubleWs * ws + Weights.DoublePs * ps + Weights.DoubleBw * bw, 0.0, 1.0);
                        decimal invalidation = prev1.High;
                        decimal height = invalidation - prev2.Low;
                        string patternName = FormatPatternName(MultiWavePatternType.DoubleBottomBreakout);
                        string priceRange = $"Target: {prev2.Low - height * 1.0m:F2} - {prev2.Low - height * 1.618m:F2}";
                        _signals.Add(new MultiWaveSignal(MultiWavePatternType.DoubleBottomBreakout, false, _currentIndex, prev2.Low, prev2.Low, confidence, invalidation, i - 2, i, prev2.Low - height * 1.0m, prev2.Low - height * 1.618m, false, -1, patternName, priceRange, ws, ps, bw));
                        _lastSignalWaveIndex = i;
                        CurrentPhase = MultiWavePhase.Breakout;
                    }
                }
            }
        }
    }

    private static string FormatPatternName(MultiWavePatternType type)
    {
        return type switch
        {
            MultiWavePatternType.DoubleTopBreakout => "Double Top Breakout",
            MultiWavePatternType.DoubleBottomBreakout => "Double Bottom Breakout",
            MultiWavePatternType.TripleTopBreakout => "Triple Top Breakout",
            MultiWavePatternType.TripleBottomBreakout => "Triple Bottom Breakout",
            MultiWavePatternType.BullishCatapult => "Bullish Catapult",
            MultiWavePatternType.BearishCatapult => "Bearish Catapult",
            MultiWavePatternType.BullishTriangleBreakout => "Bullish Triangle Breakout",
            MultiWavePatternType.BearishTriangleBreakout => "Bearish Triangle Breakout",
            _ => type.ToString()
        };
    }
}
