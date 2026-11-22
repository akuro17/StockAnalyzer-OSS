using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>PointOfControlSelector.FindDrawableIndex: the one rule for which bin is painted as the Point of Control.</summary>
public class PointOfControlSelectorTests
{
    private static List<VolumeBin> Bins(params long[] weights)
    {
        var list = new List<VolumeBin>(weights.Length);
        for (int i = 0; i < weights.Length; i++)
        {
            list.Add(new VolumeBin { LowerBound = 10m * i, UpperBound = 10m * (i + 1), Price = 10m * i + 5m, TotalVolume = weights[i] });
        }

        return list;
    }

    [Fact]
    public void NullAndEmpty_ReturnMinusOne()
    {
        Assert.Equal(-1, PointOfControlSelector.FindDrawableIndex(null));
        Assert.Equal(-1, PointOfControlSelector.FindDrawableIndex(new List<VolumeBin>()));
    }

    [Fact]
    public void AllWeightsZero_ReturnsMinusOne_WhereTheExistingFunctionReturnsIndexZero()
    {
        var bins = Bins(0, 0, 0);

        Assert.Equal(0, VolumeAnalysis.FindPointOfControlIndex(bins)); // the existing, unchanged behaviour the helper guards against
        Assert.Equal(-1, PointOfControlSelector.FindDrawableIndex(bins));
    }

    [Fact]
    public void SingleBinWithWeight_ReturnsZero_AndSingleBinWithoutWeight_ReturnsMinusOne()
    {
        Assert.Equal(0, PointOfControlSelector.FindDrawableIndex(Bins(5)));
        Assert.Equal(-1, PointOfControlSelector.FindDrawableIndex(Bins(0)));
    }

    [Fact]
    public void TieAtTheMaximum_ReturnsTheLowestIndex()
    {
        Assert.Equal(1, PointOfControlSelector.FindDrawableIndex(Bins(3, 9, 9, 2, 9)));
    }

    [Theory]
    [InlineData(new long[] { 1, 2, 3 })]
    [InlineData(new long[] { 0, 7, 0, 7 })]
    [InlineData(new long[] { 5, 4, 3, 2, 1 })]
    [InlineData(new long[] { 0, 0, 1 })]
    public void WhenTheMaximumIsPositive_EqualsFindPointOfControlIndex(long[] weights)
    {
        var bins = Bins(weights);

        Assert.Equal(VolumeAnalysis.FindPointOfControlIndex(bins), PointOfControlSelector.FindDrawableIndex(bins));
    }
}
