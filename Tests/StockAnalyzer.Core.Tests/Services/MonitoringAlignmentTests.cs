#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The one rule that decides whether evidence describes the bars the chart accepted (real CandleData, no doubles).</summary>
public sealed class MonitoringAlignmentTests
{
    private static List<CandleData> Bars() => Enumerable.Range(0, 5)
        .Select(i => new CandleData(new DateTime(2026, 9, 24).AddDays(i), 100 + i, 105 + i, 95 + i, 102 + i, 1000 + i)).ToList();

    [Fact]
    public void IdenticalBars_AndASubsetOfTheSource_Match()
    {
        var source = Bars();
        Assert.True(MonitoringAlignment.Matches(source, source));
        Assert.True(MonitoringAlignment.Matches(source.Skip(2).ToList(), source));                // the chart may show fewer bars
        Assert.True(MonitoringAlignment.Matches([], source));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("close")]
    [InlineData("volume")]
    [InlineData("missing")]
    [InlineData("shifted")]
    public void AnyDifferenceOrAMissingBar_DoesNotMatch(string defect)
    {
        var source = Bars();
        var chart = Bars();
        var bar = chart[3];
        chart[3] = defect switch
        {
            "open" => bar with { Open = bar.Open + 0.01m },
            "high" => bar with { High = bar.High + 0.01m },
            "low" => bar with { Low = bar.Low - 0.01m },
            "close" => bar with { Close = bar.Close + 0.01m },
            "volume" => bar with { Volume = bar.Volume + 1 },
            "shifted" => bar with { Timestamp = bar.Timestamp.AddHours(1) },
            _ => bar,
        };
        if (defect == "missing") source.RemoveAt(3);
        Assert.False(MonitoringAlignment.Matches(chart, source));
    }

    [Fact]
    public void KindIsIgnored_OnlyTheUtcInstantMatters()
    {
        var source = Bars();
        var chart = source.Select(c => c with { Timestamp = DateTime.SpecifyKind(c.Timestamp, DateTimeKind.Unspecified) }).ToList();
        Assert.True(MonitoringAlignment.Matches(chart, source));
    }
}
