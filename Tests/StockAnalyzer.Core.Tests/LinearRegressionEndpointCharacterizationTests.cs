using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using StockAnalyzer.TestSupport;
using static StockAnalyzer.TestSupport.CharacterizationSupport;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// Characterization of the regression-endpoint series of <see cref="CoreLrmaIndicator"/> and <see cref="CoreLsmaIndicator"/>
/// (fixture F-OHLC8, mixed decimal scales). The expected lines were captured before the shared loop was extracted and must never be
/// edited: every value is compared by its text and by <see cref="decimal.GetBits(decimal)"/>.
/// </summary>
public class LinearRegressionEndpointCharacterizationTests
{
    private readonly ITestOutputHelper _output;

    public LinearRegressionEndpointCharacterizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static List<CoreCandleData> Candles() =>
        Enumerable.Range(0, Ohlc8Rows.Length)
            .Select(i => Ohlc8Core(i))
            .ToList();

    private static void Add(List<string> lines, string label, IReadOnlyList<decimal?> values)
    {
        lines.Add($"{label}|count|{values.Count}");
        for (int i = 0; i < values.Count; i++)
        {
            lines.Add($"{label}|{i}|{Format(values[i])}");
        }
    }

    private static List<string> Capture()
    {
        var lines = new List<string>();
        var candles = Candles();

        foreach (PriceType source in new[] { PriceType.Close, PriceType.HeikinAshiClose })
        {
            foreach (int period in new[] { 1, 2, 3, 5, 8, 9 })
            {
                var lrma = new CoreLrmaIndicator { Period = period, PriceSource = source };
                var lsma = new CoreLsmaIndicator { Period = period, PriceSource = source };
                Add(lines, $"LRMA|{source}|{period}", lrma.Calculate(candles).MainValues);
                Add(lines, $"LSMA|{source}|{period}", lsma.Calculate(candles).MainValues);
            }
        }

        var empty = new CoreLrmaIndicator { Period = 3 }.Calculate(new List<CoreCandleData>());
        lines.Add($"EMPTY|{empty.IsSuccessful}|{empty.MainValues.Count}");
        return lines;
    }

    [Fact]
    public void RegressionEndpointSeries_MatchesCapturedValues()
    {
        var actual = Capture();

        AssertMatches(Expected, actual, _output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(9)]
    public void Lrma_And_Lsma_ReturnIdenticalSeries_ForTheSamePeriod(int period)
    {
        var candles = Candles();
        var lrma = new CoreLrmaIndicator { Period = period }.Calculate(candles).MainValues;
        var lsma = new CoreLsmaIndicator { Period = period }.Calculate(candles).MainValues;

        Assert.Equal(lrma.Count, lsma.Count);
        for (int i = 0; i < lrma.Count; i++)
        {
            Assert.Equal(lrma[i].HasValue, lsma[i].HasValue);
            if (lrma[i].HasValue)
            {
                Assert.Equal(decimal.GetBits(lrma[i]!.Value), decimal.GetBits(lsma[i]!.Value));
            }
        }
    }

    private static readonly string[] Expected =
    {
        "LRMA|Close|1|count|8",
        "LRMA|Close|1|0|null",
        "LRMA|Close|1|1|null",
        "LRMA|Close|1|2|null",
        "LRMA|Close|1|3|null",
        "LRMA|Close|1|4|null",
        "LRMA|Close|1|5|null",
        "LRMA|Close|1|6|null",
        "LRMA|Close|1|7|null",
        "LSMA|Close|1|count|8",
        "LSMA|Close|1|0|null",
        "LSMA|Close|1|1|null",
        "LSMA|Close|1|2|null",
        "LSMA|Close|1|3|null",
        "LSMA|Close|1|4|null",
        "LSMA|Close|1|5|null",
        "LSMA|Close|1|6|null",
        "LSMA|Close|1|7|null",
        "LRMA|Close|2|count|8",
        "LRMA|Close|2|0|null",
        "LRMA|Close|2|1|102.75#10275,0,0,131072",
        "LRMA|Close|2|2|103.00#10300,0,0,131072",
        "LRMA|Close|2|3|100.50#10050,0,0,131072",
        "LRMA|Close|2|4|99.00#9900,0,0,131072",
        "LRMA|Close|2|5|99.75#9975,0,0,131072",
        "LRMA|Close|2|6|101.50#10150,0,0,131072",
        "LRMA|Close|2|7|102.90#10290,0,0,131072",
        "LSMA|Close|2|count|8",
        "LSMA|Close|2|0|null",
        "LSMA|Close|2|1|102.75#10275,0,0,131072",
        "LSMA|Close|2|2|103.00#10300,0,0,131072",
        "LSMA|Close|2|3|100.50#10050,0,0,131072",
        "LSMA|Close|2|4|99.00#9900,0,0,131072",
        "LSMA|Close|2|5|99.75#9975,0,0,131072",
        "LSMA|Close|2|6|101.50#10150,0,0,131072",
        "LSMA|Close|2|7|102.90#10290,0,0,131072",
        "LRMA|Close|3|count|8",
        "LRMA|Close|3|0|null",
        "LRMA|Close|3|1|null",
        "LRMA|Close|3|2|103.25#10325,0,0,131072",
        "LRMA|Close|3|3|100.95833333333333333333333333#2144687445,-1492163584,547296221,1703936",
        "LRMA|Close|3|4|98.83333333333333333333333333#1532319061,-1847179072,535776573,1703936",
        "LRMA|Close|3|5|99.375#99375,0,0,196608",
        "LRMA|Close|3|6|101.33333333333333333333333333#-1789569707,-1176868657,549329100,1703936",
        "LRMA|Close|3|7|102.95833333333333333333333333#1205163349,1621065126,558138243,1703936",
        "LSMA|Close|3|count|8",
        "LSMA|Close|3|0|null",
        "LSMA|Close|3|1|null",
        "LSMA|Close|3|2|103.25#10325,0,0,131072",
        "LSMA|Close|3|3|100.95833333333333333333333333#2144687445,-1492163584,547296221,1703936",
        "LSMA|Close|3|4|98.83333333333333333333333333#1532319061,-1847179072,535776573,1703936",
        "LSMA|Close|3|5|99.375#99375,0,0,196608",
        "LSMA|Close|3|6|101.33333333333333333333333333#-1789569707,-1176868657,549329100,1703936",
        "LSMA|Close|3|7|102.95833333333333333333333333#1205163349,1621065126,558138243,1703936",
        "LRMA|Close|5|count|8",
        "LRMA|Close|5|0|null",
        "LRMA|Close|5|1|null",
        "LRMA|Close|5|2|null",
        "LRMA|Close|5|3|null",
        "LRMA|Close|5|4|100.000#100000,0,0,196608",
        "LRMA|Close|5|5|99.00#9900,0,0,131072",
        "LRMA|Close|5|6|100.000#100000,0,0,196608",
        "LRMA|Close|5|7|102.19#10219,0,0,131072",
        "LSMA|Close|5|count|8",
        "LSMA|Close|5|0|null",
        "LSMA|Close|5|1|null",
        "LSMA|Close|5|2|null",
        "LSMA|Close|5|3|null",
        "LSMA|Close|5|4|100.000#100000,0,0,196608",
        "LSMA|Close|5|5|99.00#9900,0,0,131072",
        "LSMA|Close|5|6|100.000#100000,0,0,196608",
        "LSMA|Close|5|7|102.19#10219,0,0,131072",
        "LRMA|Close|8|count|8",
        "LRMA|Close|8|0|null",
        "LRMA|Close|8|1|null",
        "LRMA|Close|8|2|null",
        "LRMA|Close|8|3|null",
        "LRMA|Close|8|4|null",
        "LRMA|Close|8|5|null",
        "LRMA|Close|8|6|null",
        "LRMA|Close|8|7|101.125#101125,0,0,196608",
        "LSMA|Close|8|count|8",
        "LSMA|Close|8|0|null",
        "LSMA|Close|8|1|null",
        "LSMA|Close|8|2|null",
        "LSMA|Close|8|3|null",
        "LSMA|Close|8|4|null",
        "LSMA|Close|8|5|null",
        "LSMA|Close|8|6|null",
        "LSMA|Close|8|7|101.125#101125,0,0,196608",
        "LRMA|Close|9|count|8",
        "LRMA|Close|9|0|null",
        "LRMA|Close|9|1|null",
        "LRMA|Close|9|2|null",
        "LRMA|Close|9|3|null",
        "LRMA|Close|9|4|null",
        "LRMA|Close|9|5|null",
        "LRMA|Close|9|6|null",
        "LRMA|Close|9|7|null",
        "LSMA|Close|9|count|8",
        "LSMA|Close|9|0|null",
        "LSMA|Close|9|1|null",
        "LSMA|Close|9|2|null",
        "LSMA|Close|9|3|null",
        "LSMA|Close|9|4|null",
        "LSMA|Close|9|5|null",
        "LSMA|Close|9|6|null",
        "LSMA|Close|9|7|null",
        "LRMA|HeikinAshiClose|1|count|8",
        "LRMA|HeikinAshiClose|1|0|null",
        "LRMA|HeikinAshiClose|1|1|null",
        "LRMA|HeikinAshiClose|1|2|null",
        "LRMA|HeikinAshiClose|1|3|null",
        "LRMA|HeikinAshiClose|1|4|null",
        "LRMA|HeikinAshiClose|1|5|null",
        "LRMA|HeikinAshiClose|1|6|null",
        "LRMA|HeikinAshiClose|1|7|null",
        "LSMA|HeikinAshiClose|1|count|8",
        "LSMA|HeikinAshiClose|1|0|null",
        "LSMA|HeikinAshiClose|1|1|null",
        "LSMA|HeikinAshiClose|1|2|null",
        "LSMA|HeikinAshiClose|1|3|null",
        "LSMA|HeikinAshiClose|1|4|null",
        "LSMA|HeikinAshiClose|1|5|null",
        "LSMA|HeikinAshiClose|1|6|null",
        "LSMA|HeikinAshiClose|1|7|null",
        "LRMA|HeikinAshiClose|2|count|8",
        "LRMA|HeikinAshiClose|2|0|null",
        "LRMA|HeikinAshiClose|2|1|101.75#10175,0,0,131072",
        "LRMA|HeikinAshiClose|2|2|102.9375#1029375,0,0,262144",
        "LRMA|HeikinAshiClose|2|3|101.6250#1016250,0,0,262144",
        "LRMA|HeikinAshiClose|2|4|99.875#99875,0,0,196608",
        "LRMA|HeikinAshiClose|2|5|99.0625#990625,0,0,262144",
        "LRMA|HeikinAshiClose|2|6|100.6250#1006250,0,0,262144",
        "LRMA|HeikinAshiClose|2|7|102.150#102150,0,0,196608",
        "LSMA|HeikinAshiClose|2|count|8",
        "LSMA|HeikinAshiClose|2|0|null",
        "LSMA|HeikinAshiClose|2|1|101.75#10175,0,0,131072",
        "LSMA|HeikinAshiClose|2|2|102.9375#1029375,0,0,262144",
        "LSMA|HeikinAshiClose|2|3|101.6250#1016250,0,0,262144",
        "LSMA|HeikinAshiClose|2|4|99.875#99875,0,0,196608",
        "LSMA|HeikinAshiClose|2|5|99.0625#990625,0,0,262144",
        "LSMA|HeikinAshiClose|2|6|100.6250#1006250,0,0,262144",
        "LSMA|HeikinAshiClose|2|7|102.150#102150,0,0,196608",
        "LRMA|HeikinAshiClose|3|count|8",
        "LRMA|HeikinAshiClose|3|0|null",
        "LRMA|HeikinAshiClose|3|1|null",
        "LRMA|HeikinAshiClose|3|2|102.90625#10290625,0,0,327680",
        "LRMA|HeikinAshiClose|3|3|102.04166666666666666666666667#1277864619,1804781370,553168983,1703936",
        "LRMA|HeikinAshiClose|3|4|99.94791666666666666666666667#-1094014293,-313529248,541818741,1703936",
        "LRMA|HeikinAshiClose|3|5|98.90625#9890625,0,0,327680",
        "LRMA|HeikinAshiClose|3|6|100.22916666666666666666666667#250260139,996683772,543343401,1703936",
        "LRMA|HeikinAshiClose|3|7|102.15625#10215625,0,0,327680",
        "LSMA|HeikinAshiClose|3|count|8",
        "LSMA|HeikinAshiClose|3|0|null",
        "LSMA|HeikinAshiClose|3|1|null",
        "LSMA|HeikinAshiClose|3|2|102.90625#10290625,0,0,327680",
        "LSMA|HeikinAshiClose|3|3|102.04166666666666666666666667#1277864619,1804781370,553168983,1703936",
        "LSMA|HeikinAshiClose|3|4|99.94791666666666666666666667#-1094014293,-313529248,541818741,1703936",
        "LSMA|HeikinAshiClose|3|5|98.90625#9890625,0,0,327680",
        "LSMA|HeikinAshiClose|3|6|100.22916666666666666666666667#250260139,996683772,543343401,1703936",
        "LSMA|HeikinAshiClose|3|7|102.15625#10215625,0,0,327680",
        "LRMA|HeikinAshiClose|5|count|8",
        "LRMA|HeikinAshiClose|5|0|null",
        "LRMA|HeikinAshiClose|5|1|null",
        "LRMA|HeikinAshiClose|5|2|null",
        "LRMA|HeikinAshiClose|5|3|null",
        "LRMA|HeikinAshiClose|5|4|101.0125#1010125,0,0,262144",
        "LRMA|HeikinAshiClose|5|5|99.36250#9936250,0,0,327680",
        "LRMA|HeikinAshiClose|5|6|99.38750#9938750,0,0,327680",
        "LRMA|HeikinAshiClose|5|7|101.0275#1010275,0,0,262144",
        "LSMA|HeikinAshiClose|5|count|8",
        "LSMA|HeikinAshiClose|5|0|null",
        "LSMA|HeikinAshiClose|5|1|null",
        "LSMA|HeikinAshiClose|5|2|null",
        "LSMA|HeikinAshiClose|5|3|null",
        "LSMA|HeikinAshiClose|5|4|101.0125#1010125,0,0,262144",
        "LSMA|HeikinAshiClose|5|5|99.36250#9936250,0,0,327680",
        "LSMA|HeikinAshiClose|5|6|99.38750#9938750,0,0,327680",
        "LSMA|HeikinAshiClose|5|7|101.0275#1010275,0,0,262144",
        "LRMA|HeikinAshiClose|8|count|8",
        "LRMA|HeikinAshiClose|8|0|null",
        "LRMA|HeikinAshiClose|8|1|null",
        "LRMA|HeikinAshiClose|8|2|null",
        "LRMA|HeikinAshiClose|8|3|null",
        "LRMA|HeikinAshiClose|8|4|null",
        "LRMA|HeikinAshiClose|8|5|null",
        "LRMA|HeikinAshiClose|8|6|null",
        "LRMA|HeikinAshiClose|8|7|100.71354166666666666666666666#895134378,1582896690,545969203,1703936",
        "LSMA|HeikinAshiClose|8|count|8",
        "LSMA|HeikinAshiClose|8|0|null",
        "LSMA|HeikinAshiClose|8|1|null",
        "LSMA|HeikinAshiClose|8|2|null",
        "LSMA|HeikinAshiClose|8|3|null",
        "LSMA|HeikinAshiClose|8|4|null",
        "LSMA|HeikinAshiClose|8|5|null",
        "LSMA|HeikinAshiClose|8|6|null",
        "LSMA|HeikinAshiClose|8|7|100.71354166666666666666666666#895134378,1582896690,545969203,1703936",
        "LRMA|HeikinAshiClose|9|count|8",
        "LRMA|HeikinAshiClose|9|0|null",
        "LRMA|HeikinAshiClose|9|1|null",
        "LRMA|HeikinAshiClose|9|2|null",
        "LRMA|HeikinAshiClose|9|3|null",
        "LRMA|HeikinAshiClose|9|4|null",
        "LRMA|HeikinAshiClose|9|5|null",
        "LRMA|HeikinAshiClose|9|6|null",
        "LRMA|HeikinAshiClose|9|7|null",
        "LSMA|HeikinAshiClose|9|count|8",
        "LSMA|HeikinAshiClose|9|0|null",
        "LSMA|HeikinAshiClose|9|1|null",
        "LSMA|HeikinAshiClose|9|2|null",
        "LSMA|HeikinAshiClose|9|3|null",
        "LSMA|HeikinAshiClose|9|4|null",
        "LSMA|HeikinAshiClose|9|5|null",
        "LSMA|HeikinAshiClose|9|6|null",
        "LSMA|HeikinAshiClose|9|7|null",
        "EMPTY|True|0",
    };
}
