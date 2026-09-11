using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using StockAnalyzer.Core.Models.Parameters;
using System.Windows.Media;
using StockAnalyzer.Services;

namespace StockAnalyzer.Tests.Serialization;

public class IndicatorSettingsSerializationTests
{
    private readonly JsonSerializerOptions _options;

    public IndicatorSettingsSerializationTests()
    {
        _options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new ColorJsonConverter() }
        };
    }

    [Fact]
    public void SerializeAndDeserialize_Sma_ShouldPreservePeriodParameter()
    {
        // Arrange
        var original = new IndicatorSettings
        {
            Type = "SMA",
            Category = IndicatorCategory.Trend,
            ParameterObject = new PeriodParameter { Period = 50 },
            Color = Colors.Red,
            Thickness = 2.0
        };

        // Act
        var json = JsonSerializer.Serialize(original, _options);
        var deserialized = JsonSerializer.Deserialize<IndicatorSettings>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Type, deserialized.Type);
        Assert.Equal(original.Category, deserialized.Category);
        Assert.IsType<PeriodParameter>(deserialized.ParameterObject);
        Assert.Equal(50, ((PeriodParameter)deserialized.ParameterObject).Period);
        Assert.Equal(original.Color, deserialized.Color);
    }

    [Fact]
    public void SerializeAndDeserialize_Macd_ShouldPreserveMacdParameter()
    {
        // Arrange
        var original = new IndicatorSettings
        {
            Type = "MACD",
            Category = IndicatorCategory.Oscillator,
            ParameterObject = new MacdParameter 
            { 
                ShortPeriod = 12, 
                LongPeriod = 26, 
                SignalPeriod = 9,
                SignalColor = Colors.Blue,
                HistogramUpColor = Colors.Green,
                HistogramDownColor = Colors.Red
            }
        };

        // Act
        var json = JsonSerializer.Serialize(original, _options);
        var deserialized = JsonSerializer.Deserialize<IndicatorSettings>(json, _options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.IsType<MacdParameter>(deserialized.ParameterObject);
        var param = (MacdParameter)deserialized.ParameterObject;
        Assert.Equal(12, param.ShortPeriod);
        Assert.Equal(26, param.LongPeriod);
        Assert.Equal(Colors.Blue, param.SignalColor);
    }

    [Fact]
    public void SerializeAndDeserialize_List_ShouldPreservePolymorphism()
    {
        // Arrange
        var list = new List<IndicatorSettings>
        {
            new IndicatorSettings { Type = "SMA", ParameterObject = new PeriodParameter { Period = 20 } },
            new IndicatorSettings { Type = "BB", ParameterObject = new BollingerBandsParameter { Period = 20, Sigma = 2.0 } }
        };

        // Act
        var json = JsonSerializer.Serialize(list, _options);
        var deserializedList = JsonSerializer.Deserialize<List<IndicatorSettings>>(json, _options);

        // Assert
        Assert.NotNull(deserializedList);
        Assert.Equal(2, deserializedList.Count);
        Assert.IsType<PeriodParameter>(deserializedList[0].ParameterObject);
        Assert.IsType<BollingerBandsParameter>(deserializedList[1].ParameterObject);
    }
}
