using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public sealed class FeatureChannelNormalizationDefaultsTests
{
    [Theory]
    [InlineData(IndicatorType.RSI)] [InlineData(IndicatorType.Stoch)] [InlineData(IndicatorType.WilliamsR)] [InlineData(IndicatorType.MFI)]
    [InlineData(IndicatorType.ADX)] [InlineData(IndicatorType.CMF)] [InlineData(IndicatorType.ROC)] [InlineData(IndicatorType.PPO)]
    [InlineData(IndicatorType.BandWidth)] [InlineData(IndicatorType.NATR)] [InlineData(IndicatorType.ZScore)] [InlineData(IndicatorType.CCI)]
    public void BoundedOscillatorsRatiosAndPercentages_NeedNoNormalization(IndicatorType type) =>
        Assert.Equal(ChannelNormalization.None, FeatureChannelNormalizationDefaults.ForIndicator(type));

    [Theory]
    [InlineData(IndicatorType.SMA)] [InlineData(IndicatorType.EMA)] [InlineData(IndicatorType.BB)] [InlineData(IndicatorType.VWAP)]
    [InlineData(IndicatorType.ParabolicSAR)] [InlineData(IndicatorType.Ichimoku)] [InlineData(IndicatorType.Price)]
    public void PriceUnitSeries_AreMinMaxScaledLikeThePriceFields(IndicatorType type)
    {
        Assert.Equal(ChannelNormalization.WindowMinMax, FeatureChannelNormalizationDefaults.ForIndicator(type));
        Assert.Equal(ChannelNormalization.WindowMinMax, FeatureChannelNormalizationDefaults.ForPrice());
    }

    [Theory]
    [InlineData(IndicatorType.Volume)] [InlineData(IndicatorType.OBV)] [InlineData(IndicatorType.ATR)] [InlineData(IndicatorType.MACD)]
    [InlineData(IndicatorType.Momentum)] [InlineData(IndicatorType.ADL)]
    public void EverythingElse_IsZScoredSoItIsNeverFedRaw(IndicatorType type) =>
        Assert.Equal(ChannelNormalization.WindowZScore, FeatureChannelNormalizationDefaults.ForIndicator(type));

    [Fact]
    public void EveryIndicatorTypeHasADefault()
    {
        foreach (var type in Enum.GetValues<IndicatorType>())
            Assert.True(Enum.IsDefined(FeatureChannelNormalizationDefaults.ForIndicator(type)), type.ToString());
    }
}
