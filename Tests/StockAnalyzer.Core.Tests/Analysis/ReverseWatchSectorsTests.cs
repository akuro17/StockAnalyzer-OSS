using System.Linq;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Services.Analysis;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// The sector-to-phase assignment of the Reverse Watch plane. The table is the one the curve service has always produced (and the chart
/// renderer coloured by): East covers [337.5, 22.5), then 45-degree sectors counter-clockwise. Pinned here so that moving the definition
/// cannot change a phase or a colour.
/// </summary>
public sealed class ReverseWatchSectorsTests
{
    [Theory]
    [InlineData(0.0, ReverseWatchPhase.Phase2)]      // East
    [InlineData(22.4, ReverseWatchPhase.Phase2)]     // last angle of East
    [InlineData(22.5, ReverseWatchPhase.Phase3)]     // first angle of North-East
    [InlineData(45.0, ReverseWatchPhase.Phase3)]     // North-East
    [InlineData(67.4, ReverseWatchPhase.Phase3)]
    [InlineData(67.5, ReverseWatchPhase.Phase4)]     // North
    [InlineData(90.0, ReverseWatchPhase.Phase4)]
    [InlineData(112.5, ReverseWatchPhase.Phase5)]    // North-West
    [InlineData(157.5, ReverseWatchPhase.Phase6)]    // West
    [InlineData(202.5, ReverseWatchPhase.Phase7)]    // South-West
    [InlineData(247.5, ReverseWatchPhase.Phase8)]    // South
    [InlineData(292.5, ReverseWatchPhase.Phase1)]    // South-East
    [InlineData(337.4, ReverseWatchPhase.Phase1)]
    [InlineData(337.5, ReverseWatchPhase.Phase2)]    // East again, across the wrap
    [InlineData(359.9, ReverseWatchPhase.Phase2)]
    public void EveryBoundaryAngleFallsInTheSectorTheServiceAssignedBefore(double degrees, ReverseWatchPhase expected)
    {
        Assert.Equal(expected, ReverseWatchSectors.PhaseAtAngle(degrees));
    }

    [Theory]
    [InlineData(-22.5, ReverseWatchPhase.Phase2)]    // the same angle as 337.5
    [InlineData(-90.0, ReverseWatchPhase.Phase8)]    // the same angle as 270
    [InlineData(-180.0, ReverseWatchPhase.Phase6)]   // the same angle as 180
    [InlineData(360.0, ReverseWatchPhase.Phase2)]    // the same angle as 0
    [InlineData(720.0, ReverseWatchPhase.Phase2)]
    public void AnglesOutsideOneTurnAreReducedToTheirSector(double degrees, ReverseWatchPhase expected)
    {
        Assert.Equal(expected, ReverseWatchSectors.PhaseAtAngle(degrees));
    }

    [Fact]
    public void TheCentreOfEachSectorGivesADistinctPhaseAndNoneIsEverReturned()
    {
        ReverseWatchPhase[] phases = Enumerable.Range(0, 8)
            .Select(sector => ReverseWatchSectors.PhaseAtAngle(sector * ReverseWatchSectors.SectorWidthDegrees))
            .ToArray();

        Assert.Equal(8, phases.Distinct().Count());
        Assert.DoesNotContain(ReverseWatchPhase.None, phases);
        Assert.Equal(ReverseWatchPhase.Phase2, phases[0]);
        Assert.Equal(ReverseWatchPhase.Phase1, phases[7]);
    }
}
