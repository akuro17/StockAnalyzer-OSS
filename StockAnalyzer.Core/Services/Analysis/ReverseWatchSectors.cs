using StockAnalyzer.Core.Models.Analysis;

namespace StockAnalyzer.Core.Services.Analysis;

/// <summary>
/// The sector of the Reverse Watch plane that an angle falls in, which is the phase of a curve point. The angle is
/// atan2(normalized price, normalized volume) in degrees: 0 = East (volume high), 90 = North (price high), counter-clockwise.
/// The eight sectors are 45 degrees wide and centred on the axes, so East covers [337.5, 22.5). This is the only definition of
/// the sector-to-phase assignment: the curve service uses it for the phases it reports, and the chart renderer uses it for the colours.
/// </summary>
public static class ReverseWatchSectors
{
    /// <summary>Width of one sector in degrees.</summary>
    public const double SectorWidthDegrees = 45.0;

    private const double FullTurnDegrees = 360.0;
    private const double HalfSectorDegrees = SectorWidthDegrees / 2.0;

    /// <summary>The phase of each sector, indexed by the sector number counted counter-clockwise from East.</summary>
    private static readonly ReverseWatchPhase[] PhaseBySector =
    {
        ReverseWatchPhase.Phase2, // 0: East
        ReverseWatchPhase.Phase3, // 1: North-East
        ReverseWatchPhase.Phase4, // 2: North
        ReverseWatchPhase.Phase5, // 3: North-West
        ReverseWatchPhase.Phase6, // 4: West
        ReverseWatchPhase.Phase7, // 5: South-West
        ReverseWatchPhase.Phase8, // 6: South
        ReverseWatchPhase.Phase1, // 7: South-East
    };

    /// <summary>The phase of a point whose angle is <paramref name="degrees"/>; any finite angle is accepted and is reduced to one turn first.</summary>
    public static ReverseWatchPhase PhaseAtAngle(double degrees)
    {
        double shifted = (degrees + HalfSectorDegrees) % FullTurnDegrees;
        if (shifted < 0)
        {
            shifted += FullTurnDegrees;
        }

        return PhaseBySector[(int)(shifted / SectorWidthDegrees) % PhaseBySector.Length];
    }
}
