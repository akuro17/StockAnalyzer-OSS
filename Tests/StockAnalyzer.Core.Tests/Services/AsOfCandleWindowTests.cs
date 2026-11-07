using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Screener;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;
using static StockAnalyzer.Core.Tests.TestHelpers.ScreenerSideFrameFixtures;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// <see cref="AsOfCandleWindow"/>: the bars known on a date. <c>Build</c> is the unchanged definition; <c>CutAt</c> + <c>BuildFromCut</c> are its two halves, so a caller that builds several time frames
/// of one symbol cuts the daily history once. The bar set of the Screener (<see cref="ScreenerBarsDataService.LoadBarSetAsync"/>) uses them and must serve exactly what <c>Build</c> gives per frame.
/// </summary>
public sealed class AsOfCandleWindowTests
{
    private static readonly TimeFrame[] Frames = { TimeFrame.D1, TimeFrame.W1, TimeFrame.MN1 };

    /// <summary>One bar per calendar day from Monday 2024-01-01 (see <see cref="ScreenerSideFrameFixtures.Daily"/>): 100 days end on Wednesday 2024-04-09.</summary>
    private static readonly List<CandleData> Daily100 = Daily(100);

    public static TheoryData<string> Dates => new() { "2023-12-31", "2024-01-01", "2024-01-17", "2024-01-31", "2024-02-29", "2024-04-09", "2999-01-01" };

    [Fact]
    public void CutAtReturnsTheSameListWhenTheDateCutsNothing()
    {
        Assert.Same(Daily100, AsOfCandleWindow.CutAt(Daily100, new DateOnly(2024, 4, 9)));
        Assert.Same(Daily100, AsOfCandleWindow.CutAt(Daily100, DateOnly.MaxValue));
    }

    [Fact]
    public void CutAtKeepsTheDaysOnOrBeforeTheDateInOrder()
    {
        IReadOnlyList<CandleData> cut = AsOfCandleWindow.CutAt(Daily100, new DateOnly(2024, 1, 17));

        Assert.Equal(Daily100.Take(17).ToList(), cut);
        Assert.NotSame(Daily100, cut);
        Assert.Equal(100, Daily100.Count); // the input is not changed
    }

    [Fact]
    public void CutAtIsEmptyWhenEveryDayIsLater()
    {
        Assert.Empty(AsOfCandleWindow.CutAt(Daily100, new DateOnly(2023, 12, 31)));
    }

    [Theory]
    [MemberData(nameof(Dates))]
    public void BuildIsCutAtThenBuildFromCutForEveryFrameAndCount(string date)
    {
        DateOnly endDate = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);
        foreach (TimeFrame frame in Frames)
        {
            foreach (int count in new[] { 0, 1, 5, 1000 })
            {
                IReadOnlyList<CandleData> built = AsOfCandleWindow.Build(Daily100, endDate, frame, count);
                IReadOnlyList<CandleData> halves = AsOfCandleWindow.BuildFromCut(AsOfCandleWindow.CutAt(Daily100, endDate), frame, count);

                Assert.Equal(built, halves);
            }
        }
    }

    [Fact]
    public void BuildGivesTheHandComputedBarsAtADateInTheMiddleOfAWeekAndAMonth()
    {
        // Wednesday 2024-01-17 is day index 16. Each day i has Open = Close = 100 + i, High = 101 + i, Low = 99 + i, Volume 1000 (see Daily).
        var endDate = new DateOnly(2024, 1, 17);

        IReadOnlyList<CandleData> weekly = AsOfCandleWindow.Build(Daily100, endDate, TimeFrame.W1, 0);
        IReadOnlyList<CandleData> monthly = AsOfCandleWindow.Build(Daily100, endDate, TimeFrame.MN1, 0);
        IReadOnlyList<CandleData> latestTwoWeeks = AsOfCandleWindow.Build(Daily100, endDate, TimeFrame.W1, 2);

        Assert.Equal(new[] { new DateTime(2024, 1, 1), new DateTime(2024, 1, 8), new DateTime(2024, 1, 15) }, weekly.Select(bar => bar.Timestamp).ToArray());
        CandleData partialWeek = weekly[^1]; // days 14, 15, 16
        Assert.Equal((114m, 117m, 113m, 116m, 3000L), (partialWeek.Open, partialWeek.High, partialWeek.Low, partialWeek.Close, partialWeek.Volume));
        CandleData january = Assert.Single(monthly); // days 0..16
        Assert.Equal((new DateTime(2024, 1, 1), 100m, 117m, 99m, 116m, 17000L), (january.Timestamp, january.Open, january.High, january.Low, january.Close, january.Volume));
        Assert.Equal(weekly.Skip(1).ToList(), latestTwoWeeks);
    }

    [Fact]
    public void AWeekThatStraddlesTheDateHoldsOnlyTheDaysUpToIt()
    {
        // Wednesday 2024-01-17: the week of Monday 01-15 holds 15, 16 and 17 only.
        IReadOnlyList<CandleData> weekly = AsOfCandleWindow.BuildFromCut(AsOfCandleWindow.CutAt(Daily100, new DateOnly(2024, 1, 17)), TimeFrame.W1, 0);

        CandleData last = weekly[^1];
        Assert.Equal(new DateTime(2024, 1, 15), last.Timestamp);
        Assert.Equal(3 * 1000L, last.Volume);
        Assert.Equal(100m + 16, last.Close); // the close of day index 16 (2024-01-17)
    }

    [Theory]
    [MemberData(nameof(Dates))]
    public async Task TheScreenerBarSetServesWhatBuildGivesForEveryFrame(string date)
    {
        DateOnly asOf = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture);
        var data = new RecordingDataService(new Dictionary<string, List<CandleData>> { ["A"] = Daily100 });
        var service = new ScreenerBarsDataService(data, new ScreenerBarSelection(asOf, false)); // the partial last period is kept, so the frames are exactly Build's

        IScreenerBarSet set = await service.LoadBarSetAsync("A", Frames, 0);

        foreach (TimeFrame frame in Frames)
        {
            Assert.Equal(AsOfCandleWindow.Build(Daily100, asOf, frame, 0), set.Get(frame));
        }
    }
}
