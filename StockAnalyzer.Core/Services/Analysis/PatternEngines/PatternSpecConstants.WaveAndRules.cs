namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The numbers of the Elliott Wave (<c>05_Wave_Theory\01, 02</c>) and Granville's Law (<c>04_Trading_Rules\01, 02</c>) specifications that are definitions and not user settings
/// (plan ChartPatterns56 v2, run V2-7; ledger <c>docs/sa_pattern_literal_ledger_V2-7_ElliottGranville</c>). Tunable settings live in <c>PatternFlagParameterLimits.WaveAndRules.cs</c>.
/// </summary>
public static partial class PatternSpecConstants
{
    // ---------------------------------------------------------------- Elliott Wave (05_Wave_Theory/01, 02)

    /// <summary>Spec headers (01 &amp; 02): minimum history (bars) of the Elliott Wave indicators (40-bar origin zone + initial impulse/corrective wave).</summary>
    public const int ElliottMinBars = 40;

    /// <summary>Spec 01/02 section 2: CausalZigZag fractal delay (none specified for Elliott Wave, 0 = reversal bar).</summary>
    public const int ElliottFractalBars = 0;

    /// <summary>Spec 01/02 section 2: lookback bars of the origin <c>p0</c> price-zone percentile.</summary>
    public const int ElliottZoneBars = 40;

    /// <summary>Spec 01/02 section 2: origin zone threshold (bullish &lt;= 0.25, bearish &gt;= 0.75).</summary>
    public const decimal ElliottOriginZoneThreshold = 0.25m;

    /// <summary>Spec 01/02 section 3: denominator floor (bars) in time symmetry <c>max(d_k) / max(3.0, min(d_k))</c>.</summary>
    public const decimal ElliottTimeSymmetryFloorBars = 3.0m;

    /// <summary>Spec 01 section 3: minimum and maximum duration (bars) of each wave in a 5-wave impulse.</summary>
    public const int ElliottImpulseWaveBarsMin = 2;
    public const int ElliottImpulseWaveBarsMax = 60;

    /// <summary>Spec 02 section 3: minimum and maximum duration (bars) of each wave in an ABC corrective pattern.</summary>
    public const int ElliottCorrectiveWaveBarsMin = 2;
    public const int ElliottCorrectiveWaveBarsMax = 50;

    /// <summary>Spec 01 section 4: maximum number of individual Fibonacci ratios permitted to score 0 in Impulse 1-5.</summary>
    public const int ElliottImpulseMaxZeroRatioScores = 1;

    /// <summary>Spec 01 section 4: Impulse Fibonacci ideal intervals and weights (sum = 1.0).</summary>
    public const decimal ElliottImpulseIdealW2W1Min = 0.382m;
    public const decimal ElliottImpulseIdealW2W1Max = 0.618m;
    public const decimal ElliottImpulseWeightW2W1 = 0.20m;

    public const decimal ElliottImpulseIdealW3W1Min = 1.272m;
    public const decimal ElliottImpulseIdealW3W1Max = 1.618m;
    public const decimal ElliottImpulseWeightW3W1 = 0.35m;

    public const decimal ElliottImpulseIdealW4W3Min = 0.236m;
    public const decimal ElliottImpulseIdealW4W3Max = 0.500m;
    public const decimal ElliottImpulseWeightW4W3 = 0.20m;

    public const decimal ElliottImpulseIdealW5W1Min = 0.618m;
    public const decimal ElliottImpulseIdealW5W1Max = 1.000m;
    public const decimal ElliottImpulseWeightW5W1 = 0.25m;

    /// <summary>Spec 01 section 5: ROC lookback bars for the auxiliary Wave 5 divergence attribute.</summary>
    public const int ElliottRocLookbackBars = 5;

    /// <summary>Spec 02 section 4: Corrective ABC Fibonacci ideal intervals and weights (sum = 1.0).</summary>
    public const decimal ElliottCorrectiveIdealWBWAMin = 0.382m;
    public const decimal ElliottCorrectiveIdealWBWAMax = 0.786m;
    public const decimal ElliottCorrectiveWeightWBWA = 0.40m;

    public const decimal ElliottCorrectiveIdealWCWAMin = 0.618m;
    public const decimal ElliottCorrectiveIdealWCWAMax = 1.618m;
    public const decimal ElliottCorrectiveWeightWCWA = 0.60m;

    // ---------------------------------------------------------------- Granville's Law (04_Trading_Rules/01, 02)

    /// <summary>Spec 01/02 section 3: default minimum history (<c>MaPeriod 200 + SlopePeriod 5 + 1</c>).</summary>
    public const int GranvilleDefaultMinBars = 206;

    /// <summary>Spec 01 section 5: Rule 1 optional volume confirmation multiplier (<c>1.20 * AvgVol(t-1, 20)</c>).</summary>
    public const decimal GranvilleRule1VolumeMultiplier = 1.20m;
}
