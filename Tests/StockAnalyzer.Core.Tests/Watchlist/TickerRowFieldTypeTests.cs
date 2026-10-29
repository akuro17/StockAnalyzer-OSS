using System;
using System.Linq;
using StockAnalyzer.Core.Services.Watchlist;
using Xunit;

namespace StockAnalyzer.Core.Tests.Watchlist;

public sealed class TickerRowFieldTypeTests
{
    [Theory]
    [InlineData("Close", typeof(decimal))]
    [InlineData("TrailingPE", typeof(decimal))]
    [InlineData("Volume", typeof(long))]
    [InlineData("FullTimeEmployees", typeof(long))]
    [InlineData("ChangePercent", typeof(double))]
    [InlineData("Symbol", typeof(string))]
    [InlineData("Tag", typeof(string))]
    [InlineData("IsLong", typeof(bool))]
    [InlineData("LastUpdatedUtc", typeof(DateTimeOffset))]
    [InlineData("MetadataLastUpdated", typeof(DateTime))]
    public void TryGetFieldType_ReturnsTheUnderlyingTypeOfTheProperty(string field, Type expected)
    {
        Assert.True(TickerRow.TryGetFieldType(field, out Type actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("ROE", "ReturnOnEquity")]
    [InlineData("Trailing P/E", "TrailingPE")]
    [InlineData("Change %", "ChangePercent")]
    [InlineData("52W High", "FiftyTwoWeekHigh")]
    public void TryGetFieldType_AnAliasHasTheTypeOfItsTarget(string alias, string target)
    {
        Assert.True(TickerRow.TryGetFieldType(alias, out Type aliasType));
        Assert.True(TickerRow.TryGetFieldType(target, out Type targetType));
        Assert.Equal(targetType, aliasType);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("CLOSE")]
    [InlineData("trailing p/e")]
    public void TryGetFieldType_IsCaseInsensitive(string field)
    {
        Assert.True(TickerRow.TryGetFieldType(field, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    [InlineData("NoSuchColumn")]
    public void TryGetFieldType_IsFalseForAnUnknownName(string? field)
    {
        Assert.False(TickerRow.TryGetFieldType(field, out Type type));
        Assert.Equal(typeof(object), type);
    }

    [Fact]
    public void TryGetFieldType_AgreesWithIsKnownFieldForEveryListedName()
    {
        Assert.All(TickerRow.KnownFieldNames, name =>
        {
            Assert.True(TickerRow.IsKnownField(name), name);
            Assert.True(TickerRow.TryGetFieldType(name, out _), name);
        });
    }

    [Fact]
    public void KnownFieldNames_ListsPropertiesAndAliasesOnceInOrdinalOrder()
    {
        var names = TickerRow.KnownFieldNames;

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Close", names);
        Assert.Contains("ReturnOnEquity", names);
        Assert.Contains("ROE", names);
        Assert.Contains("Trailing P/E", names);
        var propertyCount = typeof(TickerRow).GetProperties().Length;
        Assert.True(names.Count > propertyCount, "aliases must be listed in addition to the properties");
    }

    [Fact]
    public void TheNewMembersDoNotChangeWhatTheExistingLookupsAnswer()
    {
        Assert.True(TickerRow.IsKnownField("Close"));
        Assert.True(TickerRow.IsKnownField("Trailing P/E"));
        Assert.False(TickerRow.IsKnownField("NoSuchColumn"));
        Assert.True(TickerRow.RequiresCandles("Close"));
        Assert.False(TickerRow.RequiresCandles("Sector"));
        Assert.Equal(12.5m, new TickerRow { Close = 12.5m }.GetPropertyValue("Close"));
    }
}
