using System;
using System.IO;
using System.Linq;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class RlScalerRangeCheckTests
{
    private static readonly string[] Names = { "lag0:open", "lag0:high", "lag0:low", "lag0:close", "lag0:volume" };

    /// <summary>A scaler whose trained range is [Min, Max] = [10 * (channel + 1), 20 * (channel + 1)] per channel.</summary>
    private static FixedScaler Scaler(int window = 4)
        => new(FixedScaler.CurrentSchemaVersion, RlFiles.FeatureMode, window, Array.Empty<int>(), null, Names,
            Enumerable.Range(0, Names.Length).Select(c => new FixedScalerStatistic(15d * (c + 1), 1d, 10d * (c + 1), 20d * (c + 1))).ToArray(), "hash");

    private static double[] Rows(int window, Func<int, int, double> value)
        => Enumerable.Range(0, window * Names.Length).Select(i => value(i / Names.Length, i % Names.Length)).ToArray();

    [Fact]
    public void ValuesInsideTheTrainedRangeGiveNoFinding()
        => Assert.Empty(RlScalerRangeCheck.Find(Scaler(), Rows(4, (_, c) => 15d * (c + 1))));

    [Fact]
    public void ValuesEqualToTheTrainedMinimumOrMaximumGiveNoFinding()
    {
        var values = Rows(4, (row, c) => row % 2 == 0 ? 10d * (c + 1) : 20d * (c + 1));
        Assert.Empty(RlScalerRangeCheck.Find(Scaler(), values));
    }

    [Fact]
    public void OneChannelBelowTheMinimumIsReportedWithItsIdentityAndRanges()
    {
        var values = Rows(4, (row, c) => c == 2 && row == 3 ? 29.5d : 15d * (c + 1));
        var finding = Assert.Single(RlScalerRangeCheck.Find(Scaler(), values));
        // channel 2 trained range is [30, 60]; the lowest window value is 29.5
        Assert.Equal(new RlScalerRangeFinding("lag0:low", 30d, 60d, 29.5d, 45d), finding);
    }

    [Fact]
    public void OneChannelAboveTheMaximumIsReported()
    {
        var values = Rows(4, (row, c) => c == 4 && row == 0 ? 100.5d : 15d * (c + 1));
        var finding = Assert.Single(RlScalerRangeCheck.Find(Scaler(), values));
        Assert.Equal(new RlScalerRangeFinding("lag0:volume", 50d, 100d, 75d, 100.5d), finding);
    }

    [Fact]
    public void AllChannelsOutsideAreReportedInChannelOrder()
    {
        var values = Rows(4, (row, c) => row == 0 ? 0d : 1_000d);
        var findings = RlScalerRangeCheck.Find(Scaler(), values);
        Assert.Equal(Names, findings.Select(f => f.Channel));
        Assert.All(findings, f => { Assert.Equal(0d, f.WindowMin); Assert.Equal(1_000d, f.WindowMax); });
    }

    [Fact]
    public void RowsThatAreNotWholeRowsOrFewerThanOneWindowAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => RlScalerRangeCheck.Find(Scaler(), new double[Names.Length * 4 - 1]));
        Assert.Throws<InvalidDataException>(() => RlScalerRangeCheck.Find(Scaler(), new double[Names.Length * 3]));
        Assert.Throws<InvalidDataException>(() => RlScalerRangeCheck.Find(Scaler(), new double[Names.Length * 4 + 1]));
        Assert.Throws<InvalidDataException>(() => RlScalerRangeCheck.Find(Scaler(), ReadOnlySpan<double>.Empty));
    }

    [Fact]
    public void MoreRowsThanOneWindowReportTheMinimumAndMaximumOfAllRows()
    {
        // Rows 0..5 with a window of 4: the out-of-range values sit in the first and in the last row, outside any single 4-row window of the others.
        var values = Rows(6, (row, c) => c == 2 && row == 0 ? 25d : c == 2 && row == 5 ? 61d : 15d * (c + 1));
        var finding = Assert.Single(RlScalerRangeCheck.Find(Scaler(), values));
        Assert.Equal(new RlScalerRangeFinding("lag0:low", 30d, 60d, 25d, 61d), finding);
    }

    [Fact]
    public void ExactlyOneWindowGivesTheSameResultAsBefore()
    {
        var values = Rows(4, (row, c) => c == 4 && row == 0 ? 100.5d : 15d * (c + 1));
        Assert.Equal(new RlScalerRangeFinding("lag0:volume", 50d, 100d, 75d, 100.5d), Assert.Single(RlScalerRangeCheck.Find(Scaler(), values)));
    }
}
