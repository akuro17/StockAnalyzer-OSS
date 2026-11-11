using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// The lookup of a parameter name on the parameter type is cached per TYPE. Names come from the condition (screen entries, MCP
/// arguments), so an unknown name must not add an entry anywhere.
/// </summary>
public class ScreenerParameterPropertyLookupTests
{
    private static List<CoreCandleData> Candles()
    {
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 60; i++)
        {
            decimal price = 100m + (i % 9);
            candles.Add(new CoreCandleData(new DateTime(2025, 1, 1).AddDays(i), price, price + 1m, price - 1m, price + 0.5m, 1000));
        }
        return candles;
    }

    // The cache is static and other tests fill it in parallel, so the test inspects what the entry of ONE type contains instead of counting entries.
    private static IReadOnlyCollection<string> CachedPropertyNamesOf(Type parameterType)
    {
        var field = typeof(ScreenerValueExtractor).GetField("_parameterPropertiesByType", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var cache = (IDictionary)field!.GetValue(null)!;
        var table = cache[parameterType] as IReadOnlyDictionary<string, PropertyInfo>;
        Assert.NotNull(table);
        return new List<string>(table!.Keys);
    }

    [Fact]
    public void UnknownParameterNames_AddNoCacheEntry()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = Candles();
        var warm = new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { ["Period"] = 5 } };
        extractor.ExtractValueNullable(warm, candles);
        var before = CachedPropertyNamesOf(typeof(StockAnalyzer.Core.Models.Parameters.CoreSmaParameter));

        for (int i = 0; i < 2000; i++)
        {
            var config = new ScreenerIndicatorSideConfig
            {
                IndicatorType = IndicatorType.SMA,
                Parameters = new Dictionary<string, object> { ["Period"] = 5, ["NoSuchKey" + i] = i }
            };
            extractor.ExtractValueNullable(config, candles);
        }

        var after = CachedPropertyNamesOf(typeof(StockAnalyzer.Core.Models.Parameters.CoreSmaParameter));
        Assert.Equal(before, after);
        Assert.Contains("Period", after);
        Assert.DoesNotContain(after, name => name.StartsWith("NoSuchKey", StringComparison.Ordinal));
    }

    [Fact]
    public void AKnownNameStillAppliesTheValue_AndTheNameIsCaseSensitive()
    {
        var extractor = new ScreenerValueExtractor();
        var candles = Candles();

        decimal? five = extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { ["Period"] = 5 } }, candles);
        decimal? twenty = extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { ["Period"] = 20 } }, candles);
        decimal? wrongCase = extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA, Parameters = new Dictionary<string, object> { ["period"] = 5 } }, candles);
        decimal? defaults = extractor.ExtractValueNullable(new ScreenerIndicatorSideConfig { IndicatorType = IndicatorType.SMA }, candles);

        Assert.NotEqual(five, twenty);
        Assert.Equal(defaults, wrongCase);   // "period" is not "Period": ignored, the default Period applies (as GetProperty(name) did)
    }
}
