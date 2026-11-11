using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>Point of Control: the greatest TotalVolume; on a tie the FIRST (lowest-index) bin, as the stable OrderByDescending(...).FirstOrDefault() it replaced.</summary>
public class VolumeAnalysisPointOfControlTests
{
    private static List<VolumeBin> Bins(params long[] volumes)
    {
        var bins = new List<VolumeBin>();
        for (int i = 0; i < volumes.Length; i++)
        {
            bins.Add(new VolumeBin { Price = 100m + i, LowerBound = 100m + i - 0.5m, UpperBound = 100m + i + 0.5m, TotalVolume = volumes[i] });
        }
        return bins;
    }

    [Fact]
    public void NullOrEmpty_HasNoPointOfControl()
    {
        Assert.Equal(-1, VolumeAnalysis.FindPointOfControlIndex(null));
        Assert.Equal(-1, VolumeAnalysis.FindPointOfControlIndex(new List<VolumeBin>()));
        Assert.Null(VolumeAnalysis.FindPointOfControl(null));
        Assert.Null(VolumeAnalysis.FindPointOfControl(new List<VolumeBin>()));
    }

    [Theory]
    [InlineData(0L)]
    public void SingleBin_IsThePointOfControl(long volume)
    {
        Assert.Equal(0, VolumeAnalysis.FindPointOfControlIndex(Bins(volume)));
    }

    [Fact]
    public void TheGreatestVolumeWins_AnywhereInTheList()
    {
        Assert.Equal(1, VolumeAnalysis.FindPointOfControlIndex(Bins(5, 9, 3)));
        Assert.Equal(2, VolumeAnalysis.FindPointOfControlIndex(Bins(5, 9, 10)));
        Assert.Equal(0, VolumeAnalysis.FindPointOfControlIndex(Bins(10, 9, 3)));
    }

    [Fact]
    public void OnATie_TheFirstBinWins()
    {
        Assert.Equal(1, VolumeAnalysis.FindPointOfControlIndex(Bins(5, 9, 9, 9, 2)));
        Assert.Equal(0, VolumeAnalysis.FindPointOfControlIndex(Bins(0, 0, 0)));
    }

    [Fact]
    public void FindPointOfControl_ReturnsTheBinOfThatIndex()
    {
        var bins = Bins(1, 7, 7);
        Assert.Same(bins[1], VolumeAnalysis.FindPointOfControl(bins));
    }

    [Fact]
    public void TheSameRuleAsTheOrderByDescendingItReplaced_OnManyShapes()
    {
        var random = new System.Random(12345);
        for (int round = 0; round < 200; round++)
        {
            var volumes = new long[random.Next(1, 60)];
            for (int i = 0; i < volumes.Length; i++) volumes[i] = random.Next(0, 6);   // small range: many ties
            var bins = Bins(volumes);

            var expected = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OrderByDescending(bins, b => b.TotalVolume));
            Assert.Same(expected, VolumeAnalysis.FindPointOfControl(bins));
        }
    }
}
