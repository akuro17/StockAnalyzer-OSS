using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Bin ranking (VolumeProfileRanking.Rank): order, tie rule, shares, boundaries, and agreement with the existing Point of Control rule.
/// The existing profile / POC / Value Area functions are not changed; they are only the reference here.
/// </summary>
public class VolumeProfileRankingTests
{
    /// <summary>Bins with price 10, 20, ... (width 10) and the given weights; index 0 = lowest price.</summary>
    private static List<VolumeBin> Bins(params long[] weights)
    {
        var list = new List<VolumeBin>(weights.Length);
        for (int i = 0; i < weights.Length; i++)
        {
            decimal lower = 10m * i;
            decimal upper = 10m * (i + 1);
            list.Add(new VolumeBin { LowerBound = lower, UpperBound = upper, Price = (lower + upper) / 2, TotalVolume = weights[i] });
        }

        return list;
    }

    public static IEnumerable<object[]> ProfileSettings()
    {
        yield return new object[] { 20, 10, VolumeDistributionMode.Proportional };
        yield return new object[] { 50, 24, VolumeDistributionMode.Full };
        yield return new object[] { 200, 50, VolumeDistributionMode.Proportional };
        yield return new object[] { 30, 17, VolumeDistributionMode.Proportional };
    }

    // ---- order, ranks and shares ----

    [Fact]
    public void Rank_OrdersByWeightDescending_AndAssignsUniqueOrdinalRanks()
    {
        var bins = Bins(10, 50, 30, 40);

        var rows = VolumeProfileRanking.Rank(bins);

        Assert.Equal(new[] { 1, 3, 2, 0 }, rows.Select(r => r.BinIndex).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.Rank).ToArray());
        Assert.Equal(new long[] { 50, 40, 30, 10 }, rows.Select(r => r.Volume).ToArray());
        Assert.Equal(bins[1].Price, rows[0].Price);
        Assert.Equal(bins[1].LowerBound, rows[0].LowerBound);
        Assert.Equal(bins[1].UpperBound, rows[0].UpperBound);
    }

    [Fact]
    public void Rank_TiedWeights_LowerPriceFirst_AndRanksStayUnique()
    {
        // Weights 50, 30, 30, 10 with the tie at indices 1 and 2 (and a second tie of 50 at index 3).
        var rows = VolumeProfileRanking.Rank(Bins(30, 50, 30, 50));

        Assert.Equal(new[] { 1, 3, 0, 2 }, rows.Select(r => r.BinIndex).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, rows.Select(r => r.Rank).ToArray());
    }

    [Fact]
    public void Rank_SharesAndCumulativeSharesAreExactFractionsOfTheTotal()
    {
        var rows = VolumeProfileRanking.Rank(Bins(10, 50, 30, 10));

        Assert.Equal(0.5m, rows[0].Share);
        Assert.Equal(0.3m, rows[1].Share);
        Assert.Equal(0.1m, rows[2].Share);
        Assert.Equal(0.1m, rows[3].Share);
        Assert.Equal(0.5m, rows[0].CumulativeShare);
        Assert.Equal(0.8m, rows[1].CumulativeShare);
        Assert.Equal(0.9m, rows[2].CumulativeShare);
        Assert.Equal(1m, rows[3].CumulativeShare);
    }

    [Fact]
    public void Rank_ThreeEqualBins_AreNotRoundedInCore_AndTheLastCumulativeShareIsExactlyOne()
    {
        // I8: 1/3 each. Core never rounds, and the cumulative share comes from the exact running volume, not from summed shares.
        var rows = VolumeProfileRanking.Rank(Bins(7, 7, 7));

        decimal third = 7m / 21m;
        Assert.All(rows, r => Assert.Equal(third, r.Share));
        Assert.Equal(7m / 21m, rows[0].CumulativeShare);
        Assert.Equal(14m / 21m, rows[1].CumulativeShare);
        Assert.Equal(1m, rows[2].CumulativeShare);
    }

    [Fact]
    public void Rank_WithLimit_LastReturnedCumulativeShareIsBelowOne_AndShareStillUsesTheTotalOfAllBins()
    {
        // I9
        var rows = VolumeProfileRanking.Rank(Bins(10, 50, 30, 10), limit: 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal(0.5m, rows[0].Share);          // 50 / 100, not 50 / 80
        Assert.Equal(0.3m, rows[1].Share);
        Assert.Equal(0.8m, rows[1].CumulativeShare);
        Assert.True(rows[1].CumulativeShare < 1m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(1000)]
    public void Rank_LimitOmittedOrAtLeastTheRowCount_ReturnsAllRows_AndTheLastCumulativeShareIsOne(int? limit)
    {
        var rows = VolumeProfileRanking.Rank(Bins(10, 50, 30, 10), limit);

        Assert.Equal(4, rows.Count);
        Assert.Equal(1m, rows[^1].CumulativeShare);
    }

    [Fact]
    public void Rank_LimitOne_ReturnsOnlyThePointOfControlRow()
    {
        var rows = VolumeProfileRanking.Rank(Bins(10, 50, 30, 10), limit: 1);

        Assert.Single(rows);
        Assert.Equal(1, rows[0].BinIndex);
        Assert.Equal(1, rows[0].Rank);
    }

    // ---- boundaries ----

    [Fact]
    public void Rank_NullAndEmpty_ReturnEmpty()
    {
        Assert.Empty(VolumeProfileRanking.Rank(null));
        Assert.Empty(VolumeProfileRanking.Rank(new List<VolumeBin>()));
        Assert.Empty(VolumeProfileRanking.Rank(null, limit: 3));
    }

    [Fact]
    public void Rank_SingleBin_IsRankOneWithShareOne()
    {
        var rows = VolumeProfileRanking.Rank(Bins(42));

        var row = Assert.Single(rows);
        Assert.Equal(1, row.Rank);
        Assert.Equal(0, row.BinIndex);
        Assert.Equal(1m, row.Share);
        Assert.Equal(1m, row.CumulativeShare);
    }

    [Fact]
    public void Rank_AllWeightsZero_ReturnsEmpty()
    {
        Assert.Empty(VolumeProfileRanking.Rank(Bins(0, 0, 0)));
    }

    [Fact]
    public void Rank_ZeroWeightBins_AreNotRanked_AndTheirIndexesAreKept()
    {
        var rows = VolumeProfileRanking.Rank(Bins(0, 20, 0, 80));

        Assert.Equal(new[] { 3, 1 }, rows.Select(r => r.BinIndex).ToArray());
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Rank).ToArray());
        Assert.Equal(1m, rows[^1].CumulativeShare);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rank_LimitBelowOne_Throws(int limit)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => VolumeProfileRanking.Rank(Bins(1, 2), limit));
        Assert.Equal("limit", ex.ParamName);
        // The limit is checked before the input, so an empty input does not hide a bad limit.
        Assert.Throws<ArgumentOutOfRangeException>(() => VolumeProfileRanking.Rank(null, limit));
    }

    [Fact]
    public void Rank_NegativeWeight_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => VolumeProfileRanking.Rank(Bins(5, -1, 7)));
        Assert.Equal("bins", ex.ParamName);
    }

    [Fact]
    public void Rank_FullModeVolumesBeyondTheLongRange_DoNotThrow_AndKeepExactShares()
    {
        // I7: ten bins of long.MaxValue / 4 each: the total (about 2.3e19 x 10) exceeds long.MaxValue; the decimal total stays exact.
        long big = long.MaxValue / 4;
        var rows = VolumeProfileRanking.Rank(Bins(Enumerable.Repeat(big, 10).ToArray()));

        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.Equal(0.1m, r.Share));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, rows.Select(r => r.BinIndex).ToArray());
        Assert.Equal(1m, rows[^1].CumulativeShare);
    }

    [Fact]
    public void Rank_DoesNotModifyTheInput()
    {
        // I5
        var bins = Bins(10, 50, 30, 40);
        var before = bins.Select(b => (b.LowerBound, b.UpperBound, b.Price, b.TotalVolume, b.BuyVolume, b.SellVolume, b.WidthPercent)).ToArray();
        var firstReference = bins[0];

        _ = VolumeProfileRanking.Rank(bins, 2);

        Assert.Equal(4, bins.Count);
        Assert.Same(firstReference, bins[0]);
        Assert.Equal(before, bins.Select(b => (b.LowerBound, b.UpperBound, b.Price, b.TotalVolume, b.BuyVolume, b.SellVolume, b.WidthPercent)).ToArray());
    }

    // ---- invariants against the existing profile functions ----

    [Theory]
    [MemberData(nameof(ProfileSettings))]
    public void Rank_VolumeProfile_FirstRowIsThePointOfControl_AndTheOtherInvariantsHold(int period, int rowCount, VolumeDistributionMode mode)
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        int compared = 0;

        for (int end = period - 1; end < candles.Count; end += 3)
        {
            var window = candles.Skip(end - period + 1).Take(period).ToList();
            var bins = VolumeAnalysis.CalculateProfile(window, rowCount, mode);
            AssertRankingInvariants(bins);
            compared++;
        }

        Assert.True(compared > 10);
    }

    [Theory]
    [MemberData(nameof(ProfileSettings))]
    public void Rank_TimeAtPrice_FirstRowIsThePointOfControl_AndTheOtherInvariantsHold(int period, int rowCount, VolumeDistributionMode _)
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        int compared = 0;

        for (int end = period - 1; end < candles.Count; end += 3)
        {
            var window = candles.Skip(end - period + 1).Take(period).ToList();
            var bins = TimeAtPriceAnalysis.CalculateProfile(window, rowCount, PriceType.Close);
            AssertRankingInvariants(bins);
            compared++;
        }

        Assert.True(compared > 10);
    }

    private static void AssertRankingInvariants(IReadOnlyList<VolumeBin> bins)
    {
        var rows = VolumeProfileRanking.Rank(bins);
        long total = bins.Sum(b => b.TotalVolume);

        if (total == 0)
        {
            Assert.Empty(rows);
            return;
        }

        // I1: row 1 is the Point of Control by the existing rule (first maximum, lowest price).
        Assert.Equal(VolumeAnalysis.FindPointOfControlIndex(bins), rows[0].BinIndex);
        Assert.Equal(VolumeAnalysis.FindPointOfControl(bins)!.Price, rows[0].Price);

        // I3: every weight appears once, the volumes add up to the profile, the last cumulative share is exactly 1.
        Assert.Equal(total, rows.Sum(r => r.Volume));
        Assert.Equal(bins.Count(b => b.TotalVolume > 0), rows.Count);
        Assert.Equal(1m, rows[^1].CumulativeShare);

        // I4: weights never increase; equal weights keep ascending index.
        for (int i = 1; i < rows.Count; i++)
        {
            Assert.True(rows[i - 1].Volume >= rows[i].Volume, $"row {i}");
            if (rows[i - 1].Volume == rows[i].Volume)
            {
                Assert.True(rows[i - 1].BinIndex < rows[i].BinIndex, $"tie at row {i}");
            }

            Assert.True(rows[i - 1].CumulativeShare < rows[i].CumulativeShare, $"cumulative at row {i}");
        }

        // I6: the ranks are exactly 1..M.
        Assert.Equal(Enumerable.Range(1, rows.Count).ToArray(), rows.Select(r => r.Rank).ToArray());

        // Every row carries the bin it names.
        Assert.All(rows, r =>
        {
            Assert.Equal(bins[r.BinIndex].TotalVolume, r.Volume);
            Assert.Equal(bins[r.BinIndex].Price, r.Price);
            Assert.Equal(bins[r.BinIndex].LowerBound, r.LowerBound);
            Assert.Equal(bins[r.BinIndex].UpperBound, r.UpperBound);
        });
    }

    [Theory]
    [InlineData(10, 12, VolumeDistributionMode.Proportional)]
    [InlineData(25, 20, VolumeDistributionMode.Full)]
    [InlineData(0, 16, VolumeDistributionMode.Proportional)]
    public void Rank_VolumeProfileSegments_FirstRowPriceEqualsTheSegmentPoc(int period, int rowCount, VolumeDistributionMode mode)
    {
        // I2: the viewport calculator keeps its own POC loop; row 1 must agree with it for every visible window.
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        var parameter = new CoreVolumeProfileParameter { Period = period, RowCount = rowCount, Mode = mode };
        int checkedSegments = 0;

        for (int visibleStart = 0; visibleStart <= 200; visibleStart += 40)
        {
            var result = VolumeProfileViewportCalculator.Calculate("rank", candles, visibleStart, 60, TimeframeType.Daily, parameter);

            Assert.Equal(VolumeProfileResultStatus.Success, result.Status);
            foreach (var segment in result.Segments)
            {
                var rows = VolumeProfileRanking.Rank(segment.Bins);
                Assert.True(segment.POC.HasValue);
                Assert.Equal(segment.POC!.Value, rows[0].Price);
                checkedSegments++;
            }
        }

        Assert.True(checkedSegments >= 6);
    }

    [Fact]
    public void Rank_TimeAtPriceSegments_FirstRowPriceEqualsTheSegmentTpoc()
    {
        var candles = VolumeAnalysisSpanTests.RandomCandles(300);
        var parameter = new CoreTimeAtPriceParameter { Period = 20, RowCount = 15 };
        int checkedSegments = 0;

        for (int visibleStart = 0; visibleStart <= 200; visibleStart += 40)
        {
            var result = TimeAtPriceViewportCalculator.Calculate("rank", candles, visibleStart, 60, TimeframeType.Daily, parameter);

            Assert.Equal(VolumeProfileResultStatus.Success, result.Status);
            foreach (var segment in result.Segments)
            {
                var rows = VolumeProfileRanking.Rank(segment.Bins);
                Assert.True(segment.POC.HasValue);
                Assert.Equal(segment.POC!.Value, rows[0].Price);
                checkedSegments++;
            }
        }

        Assert.True(checkedSegments >= 6);
    }

    [Fact]
    public void Rank_SegmentWithAllZeroVolume_IsEmptyAndTheSegmentPocIsNull()
    {
        // The calculator reports no POC when every bin weighs 0; the ranking is empty in the same case (never an arbitrary bin).
        var candles = new List<CoreCandleData>();
        for (int i = 0; i < 12; i++)
        {
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), 10m + i, 12m + i, 9m + i, 11m + i, 0));
        }

        var parameter = new CoreVolumeProfileParameter { Period = 0, RowCount = 8 };
        var result = VolumeProfileViewportCalculator.Calculate("zero", candles, 0, candles.Count, TimeframeType.Daily, parameter);

        var segment = Assert.Single(result.Segments);
        Assert.False(segment.POC.HasValue);
        Assert.Empty(VolumeProfileRanking.Rank(segment.Bins));
    }
}
