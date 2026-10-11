using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The fingerprint of the prediction settings that decide whether a recorded load failure or a cached attention context still applies.</summary>
public sealed class PredictionContractFingerprintTests
{
    private static IStockAnalyzerSettings Settings(int window = 75, PredictionFeatureMode mode = PredictionFeatureMode.OhlcvMinMax,
        string[]? labels = null, FeatureSpec? spec = null)
    {
        var settings = new Mock<IStockAnalyzerSettings>();
        settings.SetupGet(s => s.PredictionWindowSize).Returns(window);
        settings.SetupGet(s => s.PredictionFeatureMode).Returns(mode);
        settings.SetupGet(s => s.PredictionClassLabels).Returns(labels ?? new[] { "Up", "Down", "Neutral" });
        settings.SetupGet(s => s.PredictionFeatureSpec).Returns(spec);
        return settings.Object;
    }

    private static FeatureSpec Spec(int lag) => new() { Channels = Array.Empty<FeatureChannel>(), Lags = new[] { lag } };

    [Fact]
    public void Compute_MatchesTheKnownAnswerVector()
    {
        // window 75, mode OhlcvMinMax, labels Up,Down,Neutral, no spec: the layout is v1 \n window \n mode \n spec \n labels
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v1\n75\nOhlcvMinMax\n\nUp,Down,Neutral"))).ToLowerInvariant();

        Assert.Equal(expected, PredictionContractFingerprint.Compute(Settings()));
    }

    [Fact]
    public void Compute_IdenticalSettingsGiveIdenticalValues()
    {
        Assert.Equal(PredictionContractFingerprint.Compute(Settings()), PredictionContractFingerprint.Compute(Settings()));
        Assert.Equal(PredictionContractFingerprint.Compute(Settings(mode: PredictionFeatureMode.ComposedFeatures, spec: Spec(1))),
            PredictionContractFingerprint.Compute(Settings(mode: PredictionFeatureMode.ComposedFeatures, spec: Spec(1))));
    }

    [Fact]
    public void Compute_ChangesWithTheWindowTheModeTheLabelsAndTheSpec_Each_OnItsOwn()
    {
        var baseline = PredictionContractFingerprint.Compute(Settings());
        var composed = Settings(mode: PredictionFeatureMode.ComposedFeatures, spec: Spec(1));
        var values = new List<string>
        {
            baseline,
            PredictionContractFingerprint.Compute(Settings(window: 76)),
            PredictionContractFingerprint.Compute(Settings(mode: PredictionFeatureMode.LogReturn)),
            PredictionContractFingerprint.Compute(Settings(labels: new[] { "Up", "Neutral", "Down" })),
            PredictionContractFingerprint.Compute(composed),
            PredictionContractFingerprint.Compute(Settings(mode: PredictionFeatureMode.ComposedFeatures, spec: Spec(2))),
        };

        Assert.Equal(values.Count, new HashSet<string>(values).Count);
    }

    [Fact]
    public void Compute_IgnoresAFeatureSpecOutsideComposedMode()
    {
        Assert.Equal(PredictionContractFingerprint.Compute(Settings()), PredictionContractFingerprint.Compute(Settings(spec: Spec(3))));
    }
}
