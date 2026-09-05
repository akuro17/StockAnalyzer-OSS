using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Volatility;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Indicators;

public class CoreSSAResidualBandIndicatorTests
{
    private readonly List<CoreCandleData> _testData;

    public CoreSSAResidualBandIndicatorTests()
    {
        _testData = new List<CoreCandleData>();
        for (int i = 0; i < 70; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(i * 0.2) * 10m + i * 0.5m;
            _testData.Add(new CoreCandleData(
                new DateTime(2023, 1, 1).AddDays(i),
                price - 1m,
                price + 2m,
                price - 2m,
                price,
                1000 + i * 10
            ));
        }
    }

    [Fact]
    public void Factory_IsRegistered_ReturnsCorrectType()
    {
        Assert.True(IndicatorFactory.Default.IsRegistered(IndicatorType.SSAResidualBand));
        var indicator = IndicatorFactory.Default.Create(IndicatorType.SSAResidualBand);
        Assert.NotNull(indicator);
        Assert.IsType<CoreSSAResidualBandIndicator>(indicator);
    }

    [Fact]
    public void DefaultSettings_HasCorrectConfiguration()
    {
        var settingsList = DefaultCoreIndicatorSettings.GetDefault();
        var settings = settingsList.FirstOrDefault(s => s.TypeEnum == IndicatorType.SSAResidualBand);
        Assert.NotNull(settings);
        Assert.True(settings.IsOverlay);
        Assert.Equal(CoreIndicatorCategory.Volatility, settings.Category);
        Assert.Equal(IndicatorDefaultConstants.SsaResidualBandCenterColor, settings.Color);
        Assert.IsType<CoreSSAResidualBandParameter>(settings.ParameterObject);
    }

    [Fact]
    public void Parameter_Validation_EnforcesConstraints()
    {
        var param = new CoreSSAResidualBandParameter
        {
            WindowSize = 64,
            EmbeddingDimension = 20,
            NumComponents = 2,
            Multiplier = 2.0m
        };
        param.Validate();

        param.WindowSize = 3;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.WindowSize = 64;

        param.EmbeddingDimension = 1;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.EmbeddingDimension = 33;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.EmbeddingDimension = 20;

        param.NumComponents = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());

        param.NumComponents = 21;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
        param.NumComponents = 2;

        param.Multiplier = 0m;
        Assert.Throws<ArgumentOutOfRangeException>(() => param.Validate());
    }

    [Fact]
    public void Calculate_WithValidData_ProducesSymmetricBandsAndBandWidth()
    {
        var indicator = new CoreSSAResidualBandIndicator
        {
            WindowSize = 16,
            EmbeddingDimension = 6,
            NumComponents = 2,
            Multiplier = 2.0m
        };

        var result = indicator.Calculate(_testData);
        Assert.True(result.IsSuccessful);

        var center = indicator.CenterBand;
        var upper = indicator.UpperBand;
        var lower = indicator.LowerBand;
        var bw = indicator.BandWidth;

        Assert.Equal(_testData.Count, center.Count);
        Assert.Equal(_testData.Count, upper.Count);
        Assert.Equal(_testData.Count, lower.Count);
        Assert.Equal(_testData.Count, bw.Count);

        for (int i = 0; i < 15; i++)
        {
            Assert.Null(center[i]);
            Assert.Null(upper[i]);
            Assert.Null(lower[i]);
            Assert.Null(bw[i]);
        }

        for (int i = 15; i < _testData.Count; i++)
        {
            Assert.NotNull(center[i]);
            Assert.NotNull(upper[i]);
            Assert.NotNull(lower[i]);
            Assert.NotNull(bw[i]);

            Assert.True(upper[i] >= center[i], $"Upper ({upper[i]}) should be >= Center ({center[i]}) at bar {i}");
            Assert.True(center[i] >= lower[i], $"Center ({center[i]}) should be >= Lower ({lower[i]}) at bar {i}");
            Assert.True(bw[i] >= 0m, $"BandWidth ({bw[i]}) should be >= 0 at bar {i}");
        }

        // Overlay series dictionary must contain only price-native series (no percentage BandWidth)
        Assert.True(result.HasSeries(IndicatorResult.MainSeriesName));
        Assert.True(result.HasSeries(CoreSSAResidualBandIndicator.UpperSeriesName));
        Assert.True(result.HasSeries(CoreSSAResidualBandIndicator.LowerSeriesName));
        Assert.False(result.HasSeries(CoreSSAResidualBandIndicator.BandWidthSeriesName));
    }

    [Fact]
    public void Calculate_ConstantPriceSeries_ZeroBandWidth()
    {
        var flatData = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            flatData.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }

        var indicator = new CoreSSAResidualBandIndicator
        {
            WindowSize = 10,
            EmbeddingDimension = 4,
            NumComponents = 2,
            Multiplier = 2.0m
        };

        var result = indicator.Calculate(flatData);
        Assert.True(result.IsSuccessful);

        for (int i = 9; i < 30; i++)
        {
            Assert.Equal(100m, indicator.CenterBand[i]);
            Assert.Equal(100m, indicator.UpperBand[i]);
            Assert.Equal(100m, indicator.LowerBand[i]);
            Assert.Equal(0m, indicator.BandWidth[i]);
        }
    }

    [Fact]
    public void Calculate_Causality_StrictNonRepaintingTest()
    {
        var indicator = new CoreSSAResidualBandIndicator
        {
            WindowSize = 16,
            EmbeddingDimension = 6,
            NumComponents = 2,
            Multiplier = 2.0m
        };

        var data40 = _testData.Take(40).ToList();
        var result40 = indicator.Calculate(data40);
        Assert.True(result40.IsSuccessful);
        var center40 = indicator.CenterBand.ToList();
        var upper40 = indicator.UpperBand.ToList();
        var lower40 = indicator.LowerBand.ToList();

        var result70 = indicator.Calculate(_testData);
        Assert.True(result70.IsSuccessful);
        var center70 = indicator.CenterBand.ToList();
        var upper70 = indicator.UpperBand.ToList();
        var lower70 = indicator.LowerBand.ToList();

        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(center40[i], center70[i]);
            Assert.Equal(upper40[i], upper70[i]);
            Assert.Equal(lower40[i], lower70[i]);
        }
    }

    [Fact]
    public void Calculate_LargeWindow_ArrayPoolMemorySafety()
    {
        var largeData = new List<CoreCandleData>();
        for (int i = 0; i < 350; i++)
        {
            decimal price = 100m + (decimal)Math.Sin(i * 0.1) * 15m;
            largeData.Add(new CoreCandleData(new DateTime(2023, 1, 1).AddDays(i), price, price + 1, price - 1, price, 1000));
        }

        var indicator = new CoreSSAResidualBandIndicator
        {
            WindowSize = 300, // > 256 triggers ArrayPool path
            EmbeddingDimension = 50,
            NumComponents = 2,
            Multiplier = 2.0m
        };

        var result = indicator.Calculate(largeData);
        Assert.True(result.IsSuccessful);
        Assert.Equal(350, indicator.CenterBand.Count);
        Assert.NotNull(indicator.CenterBand[349]);
    }

    [Fact]
    public void Calculate_FastEigenEnergyMode_ProducesValidBands()
    {
        var indicator = new CoreSSAResidualBandIndicator
        {
            WindowSize = 16,
            EmbeddingDimension = 6,
            NumComponents = 2,
            Multiplier = 2.0m,
            SigmaMode = SsaResidualBandSigmaMode.FastEigenEnergy
        };

        var result = indicator.Calculate(_testData);
        Assert.True(result.IsSuccessful);

        var center = indicator.CenterBand;
        var upper = indicator.UpperBand;
        var lower = indicator.LowerBand;

        for (int i = 15; i < _testData.Count; i++)
        {
            Assert.NotNull(center[i]);
            Assert.NotNull(upper[i]);
            Assert.NotNull(lower[i]);
            Assert.True(upper[i] >= center[i]);
            Assert.True(center[i] >= lower[i]);
        }
    }
}
