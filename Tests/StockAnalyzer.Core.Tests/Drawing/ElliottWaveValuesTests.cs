using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class ElliottWaveValuesTests
{
    private static readonly IndicatorColor BaseColor = new(255, 33, 150, 243);

    [Fact]
    public void UpwardValidSixPointImpulse_ReturnsOkAndExactCalculatedValues()
    {
        // p0=100, p1=140 (len1=40), p2=120 (w2=20 -> 50%), p3=200 (len3=80 -> 2.00x),
        // p4=160 (w4=40 -> 50% of len3, > p1=140), p5=220 (len5=60 -> len3=80 not shortest)
        decimal[] prices = { 100m, 140m, 120m, 200m, 160m, 220m };

        var validation = ElliottWaveValues.ValidateImpulse(prices, isImpulse: true);
        Assert.True(validation.IsEvaluated);
        Assert.False(validation.HasAnyViolation);
        Assert.Equal("OK", validation.RuleStatus);

        var values = ElliottWaveValues.Calculate(prices, isImpulse: true, BaseColor);
        Assert.Equal(4, values.Length);
        Assert.Equal(
            new[] { "Wave2Retracement", "Wave3ToWave1Ratio", "Wave4Retracement", "RuleStatus" },
            values.Select(v => v.Key).ToArray());

        Assert.Equal(50m, values[0].NumericValue);
        Assert.Equal("50.00%", values[0].FormattedText);

        Assert.Equal(2m, values[1].NumericValue);
        Assert.Equal("2.00", values[1].FormattedText);

        Assert.Equal(50m, values[2].NumericValue);
        Assert.Equal("50.00%", values[2].FormattedText);

        Assert.Null(values[3].NumericValue);
        Assert.Equal("OK", values[3].FormattedText);
        Assert.Equal(IndicatorColor.Bullish, values[3].Color);
    }

    [Fact]
    public void DownwardValidSixPointImpulse_ReturnsOkAndExactCalculatedValues()
    {
        // p0=200, p1=150 (len1=50), p2=175 (w2=25 -> 50%), p3=75 (len3=100 -> 2.00x),
        // p4=100 (w4=25 -> 25% of len3, < p1=150), p5=40 (len5=60)
        decimal[] prices = { 200m, 150m, 175m, 75m, 100m, 40m };

        var validation = ElliottWaveValues.ValidateImpulse(prices, isImpulse: true);
        Assert.True(validation.IsEvaluated);
        Assert.False(validation.HasAnyViolation);
        Assert.Equal("OK", validation.RuleStatus);

        var values = ElliottWaveValues.Calculate(prices, isImpulse: true, BaseColor);
        Assert.Equal(50m, values[0].NumericValue);
        Assert.Equal(2m, values[1].NumericValue);
        Assert.Equal(25m, values[2].NumericValue);
        Assert.Equal("OK", values[3].FormattedText);
    }

    [Fact]
    public void UpwardViolations_DetectR1_R2_R3_IndividuallyAndCombined()
    {
        // R1 violated: p2=95 <= p0=100 (but p4=150 > p1=140, len3=95 >= len1=40)
        var r1Only = ElliottWaveValues.ValidateImpulse(new[] { 100m, 140m, 95m, 190m, 150m, 210m });
        Assert.True(r1Only.Rule1Violated);
        Assert.False(r1Only.Rule2Violated);
        Assert.False(r1Only.Rule3Violated);
        Assert.Equal("R1", r1Only.RuleStatus);
        Assert.True(r1Only.IsSegmentViolated(1));
        Assert.True(r1Only.IsTerminalPointViolated(2));

        // R2 violated: len3=20 < len1=40 && len3=20 < len5=40
        var r2Only = ElliottWaveValues.ValidateImpulse(new[] { 100m, 140m, 130m, 150m, 145m, 185m });
        Assert.False(r2Only.Rule1Violated);
        Assert.True(r2Only.Rule2Violated);
        Assert.False(r2Only.Rule3Violated);
        Assert.Equal("R2", r2Only.RuleStatus);
        Assert.True(r2Only.IsSegmentViolated(2));
        Assert.True(r2Only.IsTerminalPointViolated(3));

        // R3 violated: p4=135 <= p1=140
        var r3Only = ElliottWaveValues.ValidateImpulse(new[] { 100m, 140m, 120m, 200m, 135m, 195m });
        Assert.False(r3Only.Rule1Violated);
        Assert.False(r3Only.Rule2Violated);
        Assert.True(r3Only.Rule3Violated);
        Assert.Equal("R3", r3Only.RuleStatus);
        Assert.True(r3Only.IsSegmentViolated(3));
        Assert.True(r3Only.IsTerminalPointViolated(4));

        // Combined R1,R3 ascending join
        var r1AndR3 = ElliottWaveValues.ValidateImpulse(new[] { 100m, 140m, 95m, 210m, 130m, 200m });
        Assert.Equal("R1,R3", r1AndR3.RuleStatus);

        var values = ElliottWaveValues.Calculate(new[] { 100m, 140m, 95m, 210m, 130m, 200m }, isImpulse: true, BaseColor);
        Assert.Equal("R1,R3", values[3].FormattedText);
        Assert.Equal(IndicatorColor.Bearish, values[3].Color);
    }

    [Fact]
    public void DownwardViolations_DetectR1_R2_R3_WithMirroredDirection()
    {
        // Downward R1: p2 >= p0
        var r1 = ElliottWaveValues.ValidateImpulse(new[] { 200m, 150m, 205m, 80m, 120m, 50m });
        Assert.Equal("R1", r1.RuleStatus);

        // Downward R2: len3=20 < len1=50 && len3=20 < len5=50
        var r2 = ElliottWaveValues.ValidateImpulse(new[] { 200m, 150m, 160m, 140m, 145m, 95m });
        Assert.Equal("R2", r2.RuleStatus);

        // Downward R3: p4 >= p1
        var r3 = ElliottWaveValues.ValidateImpulse(new[] { 200m, 150m, 175m, 80m, 155m, 90m });
        Assert.Equal("R3", r3.RuleStatus);
    }

    [Fact]
    public void IndeterminateDirection_ZeroLength_PartialCounts_And_NonImpulse_ReturnNullSafely()
    {
        // p1 == p0 -> RuleStatus null and Wave2/Wave3 ratios null (len1 == 0)
        var flatFirstWave = ElliottWaveValues.Calculate(new[] { 100m, 100m, 90m, 130m, 110m, 150m }, isImpulse: true, BaseColor);
        Assert.Null(flatFirstWave[0].NumericValue);
        Assert.Null(flatFirstWave[1].NumericValue);
        Assert.Equal(50m, flatFirstWave[2].NumericValue); // |110-130| / |130-90| * 100 = 50%
        Assert.Equal("-", flatFirstWave[3].FormattedText);
        Assert.Null(ElliottWaveValues.ValidateImpulse(new[] { 100m, 100m, 90m }).RuleStatus);

        // len3 == 0 -> Wave4Retracement null
        var flatWave3 = ElliottWaveValues.Calculate(new[] { 100m, 140m, 120m, 120m, 110m }, isImpulse: true, BaseColor);
        Assert.Equal(50m, flatWave3[0].NumericValue);
        Assert.Equal(0m, flatWave3[1].NumericValue);
        Assert.Null(flatWave3[2].NumericValue);
        Assert.Equal("R3", flatWave3[3].FormattedText);

        // Partial 3-point impulse: evaluates R1 & Wave2Retracement only
        var threePoints = ElliottWaveValues.Calculate(new[] { 100m, 140m, 110m }, isImpulse: true, BaseColor);
        Assert.Equal(75m, threePoints[0].NumericValue);
        Assert.Null(threePoints[1].NumericValue);
        Assert.Null(threePoints[2].NumericValue);
        Assert.Equal("OK", threePoints[3].FormattedText);

        // Non-Impulse mode: fixed length 4, all null values
        var correction = ElliottWaveValues.Calculate(new[] { 100m, 140m, 90m, 180m }, isImpulse: false, BaseColor);
        Assert.Equal(4, correction.Length);
        Assert.All(correction, v =>
        {
            Assert.Null(v.NumericValue);
            Assert.Equal("-", v.FormattedText);
        });
        Assert.Null(ElliottWaveValues.ValidateImpulse(new[] { 100m, 140m, 90m, 180m }, isImpulse: false).RuleStatus);
    }
}
