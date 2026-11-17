using System;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>WebAI UI-exposure follow-up Run 3: Kalman Empty rule, persisted enum values, separability grade thresholds.</summary>
public class UiExposureRun3Tests
{
    [Fact]
    public void Kalman_BandBeyondTheDecimalRange_ReturnsEmptyInsteadOfThrowing()
    {
        decimal[] closes = new decimal[40];
        for (int i = 0; i < closes.Length; i++) closes[i] = 100m + i * 1.5m + (i % 3 == 0 ? 1m : -0.5m);

        var normal = KalmanProjectionAnalysis.CalculateProjection(closes, 10, 0.01m, 0.1m, 2m);
        var refused = KalmanProjectionAnalysis.CalculateProjection(closes, 10, 0.01m, 0.1m, 1e28m);

        Assert.False(normal.IsEmpty);
        Assert.True(refused.IsEmpty);
        Assert.Empty(refused.Steps);
    }

    [Theory]
    [InlineData(SsaRankSelectionMethod.KaiserGuttman, 0)]
    [InlineData(SsaRankSelectionMethod.ScreeMaxCurvature, 1)]
    [InlineData(SsaRankSelectionMethod.CumulativeEnergy, 2)]
    public void SsaRankSelectionMethod_ValuesAreAPersistedIntegerContract(SsaRankSelectionMethod method, int expected)
    {
        // The settings panels map ComboBox.SelectedIndex to these integers and drawings persist them: append-only, never renumbered.
        Assert.Equal(expected, (int)method);
    }

    [Theory]
    [InlineData(SsaDiagnostics.ExcellentSeparabilityScore, "Excellent")]
    [InlineData(SsaDiagnostics.ExcellentSeparabilityScore - 0.01, "Good")]
    [InlineData(SsaDiagnostics.GoodSeparabilityScore, "Good")]
    [InlineData(SsaDiagnostics.GoodSeparabilityScore - 0.01, "Moderate")]
    [InlineData(SsaDiagnostics.ModerateSeparabilityScore, "Moderate")]
    [InlineData(SsaDiagnostics.ModerateSeparabilityScore - 0.01, "Poor")]
    public void SeparabilityGrade_ChangesAtTheNamedThresholds(double score, string expectedGrade)
    {
        Assert.Equal(expectedGrade, SsaDiagnostics.GetSeparabilityGrade(score));
    }

    [Fact]
    public void HoughChannelPositionConstants_AreDecimal()
    {
        Assert.Equal(50m, HoughChannel.NeutralPositionPercent);
        Assert.Equal(999.9m, HoughChannel.MaxPositionPercentMagnitude);
    }
}
